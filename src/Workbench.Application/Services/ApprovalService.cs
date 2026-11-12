using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

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
        CancellationToken cancellationToken = default);

    Task<ApprovalDto> DecideAsync(
        Guid id,
        bool approve,
        string? decidedBy = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>阻塞等待人工决定。返回当前状态；超时则仍是 Pending。</summary>
    Task<ApprovalDto> AwaitAsync(Guid id, int timeoutSeconds = 60, CancellationToken cancellationToken = default);

    /// <summary>标记审批已实际执行，并记录结果摘要。</summary>
    Task<ApprovalDto> MarkExecutedAsync(Guid id, string? resultSummary, CancellationToken cancellationToken = default);

    Task<int> ExpireOverdueAsync(CancellationToken cancellationToken = default);
}

public sealed class ApprovalService(IAppDbContext db, IClock clock) : IApprovalService
{
    public const int DefaultTtlMinutes = 30;

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
        var approval = new Approval
        {
            ToolName = toolName,
            Kind = kind,
            Payload = payload,
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

        return ToDto(approval);
    }

    public async Task<ApprovalDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (approval is null)
        {
            return null;
        }

        if (approval.Status == ApprovalStatus.Pending && approval.ExpiresAt <= clock.UtcNow)
        {
            approval.Status = ApprovalStatus.Expired;
            approval.DecidedAt = clock.UtcNow;
            approval.DecidedBy = "system";
            approval.DecisionReason = "超时未处理，自动过期";
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(approval);
    }

    public async Task<IReadOnlyList<ApprovalDto>> ListAsync(
        ApprovalStatus? status = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Approvals.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(a => a.Status == status.Value);
        }

        if (projectId is not null)
        {
            query = query.Where(a => a.ProjectId == projectId.Value);
        }

        var approvals = await query
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        return approvals.Select(ToDto).ToList();
    }

    public async Task<ApprovalDto> DecideAsync(
        Guid id,
        bool approve,
        string? decidedBy = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                       ?? throw new NotFoundException("审批", id);

        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new WorkbenchException($"审批 {id} 已经是 {approval.Status} 状态，不能再决定。");
        }

        if (approval.ExpiresAt <= clock.UtcNow)
        {
            approval.Status = ApprovalStatus.Expired;
            approval.DecidedAt = clock.UtcNow;
            approval.DecidedBy = "system";
            approval.DecisionReason = "超时未处理，自动过期";
            await db.SaveChangesAsync(cancellationToken);
            throw new WorkbenchException($"审批 {id} 已过期。");
        }

        if (!approve && string.IsNullOrWhiteSpace(reason))
        {
            throw new WorkbenchException("驳回审批必须填写理由。");
        }

        approval.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        approval.DecidedBy = decidedBy ?? "human";
        approval.DecisionReason = reason;
        approval.DecidedAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(approval);
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

    public async Task<ApprovalDto> MarkExecutedAsync(
        Guid id,
        string? resultSummary,
        CancellationToken cancellationToken = default)
    {
        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                       ?? throw new NotFoundException("审批", id);

        if (approval.Status != ApprovalStatus.Approved)
        {
            throw new WorkbenchException($"审批 {id} 处于 {approval.Status} 状态，不能标记为已执行。");
        }

        approval.Status = ApprovalStatus.Executed;
        approval.ResultSummary = resultSummary;
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(approval);
    }

    public async Task<int> ExpireOverdueAsync(CancellationToken cancellationToken = default)    {
        var now = clock.UtcNow;
        var overdue = await db.Approvals
            .Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var approval in overdue)
        {
            approval.Status = ApprovalStatus.Expired;
            approval.DecidedAt = now;
            approval.DecidedBy = "system";
            approval.DecisionReason = "超时未处理，自动过期";
        }

        if (overdue.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return overdue.Count;
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
        a.DecidedAt);
}
