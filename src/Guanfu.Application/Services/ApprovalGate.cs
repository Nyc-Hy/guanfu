using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 数据库写入审批闸门。工具名与类别也是指纹校验的一部分，旧审批记录仍需按原类型识别。
///
/// 两条路径：
/// 1. 没带 approvalId → 申请一条审批，等人工决定；
/// 2. 带了 approvalId → 校验这条审批确实是给「这个工具 + 这个操作 + 这个项目」批的，
///    然后**原子占住**它。批一次只能用一次。
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// 取得执行许可。通过时审批已被占住（Approved → Executing），
    /// 调用方**必须**在执行结束后调用 <see cref="CompleteAsync"/>，否则审批会卡在 Executing。
    /// </summary>
    Task<ApprovalGateResult> AcquireAsync(
        string toolName,
        string kind,
        string payload,
        string summary,
        string? requestedBy,
        Guid? taskId,
        Guid projectId,
        Guid? approvalId,
        int waitSeconds,
        CancellationToken cancellationToken = default,
        /// <summary>
        /// 这条审批归谁批（成员 id）。
        ///
        /// 「审批只能个人审批」—— agent 的写操作要人点头，
        /// 点的是**这把钥匙背后的人**。传 null = 还没归到人身上。
        /// </summary>
        Guid? requesterMemberId = null);

    /// <summary>收尾：把审批标记为已执行（成功或失败都算消费掉了）。</summary>
    Task CompleteAsync(Guid approvalId, string? resultSummary, CancellationToken cancellationToken = default);
}

public sealed record ApprovalGateResult(
    bool Approved,
    Guid ApprovalId,
    string Status,
    string Message);

public sealed class ApprovalGate(IApprovalService approvals) : IApprovalGate
{
    public async Task<ApprovalGateResult> AcquireAsync(
        string toolName,
        string kind,
        string payload,
        string summary,
        string? requestedBy,
        Guid? taskId,
        Guid projectId,
        Guid? approvalId,
        int waitSeconds,
        CancellationToken cancellationToken = default,
        Guid? requesterMemberId = null)
    {
        if (approvalId is not null)
        {
            return await ConsumeAsync(approvalId.Value, toolName, kind, payload, projectId, cancellationToken);
        }

        var approval = await approvals.RequestAsync(
            toolName, kind, payload, summary, requestedBy, taskId, projectId,
            ApprovalService.DefaultTtlMinutes, cancellationToken, requesterMemberId);

        var decided = await approvals.AwaitAsync(approval.Id, Math.Clamp(waitSeconds, 1, 300), cancellationToken);

        if (decided.Status != nameof(ApprovalStatus.Approved))
        {
            var message = decided.Status == nameof(ApprovalStatus.Rejected)
                ? $"已被驳回，未执行。原因: {decided.DecisionReason}"
                : $"仍在等待人工审批（当前状态 {decided.Status}）。批准后带 approvalId={decided.Id} 再次调用即可执行。";

            return new ApprovalGateResult(false, decided.Id, decided.Status, message);
        }

        // 人工在等待期间批的 —— 也要占住。否则两个并发请求能拿同一条审批跑两遍。
        return await ConsumeAsync(decided.Id, toolName, kind, payload, projectId, cancellationToken);
    }

    public async Task CompleteAsync(Guid approvalId, string? resultSummary, CancellationToken cancellationToken = default)
    {
        await approvals.MarkExecutedAsync(approvalId, resultSummary, cancellationToken);
    }

    private async Task<ApprovalGateResult> ConsumeAsync(
        Guid approvalId,
        string toolName,
        string kind,
        string payload,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var consumed = await approvals.TryConsumeAsync(
            approvalId, toolName, kind, payload, projectId, cancellationToken);

        return consumed.Ok
            ? new ApprovalGateResult(true, consumed.ApprovalId, consumed.Status, "审批已通过。")
            : new ApprovalGateResult(false, consumed.ApprovalId, consumed.Status, consumed.Error ?? "审批未通过。");
    }
}
