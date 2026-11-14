using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// 验哈希链：审计记录有没有被人在库文件层面改过。
    /// </summary>
    Task<AuditChainReport> VerifyChainAsync(CancellationToken cancellationToken = default);
}

/// <summary>哈希链校验结果。</summary>
public sealed record AuditChainReport(
    bool Intact,
    int Total,
    int Chained,
    Guid? FirstBrokenId,
    string? Reason);

public sealed record ToolAuditDto(
    Guid Id,
    string ToolName,
    string? CallerId,
    Guid? ProjectId,
    Guid? TaskId,
    /// <summary>
    /// 任务编号（如 REQ-001）。界面上要给人看的是它，不是那串 GUID ——
    /// 人认不出 GUID 指的是哪个任务。
    /// </summary>
    string? TaskKey,
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
    /// <summary>
    /// ResultSummary 的长度上限（4 KB，与列宽 HasMaxLength(4000) 一致）。
    /// shell / SQL 的结果可能是几 MB 的文本，原样入库会把审计表撑爆，
    /// 之后 ListAsync 一次就把整页结果拉进内存。超限截断并明确标注。
    /// </summary>
    internal const int ResultSummaryMaxChars = 4000;

    internal const string TruncationMarker = "…[已截断]";

    public async Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default)
    {
        // 读「上一条的哈希」和「写这一条」必须是一步 ——
        // 两步之间插进来一条，链就断了。所以这里用闸门把整段串起来。
        await ChainGate.WaitAsync(cancellationToken);
        try
        {
            var last = await db.ToolCallAudits
                .AsNoTracking()
                .OrderByDescending(a => a.Sequence)
                .FirstOrDefaultAsync(cancellationToken);

            var record = new ToolCallAudit
            {
                ToolName = entry.ToolName,
                CallerId = entry.CallerId,
                ProjectId = entry.ProjectId,
                TaskId = entry.TaskId,
                Arguments = entry.Arguments,
                RequiredApproval = entry.RequiredApproval,
                ApprovalId = entry.ApprovalId,
                Outcome = entry.Outcome,
                ResultSummary = CapSummary(entry.ResultSummary),
                RowsAffected = entry.RowsAffected,
                DurationMs = entry.DurationMs,
                CreatedAt = clock.UtcNow,
                Sequence = (last?.Sequence ?? 0) + 1,
                PrevHash = last?.Hash
            };

            record.Hash = ComputeHash(record, record.PrevHash);

            db.ToolCallAudits.Add(record);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            ChainGate.Release();
        }
    }

    /// <summary>
    /// 写链的闸门。一个进程一个库，串行化「读上一条 + 写这一条」就够了。
    /// </summary>
    private static readonly SemaphoreSlim ChainGate = new(1, 1);

    /// <summary>
    /// 一条审计记录的哈希：内容 + 上一条的哈希。
    ///
    /// 字段之间用 单元分隔符（U+001F）隔开 —— 不用普通分隔符的话，
    /// 字段值里出现分隔符就能伪造出另一条记录的哈希。
    /// </summary>
    internal static string ComputeHash(ToolCallAudit record, string? prevHash)
    {
        var canonical = string.Join('\u001F',
            prevHash ?? string.Empty,
            record.Sequence.ToString(CultureInfo.InvariantCulture),
            record.ToolName,
            record.CallerId ?? string.Empty,
            record.ProjectId?.ToString() ?? string.Empty,
            record.TaskId?.ToString() ?? string.Empty,
            record.Arguments,
            record.RequiredApproval ? "1" : "0",
            record.ApprovalId?.ToString() ?? string.Empty,
            record.Outcome,
            record.ResultSummary ?? string.Empty,
            record.RowsAffected?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            record.DurationMs.ToString(CultureInfo.InvariantCulture),
            record.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// 验哈希链：从头走一遍，重算每条的哈希，看对不对得上。
    ///
    /// 改一条内容 → 那条的哈希对不上；删一条 → 后一条的链接断了。
    /// 两种都能查出来。
    /// </summary>
    public async Task<AuditChainReport> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.ToolCallAudits
            .AsNoTracking()
            .OrderBy(a => a.Sequence)
            .ToListAsync(cancellationToken);

        string? expectedPrev = null;
        var chained = 0;

        foreach (var row in rows)
        {
            // 链启用前的老数据没有哈希：不参与校验，也不打断链。
            if (string.IsNullOrEmpty(row.Hash))
            {
                continue;
            }

            var recomputed = ComputeHash(row, row.PrevHash);
            if (!string.Equals(recomputed, row.Hash, StringComparison.Ordinal))
            {
                return new AuditChainReport(
                    false, rows.Count, chained, row.Id,
                    "这条记录的内容跟哈希对不上 —— 它被改过。");
            }

            if (!string.Equals(row.PrevHash, expectedPrev, StringComparison.Ordinal))
            {
                return new AuditChainReport(
                    false, rows.Count, chained, row.Id,
                    "跟上一条的链接断了 —— 中间有记录被删掉或换过。");
            }

            expectedPrev = row.Hash;
            chained++;
        }

        return new AuditChainReport(true, rows.Count, chained, null, null);
    }

    /// <summary>截断到上限并留标记（标记也算在上限内）；null / 短文本原样返回。</summary>
    internal static string? CapSummary(string? summary)
    {
        if (summary is null || summary.Length <= ResultSummaryMaxChars)
        {
            return summary;
        }

        var keep = ResultSummaryMaxChars - TruncationMarker.Length;
        return string.Concat(summary.AsSpan(0, keep), TruncationMarker);
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

        // 任务编号一次性捞出来（别一行一次查 —— 审计页一页 20 行就是 20 次查库）。
        var taskIds = rows
            .Where(a => a.TaskId is not null)
            .Select(a => a.TaskId!.Value)
            .Distinct()
            .ToList();

        var keys = taskIds.Count == 0
            ? []
            : await db.Tasks
                .AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Key })
                .ToListAsync(cancellationToken);

        var keyByTask = keys.ToDictionary(x => x.Id, x => x.Key);

        return rows.Select(a => new ToolAuditDto(
            a.Id,
            a.ToolName,
            a.CallerId,
            a.ProjectId,
            a.TaskId,
            a.TaskId is not null && keyByTask.TryGetValue(a.TaskId.Value, out var key) ? key : null,
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
