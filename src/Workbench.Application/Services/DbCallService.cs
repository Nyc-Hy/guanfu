using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>要落库的数据库调用记录。</summary>
public sealed record DbCallRecordEntry(
    Guid? AuditId,
    Guid ProjectId,
    Guid? TaskId,
    Guid? SessionId,
    Guid? ConnectionId,
    string ToolName,
    string? Server,
    string? Database,
    string? Sql,
    string? Kind,
    IReadOnlyList<string>? TargetTables,
    int? RowsAffected,
    string? Outcome,
    Guid? ApprovalId,
    string? CallerId,
    long DurationMs);

public interface IDbCallService
{
    /// <summary>记一条数据库调用。落库失败不能把主流程带崩。</summary>
    Task RecordAsync(DbCallRecordEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查数据库调用记录。过滤条件全为 null / 空 = 不过滤。
    /// 返回 (当页数据, 总条数)。
    /// </summary>
    Task<(IReadOnlyList<DbCallRecordDto> Rows, int Total)> ListAsync(
        Guid? projectId = null,
        Guid? connectionId = null,
        string? database = null,
        string? table = null,
        string? kind = null,
        Guid? taskId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default,
        /// <summary>
        /// SQL 原文里包含什么（大小写不敏感）。
        /// 放在 CancellationToken 之后是为了不破坏现有的位置参数调用。
        /// </summary>
        string? sql = null);

    /// <summary>
    /// 搜数据库调用：SQL 原文、库名、目标表任一命中就算。
    ///
    /// 全局搜索要能答「谁跑过这条 SQL」「哪个任务动过这张表」，
    /// 所以这三个字段任一命中都算数。
    /// </summary>
    Task<IReadOnlyList<DbCallHitDto>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 数据库调用记录的读写。
///
/// 通用工具审计表（ToolCallAudit）是给「所有工具」用的，SQL 原文、目标库、
/// 目标表全埋在那行的 JSON 里 —— 查不动。这张专表把它们摊成列，
/// 让「谁在什么时候对哪个库的哪张表跑了什么 SQL、影响几行、谁批的」能筛能翻。
/// </summary>
public sealed class DbCallService(IAppDbContext db, IClock clock) : IDbCallService
{
    /// <summary>目标表列的存储上限（与 EF 配置的 HasMaxLength(2000) 一致）。</summary>
    internal const int TargetTablesMaxChars = 2000;

    /// <summary>SQL 原文列的存储上限（与 EF 配置的 HasMaxLength(16000) 一致）。</summary>
    internal const int SqlMaxChars = 16000;

    private const int ServerMaxChars = 500;
    private const int DatabaseMaxChars = 200;
    private const int ToolNameMaxChars = 200;
    private const int KindMaxChars = 32;
    private const int OutcomeMaxChars = 50;
    private const int CallerIdMaxChars = 200;

    public async Task RecordAsync(DbCallRecordEntry entry, CancellationToken cancellationToken = default)
    {
        // 任务 id 是 agent 自己填的，可能指向一个根本不存在的任务 ——
        // 直接写进去会撞外键约束，整条记录都丢。查一下存在再写。
        var taskId = entry.TaskId;
        if (taskId is not null &&
            !await db.Tasks.AnyAsync(t => t.Id == taskId.Value, cancellationToken))
        {
            taskId = null;
        }

        db.DbCallRecords.Add(new DbCallRecord
        {
            AuditId = entry.AuditId,
            ProjectId = entry.ProjectId,
            TaskId = taskId,
            SessionId = entry.SessionId,
            ConnectionId = entry.ConnectionId,
            ToolName = Cap(entry.ToolName, ToolNameMaxChars) ?? string.Empty,
            Server = Cap(entry.Server, ServerMaxChars),
            Database = Cap(entry.Database, DatabaseMaxChars),
            Sql = Cap(entry.Sql, SqlMaxChars),
            Kind = Cap(entry.Kind, KindMaxChars),
            TargetTables = JoinTables(entry.TargetTables),
            RowsAffected = entry.RowsAffected,
            Outcome = Cap(entry.Outcome, OutcomeMaxChars),
            ApprovalId = entry.ApprovalId,
            CallerId = Cap(entry.CallerId, CallerIdMaxChars),
            DurationMs = entry.DurationMs,
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<DbCallRecordDto> Rows, int Total)> ListAsync(
        Guid? projectId = null,
        Guid? connectionId = null,
        string? database = null,
        string? table = null,
        string? kind = null,
        Guid? taskId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default,
        string? sql = null)
    {
        var query = db.DbCallRecords.AsNoTracking();

        if (projectId is not null)
        {
            query = query.Where(r => r.ProjectId == projectId.Value);
        }

        if (connectionId is not null)
        {
            query = query.Where(r => r.ConnectionId == connectionId.Value);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            // 按前缀粗筛，不是精确匹配 —— 人记的是「shop 那个库」，
            // 让他把全名一字不差敲进去不合理。
            var databaseValue = database.Trim();
            query = query.Where(r => r.Database != null && r.Database.StartsWith(databaseValue));
        }

        if (!string.IsNullOrWhiteSpace(table))
        {
            // 目标表存成一列逗号分隔的字符串，只能粗筛（Contains → instr）。
            // 精确匹配要拆开逐个比，那只能放内存里做 —— 粗筛够用就够用。
            var tableValue = table.Trim();
            query = query.Where(r => r.TargetTables != null && r.TargetTables.Contains(tableValue));
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var kindValue = kind.Trim();
            query = query.Where(r => r.Kind == kindValue);
        }

        if (taskId is not null)
        {
            query = query.Where(r => r.TaskId == taskId.Value);
        }

        if (from is not null)
        {
            query = query.Where(r => r.CreatedAt >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(r => r.CreatedAt <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(sql))
        {
            // SQL 原文里包含什么。lower() 是 SQL 函数，翻译得过去；
            // 查询词的 lower() 在客户端算好当参数传。人打「delete from」要能搜到「DELETE FROM」。
            var sqlValue = sql.Trim().ToLowerInvariant();
            query = query.Where(r => r.Sql != null && r.Sql.ToLower().Contains(sqlValue));
        }

        // 总条数与当页数据分两次查，但过滤条件完全一致 ——
        // 分页是服务端的，不能把全量拉进内存再切。
        var total = await query.CountAsync(cancellationToken);

        var pageSize = Math.Clamp(limit, 1, 500);
        var skip = Math.Max(offset, 0);

        var rows = await query
            .Include(r => r.Project)
            .Include(r => r.Task)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtos = rows
            .Select(r => new DbCallRecordDto(
                r.Id,
                r.ProjectId,
                r.Project?.Name ?? string.Empty,
                r.TaskId,
                r.Task?.Key,
                r.ConnectionId,
                r.ToolName,
                r.Server,
                r.Database,
                r.Sql,
                r.Kind,
                SplitTables(r.TargetTables),
                r.RowsAffected,
                r.Outcome,
                r.ApprovalId,
                r.CallerId,
                r.DurationMs,
                r.CreatedAt))
            .ToList();

        return (dtos, total);
    }

    public async Task<IReadOnlyList<DbCallHitDto>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var value = query.Trim();

        if (value.Length == 0)
        {
            return [];
        }

        // 大小写不敏感：人打「delete from users」要能搜到「DELETE FROM Users」。
        // lower() 是 SQL 函数，翻译得过去；value 的 lower() 在客户端算好当参数传。
        var lowered = value.ToLowerInvariant();

        var rows = await db.DbCallRecords
            .AsNoTracking()
            .Where(r =>
                (r.Sql != null && r.Sql.ToLower().Contains(lowered)) ||
                (r.Database != null && r.Database.ToLower().Contains(lowered)) ||
                (r.TargetTables != null && r.TargetTables.ToLower().Contains(lowered)))
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        var taskIds = rows.Where(r => r.TaskId is not null).Select(r => r.TaskId!.Value).Distinct().ToList();

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key })
            .ToListAsync(cancellationToken);

        var projectById = projects.ToDictionary(p => p.Id);
        var taskById = tasks.ToDictionary(t => t.Id);

        return rows.Select(r =>
        {
            taskById.TryGetValue(r.TaskId ?? Guid.Empty, out var task);

            return new DbCallHitDto(
                r.Id,
                r.TaskId,
                task?.Key,
                projectById.TryGetValue(r.ProjectId, out var project) ? project.Name : "—",
                r.Database,
                r.Sql,
                r.Kind,
                r.Outcome,
                r.CallerId,
                r.CreatedAt);
        }).ToList();
    }

    /// <summary>目标表列表 → 逗号分隔字符串（超长截断，末尾留标记）。</summary>
    private static string? JoinTables(IReadOnlyList<string>? tables)
    {
        if (tables is null || tables.Count == 0)
        {
            return null;
        }

        var kept = new List<string>();
        var length = 0;
        var truncated = false;

        foreach (var table in tables)
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                continue;
            }

            var value = table.Trim();
            var extra = kept.Count == 0 ? value.Length : value.Length + 1;

            if (length + extra > TargetTablesMaxChars - 1)
            {
                truncated = true;
                break;
            }

            kept.Add(value);
            length += extra;
        }

        if (kept.Count == 0)
        {
            return null;
        }

        var text = string.Join(",", kept);
        return truncated ? text + "…" : text;
    }

    /// <summary>逗号分隔字符串 → 目标表列表（空的返回空列表）。</summary>
    private static IReadOnlyList<string> SplitTables(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();

        // 截断标记不是表名的一部分，还原成原样。
        if (parts.Count > 0 && parts[^1].EndsWith('…'))
        {
            parts[^1] = parts[^1][..^1];
            if (parts[^1].Length == 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        return parts;
    }

    /// <summary>超长截断（null / 空白原样返回）。</summary>
    private static string? Cap(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, max - 1), "…");
    }
}
