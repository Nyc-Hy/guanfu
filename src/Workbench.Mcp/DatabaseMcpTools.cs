using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Infrastructure;

namespace Workbench.Mcp;

public sealed record DbQueryToolResult(
    bool Ok,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    int RowLimit,
    long DurationMs,
    string? Error = null);

public sealed record DbExecuteToolResult(
    Guid ApprovalId,
    string ApprovalStatus,
    int? RowsAffected,
    string Message);

/// <summary>
/// 数据库 MCP 工具。策略（决策 3）：
///   - 读：默认放心读，直接执行，只记审计日志；
///   - 写 / 修改 / DDL：必须人工审批通过后才真正执行。
/// </summary>
[McpServerToolType]
public sealed class DatabaseMcpTools(
    IDbConnectionService connections,
    ISqlCommandRunner runner,
    IApprovalService approvals,
    IToolAuditService audit,
    IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "db_query")]
    [Description("只读查询项目配置的 SQL Server 数据库。默认放行（只记审计日志），返回列名与行数据。")]
    public async Task<DbQueryToolResult> QueryAsync(
        Guid projectId,
        string sql,
        string? callerId = null,
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var config = await connections.GetAsync(projectId, null, cancellationToken)
                         ?? throw new WorkbenchException("该项目没有配置数据库连接。");

            var result = await runner.QueryAsync(config, sql, Math.Clamp(maxRows, 1, 5000), cancellationToken);
            var duration = (long)(clock.UtcNow - started).TotalMilliseconds;

            await audit.RecordAsync(new ToolAuditEntry(
                "db_query", callerId, projectId, null, sql,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"{result.Rows.Count} 行",
                RowsAffected: null,
                DurationMs: duration), cancellationToken);

            return new DbQueryToolResult(
                true, result.Columns, result.Rows, result.Truncated, result.RowLimit, duration);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_query", callerId, projectId, null, sql,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            // 不抛异常：MCP 会把异常吞成一句泛化信息，agent 就看不到原因了
            return new DbQueryToolResult(false, [], [], false, maxRows, 0, ex.Message);
        }
    }

    [McpServerTool(Name = "db_execute")]
    [McpRequiresApproval]
    [Description(
        "执行写操作（INSERT / UPDATE / DELETE / DDL）。必须人工审批通过后才会真正执行。" +
        "首次调用会生成一条审批请求并等待人工决定（waitSeconds 内没批就返回 pending）。" +
        "如果审批是在之后才通过的，用 approvalId 再次调用本工具即可直接执行——这就是令牌式续跑。")]
    public async Task<DbExecuteToolResult> ExecuteAsync(
        Guid projectId,
        string sql,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        // ---- 令牌式续跑：拿着已批准的审批直接执行 ----
        if (approvalId is not null)
        {
            var existing = await approvals.GetAsync(approvalId.Value, cancellationToken)
                           ?? throw new WorkbenchException($"审批 {approvalId} 不存在。");

            if (existing.Status != nameof(ApprovalStatus.Approved))
            {
                return new DbExecuteToolResult(
                    existing.Id, existing.Status, null,
                    $"审批当前状态为 {existing.Status}，还不能执行。");
            }

            return await RunApprovedAsync(projectId, sql, callerId, taskId, approvalId.Value, started, cancellationToken);
        }

        var config = await connections.GetAsync(projectId, null, cancellationToken)
                     ?? throw new WorkbenchException("该项目没有配置数据库连接。");

        var summary = $"对 {config.Server}/{config.Database} 执行写操作";
        var approval = await approvals.RequestAsync(
            "db_execute", "db.write", sql, summary, callerId, taskId, projectId,
            ApprovalService.DefaultTtlMinutes, cancellationToken);

        var decided = await approvals.AwaitAsync(approval.Id, Math.Clamp(waitSeconds, 1, 300), cancellationToken);

        if (decided.Status != nameof(ApprovalStatus.Approved))
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: true, ApprovalId: approval.Id,
                Outcome: decided.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                ResultSummary: decided.DecisionReason ?? decided.Status,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(
                approval.Id,
                decided.Status,
                null,
                decided.Status == nameof(ApprovalStatus.Rejected)
                    ? $"已被驳回，未执行。原因: {decided.DecisionReason}"
                    : $"仍在等待人工审批（当前状态 {decided.Status}）。批准后带 approvalId={approval.Id} 再次调用即可执行。");
        }

        return await RunApprovedAsync(projectId, sql, callerId, taskId, approval.Id, started, cancellationToken);
    }

    private async Task<DbExecuteToolResult> RunApprovedAsync(
        Guid projectId,
        string sql,
        string? callerId,
        Guid? taskId,
        Guid approvalId,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = await connections.GetAsync(projectId, null, cancellationToken)
                         ?? throw new WorkbenchException("该项目没有配置数据库连接。");

            var result = await runner.ExecuteAsync(config, sql, cancellationToken);
            await approvals.MarkExecutedAsync(
                approvalId, $"影响 {result.RowsAffected} 行", cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: true, ApprovalId: approvalId,
                Outcome: "success",
                ResultSummary: $"影响 {result.RowsAffected} 行",
                RowsAffected: result.RowsAffected,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(approvalId, nameof(ApprovalStatus.Executed), result.RowsAffected, "已审批并执行。");
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: true, ApprovalId: approvalId,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(approvalId, nameof(ApprovalStatus.Approved), null, $"执行失败: {ex.Message}");
        }
    }

    [McpServerTool(Name = "db_list_connections")]
    [Description("列出项目配置的数据库连接（不返回密码）")]
    public async Task<IReadOnlyList<ConnectionInfoDto>> ListConnectionsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var configs = await connections.ListAsync(projectId, cancellationToken);
        return configs
            .Select(c => new ConnectionInfoDto(c.Id, c.Name, c.Server, c.Database, c.UserId, c.IsDefault))
            .ToList();
    }

    [McpServerTool(Name = "db_schema")]
    [Description("读取项目数据库的表结构：表名、列名、类型、是否可空、主键。默认放行，只记审计日志。")]
    public async Task<DbSchemaToolResult> SchemaAsync(
        Guid projectId,
        string? schema = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var config = await connections.GetAsync(projectId, null, cancellationToken)
                         ?? throw new WorkbenchException("该项目没有配置数据库连接。");

            var result = await runner.GetSchemaAsync(config, schema, cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "db_schema", callerId, projectId, null, schema ?? "(全部 schema)",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"{result.Tables.Count} 张表",
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbSchemaToolResult(true, result.Tables, result.Tables.Count, null);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_schema", callerId, projectId, null, schema ?? "(全部 schema)",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbSchemaToolResult(false, [], 0, ex.Message);
        }
    }
}

public sealed record DbSchemaToolResult(
    bool Ok,
    IReadOnlyList<DbTableInfo> Tables,
    int TableCount,
    string? Error = null);

public sealed record ConnectionInfoDto(
    Guid Id,
    string Name,
    string Server,
    string Database,
    string? UserId,
    bool IsDefault);
