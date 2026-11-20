using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 评审工作流。
///
/// **边界**：观复不提供 AI。这里给的是**骨架** —— 阶段、维度、检查清单、门禁、历史；
/// 判断由 agent（可以开子 agent / 专家团并行）来做，做完逐维度回写结论。
/// 观复负责的是「不评审完不许结项」和「每轮结论都留痕」。
///
/// 评审服务同时就是结项门禁的实现（见 <see cref="IReviewGate"/>）——
/// 注册一次，两个接口共用同一个实例。
/// </summary>
public interface IReviewService : IReviewGate
{
    /// <summary>开一轮评审。按固定的七个维度各建一条待办。</summary>
    Task<ReviewRoundDto> StartAsync(Guid taskId, string? reviewer = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前（或指定）轮次里，项目规约（DoD）的逐条判定。
    ///
    /// 没有判定的一律算「待判定」—— 人和 agent 看到的必须是同一份。
    /// </summary>
    Task<IReadOnlyList<SpecItemDto>> GetSpecItemsAsync(
        Guid taskId,
        int? round = null,
        CancellationToken cancellationToken = default);

    /// <summary>取评审包：维度清单、任务上下文和已登记的报告证据。</summary>
    Task<ReviewPackDto> GetPackAsync(Guid taskId, int? round = null, CancellationToken cancellationToken = default);

    Task<ReviewEvidenceSnapshot> RecordReportEvidenceAsync(
        Guid taskId, int round, int expectedVersion, ReviewReportEvidence evidence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 回写某个维度的结论。
    ///
    /// <paramref name="skipped"/> 为 true 表示这个维度**不适用**（跳过）——
    /// 枚举里一直有 Skipped，但以前没有任何入口能写出来，
    /// 于是「本次改动不涉及安全维度」这类情况只能硬编一个通过。
    ///
    /// 跳过有两条硬约束：必须在 <paramref name="findings"/> 里写明理由，
    /// 且每轮最多跳 <see cref="ReviewService.MaxSkippedPerRound"/> 个 ——
    /// 否则七个维度全标「不适用」就能结项。
    /// </summary>
    Task<ReviewItemDto> SubmitAsync(
        Guid taskId,
        string dimension,
        bool passed,
        string? findings = null,
        string? evidence = null,
        string? recommendation = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default,
        bool skipped = false);

    /// <summary>
    /// 回写**项目规约（DoD）某一条**的判定。
    ///
    /// 七个维度是通用的，规约是这个项目特有的 —— 评审时要一条一条对着判，
    /// 否则「全都满足」没法复核，也不值当尺子。
    /// 判定按**条目的归一化形式**存：规约改了文字，旧判定就不算数了。
    /// </summary>
    Task<SpecVerdictDto> SubmitSpecVerdictAsync(
        Guid taskId,
        string item,
        bool satisfied,
        string? note = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default,
        bool skipped = false);

    /// <summary>
    /// 认领某个维度的评审（多 agent 分工）。
    ///
    /// 一轮七个维度可以拆给几个 agent 各评各的 ——
    /// 认领就是「这个维度我在做，别重复」，带租约：到期还没结论别人可以接手。
    /// 别人持有（租约未过期）时拒绝，并说清是谁占着。
    /// </summary>
    Task<ReviewItemDto> ClaimDimensionAsync(
        Guid taskId,
        string dimension,
        string agentId,
        int leaseMinutes = 30,
        int? round = null,
        CancellationToken cancellationToken = default);

    /// <summary>认领某条完成定义（DoD）的判定。跟维度一个道理。</summary>
    Task<SpecVerdictDto> ClaimSpecItemAsync(
        Guid taskId,
        string item,
        string agentId,
        int leaseMinutes = 30,
        int? round = null,
        CancellationToken cancellationToken = default);

    /// <summary>列出全部历史轮次。</summary>
    Task<IReadOnlyList<ReviewRoundDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>当前轮（最新一轮）。</summary>
    Task<ReviewRoundDto?> GetCurrentAsync(Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>状态机用的门禁接口。单独拆出来是为了不让 TaskService 依赖整个评审服务。</summary>
public interface IReviewGate
{
    Task<ReviewGateDto> GetGateAsync(Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>评审没过，不许结项。</summary>
public class ReviewGateException(ReviewGateDto gate)
    : GuanfuException(BuildMessage(gate))
{
    public ReviewGateDto Gate { get; } = gate;

    private static string BuildMessage(ReviewGateDto gate)
    {
        var parts = new List<string>();

        if (gate.PendingDimensions.Count > 0)
        {
            parts.Add($"待评审: {string.Join("、", gate.PendingDimensions)}");
        }

        if (gate.FailedDimensions.Count > 0)
        {
            parts.Add($"不通过: {string.Join("、", gate.FailedDimensions)}");
        }

        if (gate.PendingSpecItems is { Count: > 0 })
        {
            parts.Add($"规约待判定: {string.Join("、", gate.PendingSpecItems)}");
        }

        if (gate.FailedSpecItems is { Count: > 0 })
        {
            parts.Add($"规约不满足: {string.Join("、", gate.FailedSpecItems)}");
        }

        if (gate.BlockReason is not null)
        {
            parts.Add(gate.BlockReason);
        }

        // 回写指引要说清是哪种回写 —— 维度回写与规约逐条判定是两条路。
        var how = gate.PendingSpecItems is { Count: > 0 }
            ? "到任务详情页的评审区逐维度回写结论、逐条回写规约判定（agent 走评审回写工具）"
            : "到任务详情页的评审区逐维度回写结论（agent 走评审回写工具）";

        return $"评审还没过（{string.Join("；", parts)}）。{how}，全部通过后才能结项。";
    }
}

public sealed class ReviewService(
    IAppDbContext db,
    IClock clock,
    IGitInspector git,
    IProjectSpecService? specs = null,
    ICurrentMemberScope? currentMember = null) : IReviewService, IReviewGate
{
    /// <summary>
    /// 每轮最多允许跳过几个维度。
    ///
    /// Skipped 是「这个维度不适用」，不是「这个维度懒得评」。
    /// 不设上限的话，七个维度全标 Skipped 就能结项，门禁形同虚设。
    /// </summary>
    public const int MaxSkippedPerRound = 2;

    public async Task<ReviewRoundDto> StartAsync(
        Guid taskId,
        string? reviewer = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.VisibleTo(currentMember?.Current).FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        // 轮次绑 commit：评的是「这一版代码」。开轮时把 HEAD 记下来，
        // 结项门禁才能判断「评完之后代码是不是又变了」。
        var commitHash = ReadHeadSha(project);

        // 轮次号可能撞车：并发开轮时两个请求会算出同一个号。
        // 唯一索引挡得住，撞了就重算一个再来。
        ReviewRound round = null!;

        for (var attempt = 0; ; attempt++)
        {
            var nextRound = await db.ReviewRounds
                .Where(r => r.TaskId == taskId)
                .Select(r => (int?)r.Round)
                .MaxAsync(cancellationToken) ?? 0;

            round = new ReviewRound
            {
                TaskId = taskId,
                Round = nextRound + 1,
                Verdict = ReviewVerdict.Pending,
                Reviewer = reviewer,
                CommitHash = commitHash,
                StartedAt = clock.UtcNow
            };

            var order = 0;
            foreach (var dimension in ReviewDimensions.All)
            {
                round.Items.Add(new ReviewItem
                {
                    TaskId = taskId,
                    Dimension = dimension.Key,
                    DimensionName = dimension.Name,
                    Order = order++,
                    Status = ReviewStatus.Pending
                });
            }

            db.ReviewRounds.Add(round);

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = taskId,
                // 这不是状态迁移 —— from/to 留空
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Agent,
                Actor = reviewer,
                Note = $"开始第 {round.Round} 轮评审（{round.Items.Count} 个维度）",
                EventType = TaskEventTypes.ReviewRoundOpened,
                Payload = GuanfuJson.Serialize(new
                {
                    round = round.Round,
                    dimensions = round.Items.Count,
                    commitHash,
                    reviewer
                }),
                CreatedAt = clock.UtcNow
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.ClearTracking();
            }
        }

        return ToDto(round);
    }

    public async Task<SpecVerdictDto> SubmitSpecVerdictAsync(
        Guid taskId,
        string item,
        bool satisfied,
        string? note = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default,
        bool skipped = false)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            throw new GuanfuException("规约条目不能为空。");
        }

        var current = await LoadRoundAsync(taskId, round, cancellationToken)
                      ?? throw new NotFoundException("评审轮次", round?.ToString() ?? taskId.ToString());

        // 条目必须是**当前规约**里的 —— 拿着别的轮次/别的项目的条目来判没有意义。
        var dod = await LoadDefinitionOfDoneAsync(current.TaskId, cancellationToken);
        var text = dod.FirstOrDefault(d => string.Equals(TaskKeys.Normalize(d), TaskKeys.Normalize(item), StringComparison.Ordinal))
                   ?? throw new GuanfuException(
                       $"「{item}」不在这个项目当前的完成定义里。" +
                       (dod.Count == 0 ? "（项目还没填完成定义）" : $"当前有：{string.Join("；", dod)}"));

        if (skipped && string.IsNullOrWhiteSpace(note))
        {
            throw new GuanfuException("判定为「不适用」必须写明理由。");
        }

        var key = TaskKeys.Normalize(text);

        var existing = current.SpecVerdicts
            .FirstOrDefault(v => string.Equals(v.ItemKey, key, StringComparison.Ordinal));

        var verdict = existing;

        if (verdict is null)
        {
            verdict = new SpecVerdict
            {
                ReviewRoundId = current.Id,
                TaskId = taskId,
                ItemKey = key,
                ItemText = text
            };
            current.SpecVerdicts.Add(verdict);
            db.SpecVerdicts.Add(verdict);
        }
        else
        {
            // 同一轮里改了结论 —— 记新的一条，旧的留着（账本不改写）。
            // 认领要带过去：认的是「这条」，不是「那一行记录」。
            var revised = new SpecVerdict
            {
                ReviewRoundId = current.Id,
                TaskId = taskId,
                ItemKey = key,
                ItemText = text,
                Status = skipped ? ReviewStatus.Skipped : (satisfied ? ReviewStatus.Passed : ReviewStatus.Failed),
                Note = note,
                Reviewer = reviewer ?? current.Reviewer,
                DecidedAt = clock.UtcNow,
                ClaimedBy = verdict.ClaimedBy,
                ClaimedAt = verdict.ClaimedAt,
                ClaimedUntil = verdict.ClaimedUntil
            };
            db.SpecVerdicts.Add(revised);
            verdict = revised;
        }

        var now = clock.UtcNow;

        if (!string.IsNullOrWhiteSpace(verdict.ClaimedBy) &&
            !string.Equals(verdict.ClaimedBy, reviewer, StringComparison.Ordinal) &&
            verdict.ClaimedUntil is DateTimeOffset claimedUntil &&
            claimedUntil > now)
        {
            throw new GuanfuException(
                $"「{text}」正被 {verdict.ClaimedBy} 判（到 {claimedUntil:u}）。" +
                "要么等它判完，要么让它放掉再来" +
                (string.IsNullOrWhiteSpace(reviewer) ? "（你就是它的话，提交时带上 reviewer=你的 id）" : "") + "。");
        }

        verdict.Status = skipped ? ReviewStatus.Skipped : (satisfied ? ReviewStatus.Passed : ReviewStatus.Failed);
        verdict.Note = note;
        verdict.Reviewer = reviewer ?? current.Reviewer;
        verdict.DecidedAt = now;

        // 判完了，认领收工
        verdict.ClaimedBy = null;
        verdict.ClaimedUntil = null;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = verdict.Reviewer,
            Note = $"规约判定[{text}] {(skipped ? "不适用" : (satisfied ? "满足" : "不满足"))}" +
                   (string.IsNullOrWhiteSpace(note) ? "" : $"：{note}"),
            EventType = TaskEventTypes.SpecVerdictResult,
            Payload = GuanfuJson.Serialize(new
            {
                round = current.Round,
                item = text,
                result = verdict.Status.ToString(),
                reviewer = verdict.Reviewer
            }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

        return ToSpecVerdictDto(verdict);
    }

    /// <summary>
    /// 这个任务**生效**的完成定义（逐条）：项目级的 + 它所在里程碑的。
    ///
    /// 走规约服务那一份（同一个口径）—— 评审判的与 agent 读的必须是同一份，
    /// 不能各算各的。
    /// </summary>
    /// <summary>
    /// 生效的完成定义，**带来源层级**（工作区 / 项目 / 里程碑）。
    ///
    /// 判的时候该知道这条是谁的要求 —— 里程碑特有的只对这个版本里的活成立。
    /// </summary>
    private async Task<IReadOnlyList<EffectiveDoDItem>> LoadDefinitionOfDoneWithSourceAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (specs is not null)
        {
            return await specs.GetEffectiveDefinitionOfDoneWithSourceAsync(taskId, cancellationToken);
        }

        // 没有规约服务时的退路：项目级的算项目级，里程碑级的算里程碑级
        var texts = await LoadDefinitionOfDoneAsync(taskId, cancellationToken);

        var task = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.MilestoneId })
            .FirstOrDefaultAsync(cancellationToken);

        var milestone = task?.MilestoneId is Guid milestoneId
            ? await db.MilestoneSpecs.AsNoTracking()
                .FirstOrDefaultAsync(s => s.MilestoneId == milestoneId, cancellationToken)
            : null;

        var items = new List<EffectiveDoDItem>();

        foreach (var text in texts)
        {
            var source = milestone?.DefinitionOfDone?.Contains(text, StringComparison.Ordinal) == true
                ? SpecSource.Milestone
                : SpecSource.Project;

            items.Add(new EffectiveDoDItem(text, source));
        }

        return items;
    }

    private async Task<IReadOnlyList<string>> LoadDefinitionOfDoneAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (specs is not null)
        {
            return await specs.GetEffectiveDefinitionOfDoneAsync(taskId, cancellationToken);
        }

        var task = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.MilestoneId })
            .FirstOrDefaultAsync(cancellationToken);

        if (task is null)
        {
            return [];
        }

        var project = await db.ProjectSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == task.ProjectId, cancellationToken);

        var milestone = task.MilestoneId is Guid milestoneId
            ? await db.MilestoneSpecs.AsNoTracking()
                .FirstOrDefaultAsync(s => s.MilestoneId == milestoneId, cancellationToken)
            : null;

        // 两层都算（项目级 + 里程碑级）—— 退路也得跟正路一个口径
        return SplitLines(project?.DefinitionOfDone)
            .Concat(SplitLines(milestone?.DefinitionOfDone))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>当前轮的规约判定：条目 + 结论（没判的算待判定）。</summary>
    private async Task<IReadOnlyList<SpecItemDto>> BuildSpecItemsAsync(
        ReviewRound current,
        CancellationToken cancellationToken)
    {
        var dod = await LoadDefinitionOfDoneWithSourceAsync(current.TaskId, cancellationToken);
        if (dod.Count == 0)
        {
            return [];
        }

        var verdicts = await db.SpecVerdicts.AsNoTracking()
            .Where(v => v.ReviewRoundId == current.Id)
            .ToListAsync(cancellationToken);

        var latest = verdicts
            .GroupBy(v => v.ItemKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(v => v.DecidedAt ?? DateTimeOffset.MinValue).First())
            .ToDictionary(v => v.ItemKey, v => v, StringComparer.Ordinal);

        return dod.Select(item =>
        {
            var key = TaskKeys.Normalize(item.Text);
            var verdict = latest.TryGetValue(key, out var found) ? found : null;

            return new SpecItemDto(
                key,
                item.Text,
                (verdict?.Status ?? ReviewStatus.Pending).ToString(),
                verdict?.Note,
                verdict?.Reviewer,
                verdict?.DecidedAt,
                verdict?.ClaimedBy,
                verdict?.ClaimedUntil,
                item.Source);
        }).ToList();
    }

    public async Task<IReadOnlyList<SpecItemDto>> GetSpecItemsAsync(
        Guid taskId,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        var current = await LoadRoundAsync(taskId, round, cancellationToken);
        if (current is null)
        {
            return [];
        }

        return await BuildSpecItemsAsync(current, cancellationToken);
    }

    public async Task<ReviewPackDto> GetPackAsync(
        Guid taskId,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        var current = await LoadRoundAsync(taskId, round, cancellationToken)
                      ?? throw new NotFoundException("评审轮次", round?.ToString() ?? taskId.ToString());

        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        var plan = await db.Plans
            .AsNoTracking()
            .Where(p => p.TaskId == taskId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(cancellationToken);

        var artifacts = await db.Artifacts
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var documents = await db.Documents
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .OrderBy(d => d.UploadedAt)
            .ToListAsync(cancellationToken);

        // 任务摘要复用 TaskService 的映射 —— 别在这儿抄第二份，
        // 抄的那份漏了 IsArchived/Assignee/Priority/DueAt/Weight/PendingDependencies/Stale。
        var pending = await PendingDependencyCounts.CountAsync(db, [taskId], cancellationToken);

        // 项目级规约也进评审包：七个维度是通用的，规约是这个项目特有的 ——
        // 评审 agent 得知道「这个项目怎么算做完」才能判。
        var spec = await db.ProjectSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == task.ProjectId, cancellationToken);
        var specDto = spec is null
            ? ProjectSpecDto.Empty(task.ProjectId)
            : ProjectSpecService.ToDto(spec);

        // 规约的逐条判定也要进评审包 —— 评审时要一条一条对着判。
        var specItems = await BuildSpecItemsAsync(current, cancellationToken);

        return new ReviewPackDto(
            ToDto(current),
            TaskService.ToSummary(task, project, clock.UtcNow, pending.GetValueOrDefault(taskId)),
            plan is null ? null : TaskService.ToPlanDto(plan),
            artifacts.Select(TaskService.ToArtifactDto).ToList(),
            documents.Select(TaskService.ToDocumentDto).ToList(),
            ReviewDimensions.All
                .Select(d => new ReviewDimensionDto(d.Key, d.Name, d.Question, d.Checklist))
                .ToList(),
            specDto,
            specItems);
    }

    public async Task<ReviewItemDto> SubmitAsync(
        Guid taskId,
        string dimension,
        bool passed,
        string? findings = null,
        string? evidence = null,
        string? recommendation = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default,
        bool skipped = false)
    {
        var definition = ReviewDimensions.Find(dimension)
                         ?? throw new GuanfuException(
                             $"未知评审维度: {dimension}。合法值: {string.Join(", ", ReviewDimensions.Keys)}");

        var current = await LoadRoundAsync(taskId, round, cancellationToken)
                      ?? throw new NotFoundException("评审轮次", round?.ToString() ?? taskId.ToString());

        var item = current.Items.FirstOrDefault(i => string.Equals(i.Dimension, definition.Key, StringComparison.OrdinalIgnoreCase))
                   ?? throw new GuanfuException($"本轮没有「{definition.Name}」这个维度。");

        // Skipped 的两条约束（只约束**新写入**，老数据不动 —— 历史轮次不能被新规则判成违规）：
        // 1. 必须写明为什么不适用，不能空着一个「跳过」就混过去；
        // 2. 每轮最多跳 2 个，否则七个维度全标 Skipped 就能结项。
        if (skipped)
        {
            if (string.IsNullOrWhiteSpace(findings))
            {
                throw new GuanfuException(
                    $"跳过「{definition.Name}」必须在 findings 里写明理由（为什么不适用）。");
            }

            var alreadySkipped = current.Items.Count(i => i.Status == ReviewStatus.Skipped && i.Id != item.Id);
            if (alreadySkipped >= MaxSkippedPerRound)
            {
                throw new GuanfuException(
                    $"每轮最多跳过 {MaxSkippedPerRound} 个维度，本轮已经跳了 {alreadySkipped} 个。" +
                    $"「{definition.Name}」必须给出通过/不通过的结论。");
            }
        }

        // 别人认领了这个维度（租约没过）就别提交 ——
        // 两个 agent 评同一个维度，后写的把先写的结论盖掉，
        // 先写的还以为自己评过了。认领是分工，不是软建议。
        var now = clock.UtcNow;

        if (!string.IsNullOrWhiteSpace(item.ClaimedBy) &&
            !string.Equals(item.ClaimedBy, reviewer, StringComparison.Ordinal) &&
            item.ClaimedUntil is DateTimeOffset claimedUntil &&
            claimedUntil > now)
        {
            throw new GuanfuException(
                $"「{definition.Name}」正被 {item.ClaimedBy} 评（到 {claimedUntil:u}）。" +
                "要么等它评完，要么让它放掉再来" +
                (string.IsNullOrWhiteSpace(reviewer) ? "（你就是它的话，提交时带上 reviewer=你的 id）" : "") + "。");
        }

        item.Status = skipped ? ReviewStatus.Skipped : (passed ? ReviewStatus.Passed : ReviewStatus.Failed);
        item.Findings = findings;
        item.Evidence = evidence;
        item.Recommendation = recommendation;
        item.Reviewer = reviewer ?? current.Reviewer;
        item.DecidedAt = now;

        // 结论出来了，认领就收工 —— 别让这个维度看起来还在被人占着
        item.ClaimedBy = null;
        item.ClaimedUntil = null;

        // 汇总：全部维度都有结论才算结束
        var (decided, allPassed) = EvaluateDimensions(current.Items);

        current.Verdict = !decided ? ReviewVerdict.Pending : (allPassed ? ReviewVerdict.Passed : ReviewVerdict.Failed);

        if (decided)
        {
            current.FinishedAt = clock.UtcNow;
            current.Summary ??= allPassed
                ? "全部维度通过。"
                : $"不通过: {string.Join("、", current.Items.Where(i => i.Status == ReviewStatus.Failed).Select(i => i.DimensionName))}";
        }

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            // 这不是状态迁移，只是评审结论，所以 from/to 都留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = item.Reviewer,
            Note = $"评审[{definition.Name}] {(skipped ? "不适用" : (passed ? "通过" : "不通过"))}" +
                   (string.IsNullOrWhiteSpace(findings) ? "" : $"：{findings}"),
            EventType = TaskEventTypes.ReviewDimensionResult,
            Payload = GuanfuJson.Serialize(new
            {
                round = current.Round,
                dimension = definition.Key,
                result = item.Status.ToString(),
                reviewer = item.Reviewer
            }),
            CreatedAt = clock.UtcNow
        });

        if (decided)
        {
            // 轮次收尾也要留痕：消费方按 EventType 就能 replay 出「第几轮什么时候定的什么结论」
            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = taskId,
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Agent,
                Actor = item.Reviewer,
                Note = $"第 {current.Round} 轮评审结束：{current.Verdict}",
                EventType = TaskEventTypes.ReviewRoundClosed,
                Payload = GuanfuJson.Serialize(new
                {
                    round = current.Round,
                    verdict = current.Verdict.ToString(),
                    skipped = current.Items.Count(i => i.Status == ReviewStatus.Skipped),
                    commitHash = current.CommitHash,
                    // 分工也留一份：事后翻账本就知道每条是谁说的
                    judgements = current.Items
                        .OrderBy(i => i.Order)
                        .Select(i => new
                        {
                            dimension = i.Dimension,
                            result = i.Status.ToString(),
                            reviewer = i.Reviewer
                        })
                        .ToList()
                }),
                CreatedAt = clock.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return ToItemDto(item);
    }

    /// <summary>
    /// 认领某个维度的评审。
    ///
    /// 为什么要有这个：一轮七个维度，一个 agent 串行评下来很慢，
    /// 开子 agent 并行评才是常态 —— 但没有认领，两个 agent 会评同一个维度，
    /// 后写的把先写的结论盖掉。先写的还以为自己评过了。
    ///
    /// 认领带租约：认领的人要是掉线了（没交结论就跑了），
    /// 到期后别人能接手，不至于把这个维度永远卡住。
    /// </summary>
    public async Task<ReviewItemDto> ClaimDimensionAsync(
        Guid taskId,
        string dimension,
        string agentId,
        int leaseMinutes = 30,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new GuanfuException("agentId 不能为空。");
        }

        var definition = ReviewDimensions.Find(dimension)
                         ?? throw new GuanfuException(
                             $"未知评审维度: {dimension}。合法值: {string.Join(", ", ReviewDimensions.Keys)}");

        var current = await LoadRoundAsync(taskId, round, cancellationToken)
                      ?? throw new NotFoundException("评审轮次", round?.ToString() ?? taskId.ToString());

        var item = current.Items.FirstOrDefault(i =>
                       string.Equals(i.Dimension, definition.Key, StringComparison.OrdinalIgnoreCase))
                   ?? throw new GuanfuException($"本轮没有「{definition.Name}」这个维度。");

        var now = clock.UtcNow;

        if (item.Status != ReviewStatus.Pending)
        {
            throw new GuanfuException(
                $"「{definition.Name}」已经有结论了（{item.Status}），不用再认领。");
        }

        var heldByOther = !string.IsNullOrWhiteSpace(item.ClaimedBy) &&
                          !string.Equals(item.ClaimedBy, agentId, StringComparison.Ordinal) &&
                          item.ClaimedUntil is DateTimeOffset until &&
                          until > now;

        if (heldByOther)
        {
            throw new GuanfuException(
                $"「{definition.Name}」正被 {item.ClaimedBy} 评（到 {item.ClaimedUntil:u}）。" +
                "换个维度评，或者等租约到期。");
        }

        var expiresAt = now.AddMinutes(Math.Clamp(leaseMinutes, 1, 24 * 60));

        // 原子认领：占用判断与写入必须是一条 UPDATE ——
        // 分两步的话两个 agent 会同时看到「没人认领」，然后一起认领同一个维度。
        var affected = await db.ReviewItems
            .Where(i => i.Id == item.Id &&
                        i.Status == ReviewStatus.Pending &&
                        (i.ClaimedBy == null ||
                         i.ClaimedUntil == null ||
                         i.ClaimedUntil <= now ||
                         i.ClaimedBy == agentId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ClaimedBy, agentId)
                .SetProperty(i => i.ClaimedAt, now)
                .SetProperty(i => i.ClaimedUntil, expiresAt), cancellationToken);

        if (affected == 0)
        {
            throw new GuanfuException(
                $"「{definition.Name}」刚被别人认领了，重新看一眼再认。");
        }

        // ExecuteUpdateAsync 绕过变更跟踪 —— 不同步的话后面任何一次保存都会把旧值写回去
        item.ClaimedBy = agentId;
        item.ClaimedAt = now;
        item.ClaimedUntil = expiresAt;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = agentId,
            Note = $"认领评审维度[{definition.Name}]，到 {expiresAt:u} 到期",
            EventType = TaskEventTypes.ReviewDimensionClaimed,
            Payload = GuanfuJson.Serialize(new
            {
                round = current.Round,
                dimension = definition.Key,
                agentId,
                claimedUntil = expiresAt
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return ToItemDto(item);
    }

    /// <summary>认领某条完成定义的判定（跟维度一个道理）。</summary>
    public async Task<SpecVerdictDto> ClaimSpecItemAsync(
        Guid taskId,
        string item,
        string agentId,
        int leaseMinutes = 30,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new GuanfuException("agentId 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(item))
        {
            throw new GuanfuException("规约条目不能为空。");
        }

        var current = await LoadRoundAsync(taskId, round, cancellationToken)
                      ?? throw new NotFoundException("评审轮次", round?.ToString() ?? taskId.ToString());

        var dod = await LoadDefinitionOfDoneAsync(current.TaskId, cancellationToken);
        var text = dod.FirstOrDefault(d => string.Equals(TaskKeys.Normalize(d), TaskKeys.Normalize(item), StringComparison.Ordinal))
                   ?? throw new GuanfuException(
                       $"「{item}」不在这个项目当前的完成定义里。" +
                       (dod.Count == 0 ? "（项目还没填完成定义）" : $"当前有：{string.Join("；", dod)}"));

        var key = TaskKeys.Normalize(text);
        var now = clock.UtcNow;

        var verdict = current.SpecVerdicts
            .FirstOrDefault(v => string.Equals(v.ItemKey, key, StringComparison.Ordinal));

        if (verdict is not null && verdict.Status != ReviewStatus.Pending)
        {
            throw new GuanfuException($"「{text}」已经有判定了（{verdict.Status}），不用再认领。");
        }

        if (verdict is not null)
        {
            var heldByOther = !string.IsNullOrWhiteSpace(verdict.ClaimedBy) &&
                              !string.Equals(verdict.ClaimedBy, agentId, StringComparison.Ordinal) &&
                              verdict.ClaimedUntil is DateTimeOffset until &&
                              until > now;

            if (heldByOther)
            {
                throw new GuanfuException(
                    $"「{text}」正被 {verdict.ClaimedBy} 判（到 {verdict.ClaimedUntil:u}）。" +
                    "换一条判，或者等租约到期。");
            }
        }

        var expiresAt = now.AddMinutes(Math.Clamp(leaseMinutes, 1, 24 * 60));

        if (verdict is null)
        {
            verdict = new SpecVerdict
            {
                ReviewRoundId = current.Id,
                TaskId = taskId,
                ItemKey = key,
                ItemText = text
            };

            current.SpecVerdicts.Add(verdict);
            db.SpecVerdicts.Add(verdict);
        }

        verdict.ClaimedBy = agentId;
        verdict.ClaimedAt = now;
        verdict.ClaimedUntil = expiresAt;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = agentId,
            Note = $"认领规约判定[{text}]，到 {expiresAt:u} 到期",
            EventType = TaskEventTypes.SpecVerdictClaimed,
            Payload = GuanfuJson.Serialize(new
            {
                round = current.Round,
                item = text,
                agentId,
                claimedUntil = expiresAt
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return ToSpecVerdictDto(verdict);
    }

    public async Task<ReviewEvidenceSnapshot> RecordReportEvidenceAsync(
        Guid taskId, int round, int expectedVersion, ReviewReportEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ReviewEvidenceRules.Validate(evidence);

        var rounds = VisibleRounds(taskId);
        var current = await rounds.AsNoTracking().OrderByDescending(r => r.Round)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("评审轮次", taskId);
        if (current.Round != round)
        {
            throw new GuanfuException("请在当前评审轮次提交报告证据。");
        }

        var existing = ReadEvidence(current);
        if ((existing?.Version ?? 0) != expectedVersion)
        {
            throw new GuanfuException("报告证据已更新，请重新读取本轮评审后再提交。");
        }

        if (ReferenceChanged(existing?.Evidence.Revision, evidence.Revision) ||
            ReferenceChanged(existing?.Evidence.Baseline, evidence.Baseline))
        {
            throw new GuanfuException("评审基线或评审版本已变化，请开新一轮评审。");
        }

        var snapshot = new ReviewEvidenceSnapshot(expectedVersion + 1, evidence, clock.UtcNow,
            currentMember?.Current?.DisplayName);
        var json = GuanfuJson.Serialize(snapshot);
        if (json.Length > 256_000)
        {
            throw new GuanfuException("报告证据过大，请用产物上传长日志或完整补丁，在报告中填写引用。");
        }

        var updated = await rounds
            .Where(r => r.Id == current.Id && r.ReportEvidenceJson == current.ReportEvidenceJson)
            .Where(r => !db.ReviewRounds.Any(next => next.TaskId == taskId && next.Round > round))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.ReportEvidenceJson, json), cancellationToken);
        if (updated == 0)
        {
            throw new GuanfuException("评审轮次或报告证据已更新，请重新读取后再提交。");
        }

        return snapshot;
    }

    private static bool ReferenceChanged(string? previous, string? current) =>
        !string.IsNullOrWhiteSpace(previous) && !string.Equals(previous.Trim(), current?.Trim(), StringComparison.Ordinal);

    private IQueryable<ReviewRound> VisibleRounds(Guid taskId)
    {
        var visibleTasks = db.Tasks.VisibleTo(currentMember?.Current);
        return db.ReviewRounds.Where(r => r.TaskId == taskId && visibleTasks.Any(t => t.Id == r.TaskId));
    }

    public async Task<IReadOnlyList<ReviewRoundDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var rounds = await VisibleRounds(taskId)
            .AsNoTracking()
            .OrderBy(r => r.Round)
            .ToListAsync(cancellationToken);

        if (rounds.Count == 0)
        {
            return [];
        }

        var ids = rounds.Select(r => r.Id).ToList();
        var items = await db.ReviewItems
            .AsNoTracking()
            .Where(i => ids.Contains(i.ReviewRoundId))
            .OrderBy(i => i.Order)
            .ToListAsync(cancellationToken);

        return rounds.Select(r => ToDto(r, items.Where(i => i.ReviewRoundId == r.Id).ToList())).ToList();
    }

    public async Task<ReviewRoundDto?> GetCurrentAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var current = await LoadRoundAsync(taskId, null, cancellationToken, asNoTracking: true);
        return current is null ? null : ToDto(current);
    }

    public async Task<ReviewGateDto> GetGateAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        // 门禁是**只读判定**，必须看库里的真实结论：跟踪查询会优先返回内存里的旧实例，
        // 绕过 SaveChanges 的写入（批量 ExecuteUpdate / 升级脚本 / 另一个进程）它看不见，
        // 老数据里已经写好的 Skipped 会被当成「待评审」，任务就永远结不了项。
        var current = await LoadRoundAsync(taskId, null, cancellationToken, asNoTracking: true, checkVisibility: false);

        if (current is null)
        {
            // 没开过评审 —— 不拦。老流程（不走评审）照旧能结项。
            return new ReviewGateDto(false, nameof(ReviewVerdict.Pending), [], []);
        }

        var pending = new List<string>();
        var failed = new List<string>();

        var byKey = current.Items.ToDictionary(i => i.Dimension, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in ReviewDimensions.All)
        {
            if (!byKey.TryGetValue(definition.Key, out var item))
            {
                // 这一轮压根没建这个维度 —— 当成待评审，不能当它不存在。
                // 少一个维度就放行的话，六个维度全过也能结项。
                pending.Add(definition.Name);
                continue;
            }

            if (item.Status == ReviewStatus.Pending)
            {
                pending.Add(definition.Name);
            }
            else if (item.Status == ReviewStatus.Failed)
            {
                failed.Add(definition.Name);
            }
        }

        // 模型里已经删掉、但历史轮次里还留着的维度，也按实际状态算
        foreach (var item in current.Items)
        {
            if (ReviewDimensions.Keys.Contains(item.Dimension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.Status == ReviewStatus.Pending)
            {
                pending.Add(item.DimensionName);
            }
            else if (item.Status == ReviewStatus.Failed)
            {
                failed.Add(item.DimensionName);
            }
        }

        // Skipped 既不算待评审也不算不通过 —— 「不适用」是一种结论。
        // 上限（每轮 2 个）与「必须写理由」在**写入时**拦（见 SubmitAsync），
        // 这里不再重复校验：老数据里可能有超限/无理由的 Skipped，
        // 拿新规则去判历史轮次会把本来能结项的任务卡死。

        // 规约（DoD）的逐条判定也是门禁的一部分：
        // 七个维度全过，但项目自己的完成定义一条都没判 —— 不能就这么结项。
        var specItems = await BuildSpecItemsAsync(current, cancellationToken);
        var pendingSpec = specItems
            .Where(i => i.Status == nameof(ReviewStatus.Pending))
            .Select(i => i.Text)
            .ToList();
        var failedSpec = specItems
            .Where(i => i.Status == nameof(ReviewStatus.Failed))
            .Select(i => i.Text)
            .ToList();

        return new ReviewGateDto(
            true,
            current.Verdict.ToString(),
            failed,
            pending,
            await CheckCommitAsync(taskId, current, cancellationToken),
            pendingSpec,
            failedSpec);
    }

    /// <summary>
    /// 轮次绑 commit 的门禁：评的是那一版代码，评完之后代码变了就得重评。
    ///
    /// 三种情况：
    /// - 老轮次（没有 CommitHash）→ 跳过校验，保持兼容；
    /// - 有 CommitHash 且读得到 HEAD → 不一致就拒；
    /// - 有 CommitHash 但**读不到 HEAD** → 也拒。
    ///   「不知道现在是什么代码」不等于「代码没变」（规矩 10 的同一条精神）。
    /// </summary>
    private async Task<string?> CheckCommitAsync(
        Guid taskId,
        ReviewRound current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.CommitHash))
        {
            return null;
        }

        var projectId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => (Guid?)t.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        var project = projectId is null
            ? null
            : await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId.Value, cancellationToken);

        var head = ReadHeadSha(project);

        if (head is null)
        {
            return $"第 {current.Round} 轮评审绑定的是 commit {Short(current.CommitHash)}，" +
                   "但现在读不到仓库 HEAD，无法确认代码是否变更。请开新轮评审。";
        }

        if (!string.Equals(head, current.CommitHash, StringComparison.OrdinalIgnoreCase))
        {
            return $"评审后代码已变更（评审时 {Short(current.CommitHash)} → 当前 {Short(head)}），请开新轮评审。";
        }

        return null;
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(未知)" : sha[..Math.Min(8, sha.Length)];

    /// <summary>
    /// 这一轮的维度是不是都有结论了。
    ///
    /// 不只是「现有的项都不再是 Pending」——还要看模型里定义的维度
    /// 这一轮是不是都建出来了。少建一个维度就算没评完，
    /// 否则六个维度全过就能结项，第七个维度形同虚设。
    /// </summary>
    private static (bool Decided, bool AllPassed) EvaluateDimensions(IReadOnlyCollection<ReviewItem> items)
    {
        var byKey = items.ToDictionary(i => i.Dimension, StringComparer.OrdinalIgnoreCase);

        var decided = true;
        var allPassed = true;

        foreach (var definition in ReviewDimensions.All)
        {
            if (!byKey.TryGetValue(definition.Key, out var item))
            {
                decided = false;
                allPassed = false;
                continue;
            }

            if (item.Status == ReviewStatus.Pending)
            {
                decided = false;
                allPassed = false;
            }
            else if (item.Status == ReviewStatus.Failed)
            {
                allPassed = false;
            }
        }

        foreach (var item in items)
        {
            if (ReviewDimensions.Keys.Contains(item.Dimension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.Status == ReviewStatus.Pending)
            {
                decided = false;
                allPassed = false;
            }
            else if (item.Status == ReviewStatus.Failed)
            {
                allPassed = false;
            }
        }

        return (decided, allPassed);
    }

    /// <summary>
    /// 取一轮评审（含维度项）。
    /// <paramref name="asNoTracking"/> 供只读判定用：跳过变更跟踪，直接读库里的当前值；
    /// 写入路径（SubmitAsync）必须保持跟踪，否则改不动实体。
    /// </summary>
    private async Task<ReviewRound?> LoadRoundAsync(
        Guid taskId,
        int? round,
        CancellationToken cancellationToken,
        bool asNoTracking = false,
        bool checkVisibility = true)
    {
        var query = checkVisibility ? VisibleRounds(taskId) : db.ReviewRounds.Where(r => r.TaskId == taskId);

        if (round is not null)
        {
            query = query.Where(r => r.Round == round.Value);
        }

        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        var entity = await query
            .OrderByDescending(r => r.Round)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var itemsQuery = db.ReviewItems.Where(i => i.ReviewRoundId == entity.Id);

        if (asNoTracking)
        {
            itemsQuery = itemsQuery.AsNoTracking();
        }

        var items = await itemsQuery
            .OrderBy(i => i.Order)
            .ToListAsync(cancellationToken);

        // EF 的导航不一定填得上，这里手动挂回去
        entity.Items = items;
        return entity;
    }

    internal static ReviewRoundDto ToDto(ReviewRound round, List<ReviewItem>? items = null)
    {
        var all = (items ?? round.Items).OrderBy(i => i.Order).ToList();

        return new ReviewRoundDto(
            round.Id,
            round.TaskId,
            round.Round,
            round.Verdict.ToString(),
            round.Summary,
            round.Reviewer,
            round.StartedAt,
            round.FinishedAt,
            all.Select(ToItemDto).ToList(),
            round.CommitHash,
            // 分工与结论：收尾的人（协调者）要一眼看出谁还没交、哪条不通过
            all.Select(i => new ReviewJudgementDto(
                i.Dimension,
                i.DimensionName,
                i.Status.ToString(),
                i.Reviewer)).ToList(),
            all.Count(i => i.Status == ReviewStatus.Pending),
            all.Count(i => i.Status == ReviewStatus.Failed),
            ReadEvidence(round));
    }

    private static ReviewEvidenceSnapshot? ReadEvidence(ReviewRound round) =>
        round.ReportEvidenceJson is { } json ? GuanfuJson.Deserialize<ReviewEvidenceSnapshot>(json) : null;

    private static SpecVerdictDto ToSpecVerdictDto(SpecVerdict verdict) => new(
        verdict.Id,
        verdict.TaskId,
        verdict.ItemKey,
        verdict.ItemText,
        verdict.Status.ToString(),
        verdict.Note,
        verdict.Reviewer,
        verdict.DecidedAt,
        verdict.ClaimedBy,
        verdict.ClaimedUntil);

    private static ReviewItemDto ToItemDto(ReviewItem item) =>
        new(
            item.Id,
            item.Dimension,
            item.DimensionName,
            item.Order,
            item.Status.ToString(),
            item.Findings,
            item.Evidence,
            item.Recommendation,
            item.Reviewer,
            item.DecidedAt,
            item.ClaimedBy,
            item.ClaimedUntil);

    /// <summary>
    /// 读仓库当前 HEAD。项目没配仓库、git 读不出来（不是仓库、git 不可用、HEAD 为空）
    /// 一律返回 null —— 调用方必须按「读不到」处理，不能当成「没变化」。
    /// </summary>
    private string? ReadHeadSha(Project? project)
    {
        if (string.IsNullOrWhiteSpace(project?.RepoPath))
        {
            return null;
        }

        try
        {
            var state = git.Inspect(project!.RepoPath!);
            return string.IsNullOrWhiteSpace(state.HeadSha) ? null : state.HeadSha;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
