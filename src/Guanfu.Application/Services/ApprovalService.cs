using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

public interface IApprovalService
{
    Task<ApprovalDto> RequestAsync(
        string toolName,
        string kind,
        string payload,
        string summary,
        string? requestedBy = null,
        Guid? taskId = null,
        Guid? projectId = null,
        int ttlMinutes = 30,
        CancellationToken cancellationToken = default);

    Task<ApprovalDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApprovalDto>> ListAsync(
        ApprovalStatus? status = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default,
        /// <summary>
        /// 最多取多少条（最新的优先）。不设上限的话，审批页会把历史全拉进浏览器 ——
        /// 用得越久越卡。参数放在 CancellationToken 之后，是为了不破坏现有位置参数调用。
        /// </summary>
        int limit = 500);

    Task<ApprovalDto> DecideAsync(
        Guid id,
        bool approve,
        string? decidedBy = null,
        string? reason = null,
        CancellationToken cancellationToken = default,
        /// <summary>
        /// 裁决人所用人工凭证的指纹（不存明文）。
        ///
        /// 参数放在 CancellationToken 之后：插在中间会破坏既有的位置参数调用
        /// （老代码第 5 个位置传的是 ct，直接编译不过）。
        /// </summary>
        string? decidedByCredential = null);

    /// <summary>
    /// 人工撤销一条审批（Pending / Approved → Rejected）。
    ///
    /// 「批错了但还没执行」必须有后悔药：以前只能等它过期，
    /// 而 Approved 的单子在过期前一直能被续跑执行。
    /// Executing / Executed 一律拒绝 —— 已经在跑或跑完的操作撤不回来。
    /// </summary>
    /// <param name="decidedBy">
    /// 撤销的人。REST 那边带着人工凭证来，留 null（按「human」记）；
    /// MCP 那边是 agent 调的，得记成 agent 的标识 ——
    /// 审计里写「human」就成了假账。
    /// </param>
    Task<ApprovalDto> RevokeAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default,
        string? decidedBy = null);

    /// <summary>阻塞等待人工决定。返回当前状态；超时则仍是 Pending。</summary>
    Task<ApprovalDto> AwaitAsync(Guid id, int timeoutSeconds = 60, CancellationToken cancellationToken = default);

    /// <summary>标记审批已实际执行，并记录结果摘要。</summary>
    Task<ApprovalDto> MarkExecutedAsync(Guid id, string? resultSummary, CancellationToken cancellationToken = default);

    Task<int> ExpireOverdueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 对账「服务重启时被中断的执行」。
    ///
    /// <paramref name="processStartedAt"/> 是**本次进程**启动的时刻。
    /// 凡是还在 Executing、且开始时间早于它的审批，都是上一个进程留下的 ——
    /// 那次写操作可能已经生效了（commit 写进仓库了，只是没来得及回写状态），
    /// 也可能压根没跑。系统判不出来，就别假装判得出来：
    /// 标记成「结果未知」交人工确认，而不是悄悄重跑一遍。
    ///
    /// 返回被标记的条数。
    /// </summary>
    Task<int> ReconcileInterruptedAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子消费一条审批。
    ///
    /// 校验它批的确实是「这个工具 + 这个操作 + 这个项目」，而且没过期；
    /// 通过后立刻把它占住（Approved → Executing），别的并发请求就抢不到了。
    /// </summary>
    Task<ApprovalConsumeResult> TryConsumeAsync(
        Guid id,
        string toolName,
        string kind,
        string payload,
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>消费审批的结果。失败时 <see cref="Error"/> 写明为什么。</summary>
public sealed record ApprovalConsumeResult(bool Ok, Guid ApprovalId, string Status, string? Error = null)
{
    public static ApprovalConsumeResult Success(Guid id) =>
        new(true, id, nameof(ApprovalStatus.Executing));

    public static ApprovalConsumeResult Rejected(Guid id, string status, string error) =>
        new(false, id, status, error);
}

public sealed class ApprovalService(IAppDbContext db, IClock clock, INotifier? notifier = null) : IApprovalService
{
    private readonly INotifier? _notifier = notifier;

    public const int DefaultTtlMinutes = 30;

    /// <summary>
    /// Executing 卡多久算「执行中断」。执行方崩了/被杀了就不会来收尾，
    /// 审批会永远停在 Executing：既不能再批，也不能再用。
    /// </summary>
    public const int ExecutingTimeoutMinutes = 30;

    public async Task<ApprovalDto> RequestAsync(
        string toolName,
        string kind,
        string payload,
        string summary,
        string? requestedBy = null,
        Guid? taskId = null,
        Guid? projectId = null,
        int ttlMinutes = 30,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var hash = ComputePayloadHash(payload);

        // 去重：同一个操作（工具 + 参数指纹 + 项目）已经有待审的单子，就返回那一条。
        // agent 重试/超时后再申请是常态，不去重的话待审列表会被同一个操作刷屏，
        // 人批了其中一条，其余的还挂着 —— 看起来像有一堆没处理的操作。
        // 只认 Pending 且没过期：驳回/过期/已执行的单子要重新申请，不能复用。
        var pending = await db.Approvals
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending
                        && a.ToolName == toolName
                        && a.PayloadHash == hash
                        && a.ProjectId == projectId
                        && a.ExpiresAt > now)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (pending is not null)
        {
            return ToDto(pending);
        }

        var approval = new Approval
        {
            ToolName = toolName,
            Kind = kind,
            Payload = payload,
            // 指纹存的是「批的是哪个操作」。没有它，批一次就能用一辈子。
            PayloadHash = hash,
            Summary = summary,
            Status = ApprovalStatus.Pending,
            RequestedBy = requestedBy,
            TaskId = taskId,
            ProjectId = projectId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(ttlMinutes, 1, 24 * 60))
        };

        db.Approvals.Add(approval);
        await db.SaveChangesAsync(cancellationToken);

        await NotifyAsync(approval, cancellationToken);

        return ToDto(approval);
    }

    /// <summary>
    /// 审批产生时推通知。人不在电脑前时，审批不该静静躺着等到过期。
    /// 推送失败不影响主流程 —— 审批已经落库了，通知只是提醒。
    /// </summary>
    private async Task NotifyAsync(Approval approval, CancellationToken cancellationToken)
    {
        if (_notifier is null || approval.ProjectId is null)
        {
            return;
        }

        try
        {
            var project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == approval.ProjectId.Value, cancellationToken);

            if (project is null || string.IsNullOrWhiteSpace(project.NotificationWebhookUrl))
            {
                return;
            }

            var title = "观复待审批";
            var message =
                $"工具: {approval.ToolName}\n" +
                $"类别: {approval.Kind}\n" +
                $"摘要: {approval.Summary}\n" +
                $"申请人: {approval.RequestedBy ?? "(未标注)"}\n" +
                $"审批单: {approval.Id}";

            await _notifier.NotifyAsync(
                project.NotificationWebhookUrl, project.NotificationFormat, title, message, cancellationToken);
        }
        catch (Exception)
        {
            // 通知是锦上添花，不能让它把审批请求搞失败
        }
    }

    public async Task<ApprovalDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var approval = await db.Approvals
            .AsNoTracking()
            .Include(a => a.Task)
            .Include(a => a.Project)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (approval is null)
        {
            return null;
        }

        if (approval.Status == ApprovalStatus.Pending && approval.ExpiresAt <= clock.UtcNow)
        {
            // 过期必须真的落库。之前这里用 AsNoTracking 取实体再改属性，
            // 未跟踪的实体 SaveChanges 是不会写回的 —— 库里永远还是 Pending，
            // 待审列表只会越积越多，人还能批一条早就过期的审批。
            await ExpireAsync(id, clock.UtcNow, cancellationToken);
            approval.Status = ApprovalStatus.Expired;
        }

        return ToDto(approval);
    }

    public async Task<IReadOnlyList<ApprovalDto>> ListAsync(
        ApprovalStatus? status = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default,
        int limit = 500)
    {
        // 先把过期的待审落库 —— 不然「待审」列表里全是早就过期的单子，
        // 人还能点「批准」，而闸门只认状态不认过期时间。
        await ExpireOverdueAsync(cancellationToken);

        var query = db.Approvals.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(a => a.Status == status.Value);
        }

        if (projectId is not null)
        {
            query = query.Where(a => a.ProjectId == projectId);
        }

        var approvals = await query
            // 带上任务与项目：审批页要显示「这是哪个需求的活」，
            // 界面上不许露 GUID，得把编号和名字一起带出来。
            .Include(a => a.Task)
            .Include(a => a.Project)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToListAsync(cancellationToken);

        return approvals.Select(ToDto).ToList();
    }

    public async Task<ApprovalDto> DecideAsync(
        Guid id,
        bool approve,
        string? decidedBy = null,
        string? reason = null,
        CancellationToken cancellationToken = default,
        string? decidedByCredential = null)
    {
        if (!approve && string.IsNullOrWhiteSpace(reason))
        {
            throw new GuanfuException("驳回审批必须填写理由。");
        }

        // 裁决人不能空。共享 token 的现状下系统认不出真人，
        // 但至少不能连「谁点的」都没记 —— 审计里 decidedBy 是空的等于没审。
        if (string.IsNullOrWhiteSpace(decidedBy))
        {
            throw new GuanfuException("裁决人不能为空：审计要能查到是谁批的。");
        }

        var now = clock.UtcNow;

        // 先把过期的待审落库，别让人批一条已经过期的
        await ExpireOverdueAsync(cancellationToken);

        var current = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                      ?? throw new NotFoundException("审批", id);

        if (current.Status != ApprovalStatus.Pending)
        {
            throw new GuanfuException($"审批 {id} 已经是 {current.Status} 状态，不能再决定。");
        }

        if (current.ExpiresAt <= now)
        {
            throw new GuanfuException($"审批 {id} 已过期。");
        }

        var target = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;

        // 条件更新：两个人同时点，只有一个改得动，另一个明确收到「已被处理」
        var affected = await db.Approvals
            .Where(a => a.Id == id && a.Status == ApprovalStatus.Pending && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, target)
                .SetProperty(a => a.DecidedBy, decidedBy)
                .SetProperty(a => a.DecidedByCredential, decidedByCredential)
                .SetProperty(a => a.DecisionReason, reason)
                .SetProperty(a => a.DecidedAt, now), cancellationToken);

        if (affected == 0)
        {
            throw new GuanfuException($"审批 {id} 刚刚已经被处理过了。");
        }

        SyncTracked(id, target, decisionReason: reason, decidedBy: decidedBy, decidedAt: now);

        var decided = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, cancellationToken);
        return ToDto(decided);
    }

    public async Task<ApprovalDto> RevokeAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default,
        string? decidedBy = null)
    {
        var now = clock.UtcNow;

        // 撤销的人：没指定就是人（REST 那边带人工凭证来），指定了就按它记 ——
        // agent 撤的不能记成「human」，审计要分得出是谁干的。
        var actor = string.IsNullOrWhiteSpace(decidedBy) ? "human" : decidedBy.Trim();

        var current = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                      ?? throw new NotFoundException("审批", id);

        // 已经在执行 / 执行完的撤不回来 —— 操作已经落地，撤销许可不等于操作没发生。
        if (current.Status is ApprovalStatus.Executing or ApprovalStatus.Executed)
        {
            throw new GuanfuException(
                $"审批 {id} 已经是 {current.Status} 状态，操作已经落地，不能撤销。");
        }

        if (current.Status is not (ApprovalStatus.Pending or ApprovalStatus.Approved))
        {
            throw new GuanfuException(
                $"审批 {id} 已经是 {current.Status} 状态，不需要撤销。");
        }

        var note = string.IsNullOrWhiteSpace(reason)
            ? "人工撤销"
            : $"人工撤销：{reason.Trim()}";

        // 条件更新：撤销与「批准后被抢去执行」可能同时发生，
        // 只有还没被占住的那一条才撤得动。
        var affected = await db.Approvals
            .Where(a => a.Id == id &&
                        (a.Status == ApprovalStatus.Pending || a.Status == ApprovalStatus.Approved))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Rejected)
                .SetProperty(a => a.DecidedBy, actor)
                .SetProperty(a => a.DecisionReason, note)
                .SetProperty(a => a.DecidedAt, now), cancellationToken);

        if (affected == 0)
        {
            throw new GuanfuException($"审批 {id} 刚刚已经被处理或占用了，撤销没有生效。");
        }

        SyncTracked(id, ApprovalStatus.Rejected, decisionReason: note, decidedBy: actor, decidedAt: now);

        var revoked = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, cancellationToken);
        return ToDto(revoked);
    }

    public async Task<ApprovalDto> AwaitAsync(
        Guid id,
        int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        // 超时是「等了多久」的墙上时间，不能用注入的业务时钟判定
        // （测试里业务时钟是冻结的，会死等）。
        var stopwatch = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300));

        while (true)
        {
            var approval = await GetAsync(id, cancellationToken) ?? throw new NotFoundException("审批", id);

            if (approval.Status != nameof(ApprovalStatus.Pending))
            {
                return approval;
            }

            if (stopwatch.Elapsed >= limit)
            {
                return approval;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    public async Task<ApprovalDto> MarkExecutedAsync(Guid id, string? resultSummary, CancellationToken cancellationToken = default)
    {
        // 用条件更新而不是改实体再 Save：审批行可能被别的请求改过，
        // 拿跟踪态实体改会把别人的状态盖回去。
        var affected = await db.Approvals
            .Where(a => a.Id == id && (a.Status == ApprovalStatus.Executing || a.Status == ApprovalStatus.Approved))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Executed)
                .SetProperty(a => a.ResultSummary, resultSummary), cancellationToken);

        if (affected == 0)
        {
            var current = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            var status = current?.Status.ToString() ?? "不存在";
            throw new GuanfuException($"审批 {id} 处于 {status} 状态，不能标记为已执行。");
        }

        SyncTracked(id, ApprovalStatus.Executed, resultSummary);

        var updated = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == id, cancellationToken);
        return ToDto(updated);
    }

    /// <summary>
    /// 把变更跟踪里那份审批同步到最新状态。
    ///
    /// ExecuteUpdateAsync 绕过变更跟踪 —— 被跟踪的实体还留着旧值，
    /// 后面任何一次 SaveChanges 都会把它写回去（EF 会写整行），
    /// 于是「Approved → Executing」被改回 Approved，令牌又能用一次。
    ///
    /// 只同步**这次真的改过的字段**：以前 DecideAsync 把驳回理由传进了
    /// resultSummary 这个形参，理由写错了字段，ResultSummary 里躺着一句「这条不能跑」。
    /// </summary>
    private void SyncTracked(
        Guid id,
        ApprovalStatus status,
        string? resultSummary = null,
        string? decisionReason = null,
        string? decidedBy = null,
        DateTimeOffset? decidedAt = null,
        DateTimeOffset? executingStartedAt = null)
    {
        var tracked = db.Approvals.Local.FirstOrDefault(a => a.Id == id);
        if (tracked is null)
        {
            return;
        }

        tracked.Status = status;

        if (resultSummary is not null)
        {
            tracked.ResultSummary = resultSummary;
        }

        if (decisionReason is not null)
        {
            tracked.DecisionReason = decisionReason;
        }

        if (decidedBy is not null)
        {
            tracked.DecidedBy = decidedBy;
        }

        if (decidedAt is not null)
        {
            tracked.DecidedAt = decidedAt;
        }

        if (executingStartedAt is not null)
        {
            tracked.ExecutingStartedAt = executingStartedAt;
        }
    }

    public async Task<int> ExpireOverdueAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var expiredPending = await db.Approvals
            .Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Expired)
                .SetProperty(a => a.DecidedAt, now)
                .SetProperty(a => a.DecidedBy, "system")
                .SetProperty(a => a.DecisionReason, "超时未处理，自动过期"), cancellationToken);

        // Executing 也要回收：执行方崩了/被杀了就不会来调 CompleteAsync，
        // 单子永远卡在 Executing —— 既不能再批也不能再用，等于这个操作被永久锁死。
        // 老数据没有 ExecutingStartedAt，退回 DecidedAt 判定。
        //
        // 回收时**必须标 OutcomeUnknown**：分不清是「执行方崩了」还是「执行还没结束」
        // （长命令仍在跑），但两种情况下这次写操作都可能已经生效了 ——
        // 标上「结果未知」，交人工确认，不能假装它肯定没执行。
        var cutoff = now.AddMinutes(-ExecutingTimeoutMinutes);

        var interrupted = await db.Approvals
            .Where(a => a.Status == ApprovalStatus.Executing
                        && (a.ExecutingStartedAt ?? a.DecidedAt) != null
                        && (a.ExecutingStartedAt ?? a.DecidedAt) <= cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Expired)
                .SetProperty(a => a.OutcomeUnknown, true)
                .SetProperty(a => a.DecidedAt, now)
                .SetProperty(a => a.DecidedBy, "system")
                .SetProperty(a => a.DecisionReason, "执行中断或超时未收尾，这次写操作可能已经生效，结果未知，请人工确认后再重新申请"),
                cancellationToken);

        return expiredPending + interrupted;
    }

    public async Task<int> ReconcileInterruptedAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        // 只认「开始时间早于本进程启动」的 Executing —— 那一定是上一个进程留下的。
        // 本进程里正在跑的（开始时间晚于进程启动）不能碰。
        // 老数据没有 ExecutingStartedAt，退回 DecidedAt 判定。
        var interrupted = await db.Approvals
            .Where(a => a.Status == ApprovalStatus.Executing
                        && (a.ExecutingStartedAt ?? a.DecidedAt) != null
                        && (a.ExecutingStartedAt ?? a.DecidedAt) < processStartedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Expired)
                .SetProperty(a => a.OutcomeUnknown, true)
                .SetProperty(a => a.DecidedAt, now)
                .SetProperty(a => a.DecidedBy, "system")
                .SetProperty(a => a.DecisionReason,
                    "服务重启导致执行中断：这次写操作可能已经生效，结果未知，请人工确认后再重新申请"),
                cancellationToken);

        return interrupted;
    }

    public async Task<ApprovalConsumeResult> TryConsumeAsync(
        Guid id,
        string toolName,
        string kind,
        string payload,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var hash = ComputePayloadHash(payload);

        // 过期的先落库，免得拿一条过期的审批当有效
        await ExpireAsync(id, now, cancellationToken);

        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (approval is null)
        {
            return ApprovalConsumeResult.Rejected(id, nameof(ApprovalStatus.Pending), $"审批 {id} 不存在。");
        }

        if (approval.Status != ApprovalStatus.Approved)
        {
            return ApprovalConsumeResult.Rejected(
                id, approval.Status.ToString(),
                $"审批当前状态为 {approval.Status}，还不能执行。");
        }

        if (!string.Equals(approval.ToolName, toolName, StringComparison.Ordinal))
        {
            return ApprovalConsumeResult.Rejected(
                id, approval.Status.ToString(),
                $"这条审批批的是「{approval.ToolName}」，不能拿来执行「{toolName}」。");
        }

        if (!string.Equals(approval.Kind, kind, StringComparison.Ordinal))
        {
            return ApprovalConsumeResult.Rejected(
                id, approval.Status.ToString(),
                $"审批类别不匹配：审批是 {approval.Kind}，当前操作是 {kind}。");
        }

        if (approval.ProjectId != projectId)
        {
            return ApprovalConsumeResult.Rejected(
                id, approval.Status.ToString(),
                "这条审批属于别的项目，不能在这儿用。");
        }

        if (approval.PayloadHash is null)
        {
            return ApprovalConsumeResult.Rejected(
                id, approval.Status.ToString(),
                "这条审批创建于旧版本，没有绑定具体操作，不能用来执行写操作。请重新申请一次。");
        }

        if (!string.Equals(approval.PayloadHash, hash, StringComparison.Ordinal))
        {
            return ApprovalConsumeResult.Rejected(
                id, approval.Status.ToString(),
                "这次要执行的操作和当初审批的内容不一致，不能用这条审批。请重新申请一次。");
        }

        if (approval.ExpiresAt <= now)
        {
            return ApprovalConsumeResult.Rejected(id, approval.Status.ToString(), "这条审批已经过期了。");
        }

        // 原子抢占：条件更新，谁先改到谁拿到。并发请求只有一个能过。
        var affected = await db.Approvals
            .Where(a => a.Id == id
                        && a.Status == ApprovalStatus.Approved
                        && a.ToolName == toolName
                        && a.Kind == kind
                        && a.ProjectId == projectId
                        && a.PayloadHash == hash
                        && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Executing)
                .SetProperty(a => a.ExecutingStartedAt, now), cancellationToken);

        if (affected != 1)
        {
            return ApprovalConsumeResult.Rejected(id, approval.Status.ToString(), "这条审批已经被别的请求用掉了。");
        }

        // ExecuteUpdateAsync 绕过变更跟踪 —— 同步跟踪态的那一份，
        // 不然后续 SaveChanges 会把旧状态写回去，审批就白占住了。
        SyncTracked(id, ApprovalStatus.Executing, executingStartedAt: now);

        return ApprovalConsumeResult.Success(id);
    }

    private Task<int> ExpireAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Approvals
            .Where(a => a.Id == id && a.Status == ApprovalStatus.Pending && a.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Expired)
                .SetProperty(a => a.DecidedAt, now)
                .SetProperty(a => a.DecidedBy, "system")
                .SetProperty(a => a.DecisionReason, "超时未处理，自动过期"), cancellationToken);

    /// <summary>操作指纹。审批与操作必须一一对上，否则批一次就能用一辈子。</summary>
    internal static string ComputePayloadHash(string? payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        return Convert.ToHexString(bytes);
    }

    internal static ApprovalDto ToDto(Approval a) => new(
        a.Id,
        a.ToolName,
        a.Kind,
        a.Summary,
        a.Payload,
        a.Status.ToString(),
        a.RequestedBy,
        a.DecidedBy,
        a.DecisionReason,
        a.ResultSummary,
        a.TaskId,
        a.ProjectId,
        a.CreatedAt,
        a.ExpiresAt,
        a.DecidedAt,
        a.DecidedByCredential,
        a.Task?.Key,
        a.Task?.Title,
        a.Project?.Name,
        a.OutcomeUnknown);
}
