using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application.Services;

public sealed record ToolAuditEntry(
    string ToolName,
    string? CallerId,
    Guid? ProjectId,
    Guid? TaskId,
    string Arguments,
    bool RequiredApproval,
    Guid? ApprovalId,
    string Outcome,
    string? ResultSummary,
    int? RowsAffected,
    long DurationMs);

public interface IToolAuditService
{
    Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>工具调用审计。append-only：只追加，不修改不删除。</summary>
public sealed class ToolAuditService(IAppDbContext db, IClock clock) : IToolAuditService
{
    public async Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default)
    {
        db.ToolCallAudits.Add(new ToolCallAudit
        {
            ToolName = entry.ToolName,
            CallerId = entry.CallerId,
            ProjectId = entry.ProjectId,
            TaskId = entry.TaskId,
            Arguments = entry.Arguments,
            RequiredApproval = entry.RequiredApproval,
            ApprovalId = entry.ApprovalId,
            Outcome = entry.Outcome,
            ResultSummary = entry.ResultSummary,
            RowsAffected = entry.RowsAffected,
            DurationMs = entry.DurationMs,
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
