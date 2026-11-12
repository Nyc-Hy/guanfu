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
    IApprovalGate approvalGate,
    IToolAuditService audit,
    IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "db_query")]
    [Description(
        "只读查询项目配置的 SQL Server 数据库。默认放行（只记审计日志），返回列名与行数据。" +
        "只放行能确认为只读的语句（SELECT / WITH…SELECT 等）；" +
        "写操作或认不出来的语句会被拒，请改用 db_execute（会走人工审批）。")]
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

            // 先判定语句性质：认不出来也要拦，而且要说清「请改用 db_execute」。
            // 拦下来的记 denied，不记 error —— 这不是执行失败，是策略拦的。
            var analysis = runner.Analyze(sql);
            if (analysis.Kind != SqlStatementKind.Read)
            {
                var reason = SqlStatementClassifier.BuildRejection(analysis, sql);

                await audit.RecordAsync(new ToolAuditEntry(
                    "db_query", callerId, projectId, null, sql,
                    RequiredApproval: false, ApprovalId: null,
                    Outcome: "denied",
                    ResultSummary: reason,
                    RowsAffected: null,
                    DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

                return new DbQueryToolResult(false, [], [], false, maxRows, 0, reason);
            }

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
        "如果审批是在之后才通过的，用 approvalId 再次调用本工具即可直接执行——这就是令牌式续跑。" +
        "注意：续跑时执行的 SQL 必须和当初审批的那条完全一致，否则会被拒。")]
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

        // 先把连接取出来：审批单上要写清楚是哪个库，人批的时候才知道自己在批什么
        var config = await connections.GetAsync(projectId, null, cancellationToken)
                     ?? throw new WorkbenchException("该项目没有配置数据库连接。");

        // 表级规则：连接上配了「这张表免审批写」就不用等人工。
        // 认不出目标表时按「要审批」处理 —— 宁可多问一次。
        var targetTable = SqlTargetTable.TryExtract(sql);
        var requiresApproval = await connections.RequiresApprovalAsync(
            config.Id, targetTable, cancellationToken);

        if (!requiresApproval)
        {
            return await ExecuteWithoutApprovalAsync(
                config, sql, callerId, taskId, projectId, targetTable, started, cancellationToken);
        }

        var gate = await approvalGate.AcquireAsync(
            "db_execute", "db.write", sql,
            $"对 {config.Server}/{config.Database} 执行写操作：{sql}",
            callerId, taskId, projectId, approvalId, waitSeconds, cancellationToken);

        if (!gate.Approved)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: true, ApprovalId: gate.ApprovalId,
                Outcome: gate.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                ResultSummary: gate.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(
                gate.ApprovalId,
                gate.Status,
                null,
                gate.Message);
        }

        try
        {
            var result = await runner.ExecuteAsync(config, sql, cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: true, ApprovalId: gate.ApprovalId,
                Outcome: "success",
                ResultSummary: $"影响 {result.RowsAffected} 行",
                RowsAffected: result.RowsAffected,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(gate.ApprovalId, nameof(ApprovalStatus.Executed), result.RowsAffected, "已审批并执行。");
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: true, ApprovalId: gate.ApprovalId,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(gate.ApprovalId, nameof(ApprovalStatus.Executing), null, $"执行失败: {ex.Message}");
        }
        finally
        {
            // 审批已经占住了，收尾一下 —— 不收尾它就卡在 Executing
            try
            {
                await approvalGate.CompleteAsync(gate.ApprovalId, null, cancellationToken);
            }
            catch (Exception)
            {
                // 收尾失败不能盖掉真正的执行结果
            }
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

    /// <summary>
    /// 免审批直接执行。
    ///
    /// 走的还是同一条执行路径与同一套审计 —— 免的只是「等人工点头」这一步，
    /// 不是免记录。审计里 RequiredApproval=false 标出来，事后能分清。
    /// </summary>
    private async Task<DbExecuteToolResult> ExecuteWithoutApprovalAsync(
        DbConnectionConfig config,
        string sql,
        string? callerId,
        Guid? taskId,
        Guid projectId,
        string? targetTable,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.ExecuteAsync(config, sql, cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"影响 {result.RowsAffected} 行（表 {targetTable ?? "?"} 免审批）",
                RowsAffected: result.RowsAffected,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(
                Guid.Empty,
                nameof(ApprovalStatus.Executed),
                result.RowsAffected,
                $"已执行（表 {targetTable ?? "?"} 配了免审批）。");
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_execute", callerId, projectId, taskId, sql,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new DbExecuteToolResult(Guid.Empty, "error", null, $"执行失败: {ex.Message}");
        }
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
