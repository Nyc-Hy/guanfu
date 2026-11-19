using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

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
    long DurationMs,
    /// <summary>这把钥匙背后的人（成员显示名）。</summary>
    string? MemberName = null);

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
    DateTimeOffset CreatedAt,
    /// <summary>这把钥匙背后的人（成员显示名）。</summary>
    string? MemberName = null);

/// <summary>工具调用审计。append-only：只追加，不修改不删除。</summary>
public sealed class ToolAuditService(
    IAppDbContext db,
    IClock clock,
    ICurrentMemberScope? currentMember = null) : IToolAuditService
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
                PrevHash = last?.Hash,
                MemberName = entry.MemberName,
                // 新记录一律用第 2 版格式（带成员）。
                HashVersion = CurrentHashVersion
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
    /// <summary>当前写入用的哈希格式版本。</summary>
    internal const int CurrentHashVersion = 2;

    /// <summary>
    /// 一条审计记录的哈希：内容 + 上一条的哈希。
    ///
    /// **按记录自己的版本算** —— 链的格式改过（加了成员那一列），
    /// 老记录仍是第 1 版格式，新记录是第 2 版，两代同处一条链里也验得过。
    /// 版本这一列不在哈希里，但改它一样会被发现（版本对不上 → 哈希对不上）。
    /// </summary>
    internal static string ComputeHash(ToolCallAudit record, string? prevHash) =>
        record.HashVersion >= 2
            ? ComputeHashV2(record, prevHash)
            : ComputeHashV1(record, prevHash);

    /// <summary>第 1 版格式（老记录）：字段顺序与当年一模一样，别动。</summary>
    private static string ComputeHashV1(ToolCallAudit record, string? prevHash)
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

        return Hash(canonical);
    }

    /// <summary>第 2 版格式：在第 1 版的基础上把「这把钥匙背后的人」也算进去。</summary>
    private static string ComputeHashV2(ToolCallAudit record, string? prevHash)
    {
        var canonical = string.Join('\u001F',
            "v2",
            prevHash ?? string.Empty,
            record.Sequence.ToString(CultureInfo.InvariantCulture),
            record.ToolName,
            record.CallerId ?? string.Empty,
            record.MemberName ?? string.Empty,
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

        return Hash(canonical);
    }

    private static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>
    /// 验哈希链：从头走一遍，重算每条的哈希，看对不对得上。
    ///
    /// 改一条内容 → 那条的哈希对不上；删一条 → 后一条的链接断了。
    /// 两种都能查出来。
    ///
    /// **分批走，不把全表装进内存**：审计表只增不减（企业合规要求留痕），
    /// 十万条记录一次性 ToList 会吃光内存，验链本身就成了故障源。
    /// </summary>
    public async Task<AuditChainReport> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        string? expectedPrev = null;
        var chained = 0;
        var total = 0;
        long lastSequence = 0;

        while (true)
        {
            // 按 Sequence 游标翻页（不能用 Skip：越翻越慢，而且中途可能有新记录插进来）。
            var rows = await db.ToolCallAudits
                .AsNoTracking()
                .Where(a => a.Sequence > lastSequence)
                .OrderBy(a => a.Sequence)
                .Take(VerifyBatchSize)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                total++;

                // 链启用前的老数据没有哈希：不参与校验，也不打断链。
                if (string.IsNullOrEmpty(row.Hash))
                {
                    continue;
                }

                var recomputed = ComputeHash(row, row.PrevHash);
                if (!string.Equals(recomputed, row.Hash, StringComparison.Ordinal))
                {
                    return new AuditChainReport(
                        false, total, chained, row.Id,
                        "这条记录的内容跟哈希对不上 —— 它被改过。");
                }

                if (!string.Equals(row.PrevHash, expectedPrev, StringComparison.Ordinal))
                {
                    return new AuditChainReport(
                        false, total, chained, row.Id,
                        "跟上一条的链接断了 —— 中间有记录被删掉或换过。");
                }

                expectedPrev = row.Hash;
                chained++;
            }

            lastSequence = rows[^1].Sequence;

            if (rows.Count < VerifyBatchSize)
            {
                break;
            }
        }

        return new AuditChainReport(true, total, chained, null, null);
    }

    /// <summary>验链时一批读多少条。</summary>
    private const int VerifyBatchSize = 500;

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

        // 可见性：普通成员只看得到**自己任务**下的记录。
        // 没挂任务的记录（列项目之类的环境调用）谁都能看 —— 那不是谁家的私事。
        var scope = currentMember?.Current;

        if (scope is not null && !scope.IsAdmin)
        {
            var visibleTaskIds = db.Tasks
                .AsNoTracking()
                .VisibleTo(scope)
                .Select(t => t.Id);

            query = query.Where(a => a.TaskId == null || visibleTaskIds.Contains(a.TaskId.Value));
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
            a.CreatedAt,
            a.MemberName)).ToList();
    }
}
