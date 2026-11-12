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

    /// <summary>查工具调用审计。审计表一直在写，但没有入口能查 —— 补上。</summary>
    Task<IReadOnlyList<ToolAuditDto>> ListAsync(
        string? toolName = null,
        Guid? taskId = null,
        Guid? projectId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}

public sealed record ToolAuditDto(
    Guid Id,
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
    long DurationMs,
    DateTimeOffset CreatedAt);

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

    public async Task<IReadOnlyList<ToolAuditDto>> ListAsync(
        string? toolName = null,
        Guid? taskId = null,
        Guid? projectId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = db.ToolCallAudits.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            query = query.Where(a => a.ToolName == toolName);
        }

        if (taskId is not null)
        {
            query = query.Where(a => a.TaskId == taskId);
        }

        if (projectId is not null)
        {
            query = query.Where(a => a.ProjectId == projectId);
        }

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);

        return rows.Select(a => new ToolAuditDto(
            a.Id,
            a.ToolName,
            a.CallerId,
            a.ProjectId,
            a.TaskId,
            a.Arguments,
            a.RequiredApproval,
            a.ApprovalId,
            a.Outcome,
            a.ResultSummary,
            a.RowsAffected,
            a.DurationMs,
            a.CreatedAt)).ToList();
    }
}
