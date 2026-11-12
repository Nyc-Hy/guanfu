using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 评审工作流。
///
/// **边界**：工作台不提供 AI。这里给的是**骨架** —— 阶段、维度、检查清单、门禁、历史；
/// 判断由 agent（可以开子 agent / 专家团并行）来做，做完逐维度回写结论。
/// 工作台负责的是「不评审完不许结项」和「每轮结论都留痕」。
///
/// 评审服务同时就是结项门禁的实现（见 <see cref="IReviewGate"/>）——
/// 注册一次，两个接口共用同一个实例。
/// </summary>
public interface IReviewService : IReviewGate
{
    /// <summary>开一轮评审。按固定的七个维度各建一条待办。</summary>
    Task<ReviewRoundDto> StartAsync(Guid taskId, string? reviewer = null, CancellationToken cancellationToken = default);

    /// <summary>取评审包：维度清单 + 任务上下文 + 变更文件。agent 照着这个逐维度评审。</summary>
    Task<ReviewPackDto> GetPackAsync(Guid taskId, int? round = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 回写某个维度的结论。
    ///
    /// <paramref name="skipped"/> 为 true 表示这个维度**不适用**（跳过）——
    /// 枚举里一直有 Skipped，但以前没有任何入口能写出来，
    /// 于是「本次改动不涉及安全维度」这类情况只能硬编一个通过。
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
    : WorkbenchException(BuildMessage(gate))
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

        return $"评审还没过（{string.Join("；", parts)}）。" +
               "用 workbench_submit_review 回写每个维度的结论，全部通过后才能结项。";
    }
}

public sealed class ReviewService(IAppDbContext db, IClock clock, IGitInspector git) : IReviewService, IReviewGate
{
    public async Task<ReviewRoundDto> StartAsync(
        Guid taskId,
        string? reviewer = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

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

        var changedFiles = string.IsNullOrWhiteSpace(project?.RepoPath)
            ? []
            : git.Inspect(project!.RepoPath!).ChangedFilePaths;

        return new ReviewPackDto(
            ToDto(current),
            ToTaskDto(task, project),
            plan is null ? null : ToPlanDto(plan),
            artifacts.Select(ToArtifactDto).ToList(),
            documents.Select(ToDocumentDto).ToList(),
            changedFiles,
            ReviewDimensions.All
                .Select(d => new ReviewDimensionDto(d.Key, d.Name, d.Question, d.Checklist))
                .ToList());
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
                         ?? throw new WorkbenchException(
                             $"未知评审维度: {dimension}。合法值: {string.Join(", ", ReviewDimensions.Keys)}");

        var current = await LoadRoundAsync(taskId, round, cancellationToken)
                      ?? throw new NotFoundException("评审轮次", round?.ToString() ?? taskId.ToString());

        var item = current.Items.FirstOrDefault(i => string.Equals(i.Dimension, definition.Key, StringComparison.OrdinalIgnoreCase))
                   ?? throw new WorkbenchException($"本轮没有「{definition.Name}」这个维度。");

        item.Status = skipped ? ReviewStatus.Skipped : (passed ? ReviewStatus.Passed : ReviewStatus.Failed);
        item.Findings = findings;
        item.Evidence = evidence;
        item.Recommendation = recommendation;
        item.Reviewer = reviewer ?? current.Reviewer;
        item.DecidedAt = clock.UtcNow;

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
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

        return ToItemDto(item);
    }

    public async Task<IReadOnlyList<ReviewRoundDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var rounds = await db.ReviewRounds
            .AsNoTracking()
            .Where(r => r.TaskId == taskId)
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
        var current = await LoadRoundAsync(taskId, null, cancellationToken);
        return current is null ? null : ToDto(current);
    }

    public async Task<ReviewGateDto> GetGateAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var current = await LoadRoundAsync(taskId, null, cancellationToken);

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

        return new ReviewGateDto(true, current.Verdict.ToString(), failed, pending);
    }

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

    private async Task<ReviewRound?> LoadRoundAsync(Guid taskId, int? round, CancellationToken cancellationToken)
    {
        var query = db.ReviewRounds.Where(r => r.TaskId == taskId);

        if (round is not null)
        {
            query = query.Where(r => r.Round == round.Value);
        }

        var entity = await query
            .OrderByDescending(r => r.Round)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var items = await db.ReviewItems
            .Where(i => i.ReviewRoundId == entity.Id)
            .OrderBy(i => i.Order)
            .ToListAsync(cancellationToken);

        // EF 的导航不一定填得上，这里手动挂回去
        entity.Items = items;
        return entity;
    }

    private static ReviewRoundDto ToDto(ReviewRound round, List<ReviewItem>? items = null) =>
        new(
            round.Id,
            round.TaskId,
            round.Round,
            round.Verdict.ToString(),
            round.Summary,
            round.Reviewer,
            round.StartedAt,
            round.FinishedAt,
            (items ?? round.Items).OrderBy(i => i.Order).Select(ToItemDto).ToList());

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
            item.DecidedAt);

    private static TaskSummaryDto ToTaskDto(TaskItem task, Project? project) =>
        new(
            task.Id,
            task.ProjectId,
            project?.Name ?? string.Empty,
            task.Title,
            task.Status,
            task.ClaimedByAgentId,
            task.LeaseExpiresAt,
            task.UpdatedAt,
            task.LastActivityAt,
            false,
            task.MilestoneId,
            task.Key,
            task.Type,
            task.Branch);

    private static PlanDto ToPlanDto(Plan p) =>
        new(p.Id, p.Version, p.Background, p.Understanding, p.Breakdown, p.AcceptanceCriteria, p.Risks, p.Author, p.CreatedAt);

    private static ArtifactDto ToArtifactDto(Artifact a) =>
        new(a.Id, a.Type.ToString(), a.Title, a.Content, a.ExternalPath, a.CommitSha, a.Branch, a.Author, a.CreatedAt);

    private static DocumentDto ToDocumentDto(Document d) =>
        new(d.Id, d.FileName, d.SizeBytes, d.Format.ToString(), d.Kind, d.Parsed, d.UploadedAt, d.ParseError, d.StoredPath);
}
