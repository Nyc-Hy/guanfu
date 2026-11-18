using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Infrastructure;

namespace Guanfu.Mcp;

public sealed record DbQueryToolResult(
    bool Ok,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    int RowLimit,
    long DurationMs,
    string? Error = null);

/// <summary>
/// db_execute 的统一返回。与写工具 <c>WriteToolResult</c> 对齐：
/// <see cref="Ok"/> 表示「真的执行成功了」，<see cref="Error"/> 给出失败原因。
/// 审批被消费但执行失败时，<see cref="ApprovalStatus"/> 仍是 Executed（审批用掉了），
/// Message 里会带上「审批已消费，请重新申请」，免得 agent 以为还能拿同一条审批续跑。
/// </summary>
public sealed record DbExecuteToolResult(
    bool Ok,
    Guid ApprovalId,
    string ApprovalStatus,
    int? RowsAffected,
    string Message,
    string? Error = null);

public sealed record DbListRulesToolResult(
    bool Ok,
    Guid ConnectionId,
    string Server,
    string Database,
    IReadOnlyList<DbTableRuleDto> Rules,
    string? Error = null);

public sealed record DbTestConnectionToolResult(
    bool Ok,
    string? Error = null);

/// <summary>
/// 数据库 MCP 工具。策略（决策 3）：
///   - 读：默认放心读，直接执行，只记审计日志；
///   - 写 / 修改 / DDL：必须人工审批通过后才真正执行。
///
/// 免审批判定（P0-1）：用 <see cref="SqlWriteTargetAnalyzer"/> 把 SQL 拆成**逐语句**，
/// 每一条都必须是能认出目标表的写，且所有目标表的规则都写了 WriteRequiresApproval=false，
/// 才免审批。任何读 / Unknown / 解析失败 / 混合语句一律走人工审批 —— 宁可多问一次。
///
/// 审批绑连接（P0-2）：payload 里带 connectionId + server + database + sql，
/// 续跑前重新取默认连接，connectionId 与审批时不同就拒「默认连接已变化，请重新申请」。
/// </summary>
[McpServerToolType]
public sealed class DatabaseMcpTools(
    IDbConnectionService connections,
    ISqlCommandRunner runner,
    IApprovalGate approvalGate,
    IApprovalService approvals,
    IToolAuditService audit,
    IClock clock,
    IProjectScopeResolver? scopeResolver = null,
    IDbCallService? dbCalls = null)
{
    /// <summary>
    /// 算出这次调用算在哪个项目上。
    ///
    /// 优先用会话绑定的项目（agent 在哪个仓库干活就落到哪个项目），
    /// 没有会话时才看调用方显式传的 projectId。两边都对不上就拒 ——
    /// 不能让 agent 拿着 A 项目的会话去动 B 项目。
    /// </summary>
    private async Task<ProjectScope> ResolveScopeAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (scopeResolver is not null)
        {
            return await scopeResolver.ResolveAsync(projectId, cancellationToken);
        }

        if (projectId is Guid pid)
        {
            return new ProjectScope(pid, string.Empty, null, null, null);
        }

        throw new GuanfuException(
            "当前调用没有绑定项目，也没有传 projectId。请先调用 guanfu_bind_session。");
    }

    [McpServerTool(Name = "db_query")]
    [Description(
        "只读查询项目配置的 SQL Server 数据库。默认放行（只记审计日志），返回列名与行数据。" +
        "只放行能确认为只读的语句（SELECT / WITH…SELECT 等）；" +
        "写操作或认不出来的语句会被拒，请改用 db_execute（会走人工审批）。" +
        "已绑定会话时不用传 projectId。")]
    public async Task<DbQueryToolResult> QueryAsync(
        Guid? projectId = null,
        string? sql = null,
        string? callerId = null,
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        // 出错时也要能记一笔「谁在什么时候想干什么」，
        // 所以连接与作用域先声明在外面，try 里赋值。
        ProjectScope? scope = null;
        DbConnectionConfig? config = null;
        SqlStatementAnalysis? analysis = null;

        try
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new GuanfuException("SQL 不能为空。");
            }

            scope = await ResolveScopeAsync(projectId, cancellationToken);

            config = await connections.GetAsync(scope.ProjectId, null, cancellationToken)
                     ?? throw new GuanfuException("该项目没有配置数据库连接。");

            // 先判定语句性质：认不出来也要拦，而且要说清「请改用 db_execute」。
            // 拦下来的记 denied，不记 error —— 这不是执行失败，是策略拦的。
            analysis = runner.Analyze(sql);
            if (analysis.Kind != SqlStatementKind.Read)
            {
                var reason = SqlStatementClassifier.BuildRejection(analysis, sql);

                await audit.RecordAsync(new ToolAuditEntry(
                    "db_query", callerId ?? scope.AgentId, scope.ProjectId, scope.TaskId, sql,
                    RequiredApproval: false, ApprovalId: null,
                    Outcome: "denied",
                    ResultSummary: reason,
                    RowsAffected: null,
                    DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

                // 读写判定直接复用上面那次分类，不再另判一遍。
                await RecordDbCallAsync(
                    "db_query", scope, null, config, sql,
                    kind: analysis.Kind.ToString(),
                    targetTables: SqlWriteTargetAnalyzer.Analyze(sql).AllTargetTables,
                    rowsAffected: null,
                    outcome: "denied",
                    approvalId: null,
                    callerId: callerId ?? scope.AgentId,
                    started, cancellationToken);

                return new DbQueryToolResult(false, [], [], false, maxRows, 0, reason);
            }

            // 表级规则：AllowRead=false 的表不许读。
            // 规则以前只存不读，配了「不许读」的表照样被读走 —— 这里逐张表问一句。
            var readTables = SqlReadTargetExtractor.Extract(sql);
            foreach (var table in readTables)
            {
                if (await connections.IsReadAllowedAsync(config.Id, table, cancellationToken))
                {
                    continue;
                }

                var readReason =
                    $"表 {table} 配了「不许读」（AllowRead=false），这条查询被拒绝。" +
                    "要读这张表请先让人改规则，或改用 db_execute（写操作走人工审批）。";

                await audit.RecordAsync(new ToolAuditEntry(
                    "db_query", callerId ?? scope.AgentId, scope.ProjectId, scope.TaskId, sql,
                    RequiredApproval: false, ApprovalId: null,
                    Outcome: "denied",
                    ResultSummary: readReason,
                    RowsAffected: null,
                    DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

                await RecordDbCallAsync(
                    "db_query", scope, null, config, sql,
                    kind: "Read",
                    targetTables: readTables,
                    rowsAffected: null,
                    outcome: "denied",
                    approvalId: null,
                    callerId: callerId ?? scope.AgentId,
                    started, cancellationToken);

                return new DbQueryToolResult(false, [], [], false, maxRows, 0, readReason);
            }

            var result = await runner.QueryAsync(config, sql, Math.Clamp(maxRows, 1, 5000), cancellationToken);
            var duration = (long)(clock.UtcNow - started).TotalMilliseconds;

            await audit.RecordAsync(new ToolAuditEntry(
                "db_query", callerId ?? scope.AgentId, scope.ProjectId, scope.TaskId, sql,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"{result.Rows.Count} 行",
                RowsAffected: null,
                DurationMs: duration), cancellationToken);

            await RecordDbCallAsync(
                "db_query", scope, null, config, sql,
                kind: "Read",
                targetTables: readTables,
                rowsAffected: result.Rows.Count,
                outcome: "success",
                approvalId: null,
                callerId: callerId ?? scope.AgentId,
                started, cancellationToken);

            return new DbQueryToolResult(
                true, result.Columns, result.Rows, result.Truncated, result.RowLimit, duration);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "db_query", callerId, projectId, null, sql ?? string.Empty,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            // 作用域都没解析出来就没什么可记的（记录要有项目归属）。
            if (scope is not null)
            {
                await RecordDbCallAsync(
                    "db_query", scope, null, config, sql,
                    kind: analysis?.Kind.ToString() ?? "Unknown",
                    targetTables: null,
                    rowsAffected: null,
                    outcome: "error",
                    approvalId: null,
                    callerId: callerId ?? scope.AgentId,
                    started, cancellationToken);
            }

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
        "注意：续跑时执行的 SQL 必须和当初审批的那条完全一致，且默认数据库连接不能变，否则会被拒。" +
        "只有当每一条语句都是可识别目标表的写、且这些表都配了免审批写时，才不用等人工。" +
        "已绑定会话时不用传 projectId。")]
    public async Task<DbExecuteToolResult> ExecuteAsync(
        Guid? projectId = null,
        string? sql = null,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        // 出错时也要能记一笔，作用域与连接先声明在外面。
        ProjectScope? scope = null;
        DbConnectionConfig? config = null;

        try
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new GuanfuException("SQL 不能为空。");
            }

            scope = await ResolveScopeAsync(projectId, cancellationToken);

            // 会话认领的任务自动带上：审批单要挂在正确的任务上。
            taskId ??= scope.TaskId;

            // 「谁调的」：调用方没传 callerId 时落到会话里的 agent。
            var requestedBy = callerId ?? scope.AgentId;

            // 读写性质直接复用现成的分类器（db_query 用的也是它），不另写一套判定。
            var kind = runner.Analyze(sql).Kind.ToString();

            // 先把连接取出来：审批单上要写清楚是哪个库，人批的时候才知道自己在批什么。
            // 取不到连接不抛异常 —— 返回结构化错误，agent 才看得到原因。
            config = await connections.GetAsync(scope.ProjectId, null, cancellationToken);
            if (config is null)
            {
                await RecordDbCallAsync(
                    "db_execute", scope, taskId, null, sql,
                    kind: kind,
                    targetTables: null,
                    rowsAffected: null,
                    outcome: "error",
                    approvalId: null,
                    callerId: requestedBy,
                    started, cancellationToken);

                return Fail("该项目没有配置数据库连接。");
            }

            // 续跑前置校验：审批当初绑的是哪个连接？现在默认连接还是它吗？
            // 变了就在**消费审批之前**拒掉 —— 不白白用掉一条审批。
            if (approvalId is not null)
            {
                var boundConnectionId = await GetBoundConnectionIdAsync(approvalId.Value, cancellationToken);
                if (boundConnectionId is not null && boundConnectionId != config.Id)
                {
                    await AuditAsync("db_execute", requestedBy, scope.ProjectId, taskId, sql,
                        requiredApproval: true, approvalId: approvalId, outcome: "denied",
                        summary: "默认连接已变化，请重新申请。", started, rowsAffected: null, cancellationToken);

                    await RecordDbCallAsync(
                        "db_execute", scope, taskId, config, sql,
                        kind: kind,
                        targetTables: null,
                        rowsAffected: null,
                        outcome: "denied",
                        approvalId: approvalId,
                        callerId: requestedBy,
                        started, cancellationToken);

                    return Fail("默认连接已变化，请重新申请。", approvalId.Value);
                }
            }

            // 逐语句分析写目标。认不出 / 有读 / 有 Unknown / 解析失败 → AllIdentifiableWrites=false。
            var analysis = SqlWriteTargetAnalyzer.Analyze(sql);

            // 目标表：写目标优先；写目标认不出（纯读语句走 db_execute）时退回读目标。
            var targetTables = analysis.AllTargetTables.Count > 0
                ? analysis.AllTargetTables
                : SqlReadTargetExtractor.Extract(sql);

            // 禁写表：配了「禁止写」的表写不进去 —— 连人工审批都不放行。
            // 审批是流程，禁写才是保证：真正不能碰的表得有一道硬门。
            foreach (var table in analysis.AllTargetTables)
            {
                if (await connections.IsWriteAllowedAsync(config.Id, table, cancellationToken))
                {
                    continue;
                }

                var forbiddenReason =
                    $"表 {table} 配了「禁止写」，写操作已被拒绝（人工审批也不放行）。" +
                    "要写这张表，请先让人把规则改掉。";

                await AuditAsync("db_execute", requestedBy, scope.ProjectId, taskId, sql,
                    requiredApproval: true, approvalId: approvalId, outcome: "denied",
                    summary: forbiddenReason, started, rowsAffected: null, cancellationToken);

                await RecordDbCallAsync(
                    "db_execute", scope, taskId, config, sql,
                    kind: kind,
                    targetTables: analysis.AllTargetTables,
                    rowsAffected: null,
                    outcome: "denied",
                    approvalId: approvalId,
                    callerId: requestedBy,
                    started, cancellationToken);

                return Fail(forbiddenReason, approvalId ?? Guid.Empty, nameof(ApprovalStatus.Rejected));
            }

            // 免审批判定：每一条都是可识别的写，且**所有**目标表规则都免审批，才免。
            var requiresApproval = true;
            if (analysis.AllIdentifiableWrites)
            {
                requiresApproval = false;
                foreach (var table in analysis.AllTargetTables)
                {
                    if (await connections.RequiresApprovalAsync(config.Id, table, cancellationToken))
                    {
                        requiresApproval = true;
                        break;
                    }
                }
            }

            if (!requiresApproval)
            {
                return await ExecuteWithoutApprovalAsync(
                    config, sql, requestedBy, taskId, scope, kind, targetTables, analysis, started, cancellationToken);
            }

            // payload 绑连接：connectionId + server + database + sql。
            // 续跑时闸门会比对 payload 哈希，连接或 SQL 变了都对不上。
            var payload = GuanfuJson.Serialize(new
            {
                connectionId = config.Id,
                server = config.Server,
                database = config.Database,
                sql
            });

            var requestedConnectionId = config.Id;
            var gate = await approvalGate.AcquireAsync(
                "db_execute", "db.write", payload,
                BuildSummary(config, sql, analysis),
                requestedBy, taskId, scope.ProjectId, approvalId, waitSeconds, cancellationToken);

            if (!gate.Approved)
            {
                await AuditAsync("db_execute", requestedBy, scope.ProjectId, taskId, sql,
                    requiredApproval: true, approvalId: gate.ApprovalId,
                    outcome: gate.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                    summary: gate.Message, started, rowsAffected: null, cancellationToken);

                await RecordDbCallAsync(
                    "db_execute", scope, taskId, config, sql,
                    kind: kind,
                    targetTables: targetTables,
                    rowsAffected: null,
                    outcome: gate.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                    approvalId: gate.ApprovalId,
                    callerId: requestedBy,
                    started, cancellationToken);

                return new DbExecuteToolResult(false, gate.ApprovalId, gate.Status, null, gate.Message, gate.Message);
            }

            // 审批收尾时要写的结果摘要 —— 成功失败都写，
            // 免得审批历史上留一片空白（跟 git 那边的口径一致）。
            string? approvalSummary = null;

            try
            {
                // 审批通过后、真正执行前，再取一次默认连接：等待人工期间它可能被换掉了。
                // 换了就拒 —— 人批的是「在那个库上执行」，不能偷偷跑到另一个库。
                var execConfig = await connections.GetAsync(scope.ProjectId, null, cancellationToken);
                if (execConfig is null || execConfig.Id != requestedConnectionId)
                {
                    await AuditAsync("db_execute", requestedBy, scope.ProjectId, taskId, sql,
                        requiredApproval: true, approvalId: gate.ApprovalId, outcome: "denied",
                        summary: "默认连接已变化，请重新申请。", started, rowsAffected: null, cancellationToken);

                    await RecordDbCallAsync(
                        "db_execute", scope, taskId, config, sql,
                        kind: kind,
                        targetTables: targetTables,
                        rowsAffected: null,
                        outcome: "denied",
                        approvalId: gate.ApprovalId,
                        callerId: requestedBy,
                        started, cancellationToken);

                    return new DbExecuteToolResult(
                        false, gate.ApprovalId, nameof(ApprovalStatus.Executed), null,
                        "默认连接已变化，请重新申请。（审批已消费，请重新申请）",
                        "默认连接已变化，请重新申请。");
                }

                var result = await runner.ExecuteAsync(execConfig, sql, cancellationToken);

                approvalSummary = $"影响 {result.RowsAffected} 行";

                await AuditAsync("db_execute", requestedBy, scope.ProjectId, taskId, sql,
                    requiredApproval: true, approvalId: gate.ApprovalId, outcome: "success",
                    summary: $"影响 {result.RowsAffected} 行", started, rowsAffected: result.RowsAffected, cancellationToken);

                await RecordDbCallAsync(
                    "db_execute", scope, taskId, execConfig, sql,
                    kind: kind,
                    targetTables: targetTables,
                    rowsAffected: result.RowsAffected,
                    outcome: "success",
                    approvalId: gate.ApprovalId,
                    callerId: requestedBy,
                    started, cancellationToken);

                return new DbExecuteToolResult(
                    true, gate.ApprovalId, nameof(ApprovalStatus.Executed), result.RowsAffected, "已审批并执行。");
            }
            catch (Exception ex)
            {
                approvalSummary = $"执行失败: {ex.Message}";

                await AuditAsync("db_execute", callerId, projectId, taskId, sql ?? string.Empty,
                    requiredApproval: true, approvalId: gate.ApprovalId, outcome: "error",
                    summary: ex.Message, started, rowsAffected: null, cancellationToken);

                await RecordDbCallAsync(
                    "db_execute", scope, taskId, config, sql,
                    kind: kind,
                    targetTables: targetTables,
                    rowsAffected: null,
                    outcome: "error",
                    approvalId: gate.ApprovalId,
                    callerId: requestedBy,
                    started, cancellationToken);

                // 审批已经被占住（消费掉了），执行失败也不能让它回到可复用状态。
                return new DbExecuteToolResult(
                    false, gate.ApprovalId, nameof(ApprovalStatus.Executed), null,
                    $"执行失败: {ex.Message}（审批已消费，请重新申请）", ex.Message);
            }
            finally
            {
                // 审批已经占住了，收尾一下 —— 不收尾它就卡在 Executing。
                try
                {
                    // 用不可取消的 token 收尾：调用方断开时更需要把审批收干净。
                    // 原来用调用方的 token，一取消收尾就失败，审批卡死 30 分钟。
                    await approvalGate.CompleteAsync(gate.ApprovalId, approvalSummary, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 收尾失败不能盖掉真正的执行结果
                }
            }
        }
        catch (Exception ex)
        {
            // 作用域还没解析出来就用原始形参记审计 —— 记的是「谁尝试了什么」。
            await AuditAsync("db_execute", callerId, projectId, taskId, sql ?? string.Empty,
                requiredApproval: true, approvalId: approvalId, outcome: "error",
                summary: ex.Message, started, rowsAffected: null, cancellationToken);

            // 作用域都没解析出来就没什么可记的（记录要有项目归属）。
            if (scope is not null)
            {
                await RecordDbCallAsync(
                    "db_execute", scope, taskId, config, sql,
                    kind: "Unknown",
                    targetTables: null,
                    rowsAffected: null,
                    outcome: "error",
                    approvalId: approvalId,
                    callerId: callerId ?? scope.AgentId,
                    started, cancellationToken);
            }

            return new DbExecuteToolResult(
                false, approvalId ?? Guid.Empty, "Error", null, $"执行失败: {ex.Message}", ex.Message);
        }
    }

    [McpServerTool(Name = "db_list_connections")]
    [Description("列出项目配置的数据库连接（不返回密码）。已绑定会话时不用传 projectId。")]
    public async Task<IReadOnlyList<ConnectionInfoDto>> ListConnectionsAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);

        var configs = await connections.ListAsync(scope.ProjectId, cancellationToken);
        return configs
            .Select(c => new ConnectionInfoDto(c.Id, c.Name, c.Server, c.Database, c.UserId, c.IsDefault))
            .ToList();
    }

    [McpServerTool(Name = "db_list_rules")]
    [Description(
        "列出某个数据库连接上的表级规则（AllowRead / WriteRequiresApproval）。" +
        "不传 connectionId 时用项目的默认连接。规则决定了哪些表免审批写、哪些表不许读——" +
        "db_execute 报「需要审批」或 db_query 报「不许读」时，先用本工具看清规则再决定下一步。" +
        "已绑定会话时不用传 projectId。")]
    public async Task<DbListRulesToolResult> ListRulesAsync(
        Guid? projectId = null,
        Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await ResolveScopeAsync(projectId, cancellationToken);

            var config = await connections.GetAsync(scope.ProjectId, connectionId, cancellationToken);
            if (config is null)
            {
                return new DbListRulesToolResult(
                    false, Guid.Empty, string.Empty, string.Empty, [],
                    connectionId is null ? "该项目没有配置数据库连接。" : $"找不到连接 {connectionId}。");
            }

            var rules = await connections.ListRulesAsync(config.Id, cancellationToken);
            return new DbListRulesToolResult(true, config.Id, config.Server, config.Database, rules);
        }
        catch (Exception ex)
        {
            return new DbListRulesToolResult(false, Guid.Empty, string.Empty, string.Empty, [], ex.Message);
        }
    }

    [McpServerTool(Name = "db_test_connection")]
    [Description(
        "试连一个数据库连接（跑 SELECT 1），确认服务器名 / 库名 / 凭证真的连得上。" +
        "配置存进去了不等于连得上——db_execute / db_query 报连接错误时，先用本工具定位是配置问题还是网络问题。" +
        "连接必须属于当前项目（绑了会话就是会话那个项目），否则拒绝 —— 别拿别的项目的连接来探。" +
        "返回 ok=true 表示连通；ok=false 时 error 给出原因。")]
    public async Task<DbTestConnectionToolResult> TestConnectionAsync(
        Guid connectionId,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = await ResolveScopeAsync(projectId, cancellationToken);

            // 连接必须属于这个项目。以前只按 connectionId 查，
            // 绑在 A 项目的 agent 可以拿 B 项目的连接来探活 ——
            // 探活会暴露内网可达性，错误信息里还带得出服务器名。
            var owned = await connections.GetAsync(scope.ProjectId, connectionId, cancellationToken);
            if (owned is null)
            {
                return new DbTestConnectionToolResult(
                    false, $"找不到连接 {connectionId}，或它不属于项目「{scope.ProjectName}」。");
            }

            var (ok, error) = await connections.TestAsync(connectionId, cancellationToken);
            return new DbTestConnectionToolResult(ok, error);
        }
        catch (Exception ex)
        {
            return new DbTestConnectionToolResult(false, ex.Message);
        }
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
        ProjectScope scope,
        string kind,
        IReadOnlyList<string> targetTables,
        SqlWriteTargetAnalysis analysis,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var tables = analysis.AllTargetTables;
        var tableText = tables.Count > 0 ? string.Join(", ", tables) : "?";

        try
        {
            var result = await runner.ExecuteAsync(config, sql, cancellationToken);

            await AuditAsync("db_execute", callerId, scope.ProjectId, taskId, sql,
                requiredApproval: false, approvalId: null, outcome: "success",
                summary: $"影响 {result.RowsAffected} 行（表 {tableText} 免审批）",
                started, rowsAffected: result.RowsAffected, cancellationToken);

            await RecordDbCallAsync(
                "db_execute", scope, taskId, config, sql,
                kind: kind,
                targetTables: targetTables,
                rowsAffected: result.RowsAffected,
                outcome: "success",
                approvalId: null,
                callerId: callerId,
                started, cancellationToken);

            return new DbExecuteToolResult(
                true, Guid.Empty, nameof(ApprovalStatus.Executed), result.RowsAffected,
                $"已执行（表 {tableText} 配了免审批）。");
        }
        catch (Exception ex)
        {
            await AuditAsync("db_execute", callerId, scope.ProjectId, taskId, sql,
                requiredApproval: false, approvalId: null, outcome: "error",
                summary: ex.Message, started, rowsAffected: null, cancellationToken);

            await RecordDbCallAsync(
                "db_execute", scope, taskId, config, sql,
                kind: kind,
                targetTables: targetTables,
                rowsAffected: null,
                outcome: "error",
                approvalId: null,
                callerId: callerId,
                started, cancellationToken);

            return new DbExecuteToolResult(false, Guid.Empty, "Error", null, $"执行失败: {ex.Message}", ex.Message);
        }
    }

    [McpServerTool(Name = "db_schema")]
    [Description(
        "读取项目数据库的表结构：表名、列名、类型、是否可空、主键。默认放行，只记审计日志。" +
        "已绑定会话时不用传 projectId。")]
    public async Task<DbSchemaToolResult> SchemaAsync(
        Guid? projectId = null,
        string? schema = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        ProjectScope? scope = null;
        DbConnectionConfig? config = null;

        try
        {
            scope = await ResolveScopeAsync(projectId, cancellationToken);

            config = await connections.GetAsync(scope.ProjectId, null, cancellationToken)
                     ?? throw new GuanfuException("该项目没有配置数据库连接。");

            var result = await runner.GetSchemaAsync(config, schema, cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "db_schema", callerId ?? scope.AgentId, scope.ProjectId, scope.TaskId, schema ?? "(全部 schema)",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"{result.Tables.Count} 张表",
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            // 读表结构也是碰了这个库 —— 记一笔「谁在什么时候看了哪个库的哪些表」。
            await RecordDbCallAsync(
                "db_schema", scope, null, config, null,
                kind: "Read",
                targetTables: result.Tables.Select(t => $"{t.Schema}.{t.Table}").ToList(),
                rowsAffected: result.Tables.Count,
                outcome: "success",
                approvalId: null,
                callerId: callerId ?? scope.AgentId,
                started, cancellationToken);

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

            if (scope is not null)
            {
                await RecordDbCallAsync(
                    "db_schema", scope, null, config, null,
                    kind: "Read",
                    targetTables: null,
                    rowsAffected: null,
                    outcome: "error",
                    approvalId: null,
                    callerId: callerId ?? scope.AgentId,
                    started, cancellationToken);
            }

            return new DbSchemaToolResult(false, [], 0, ex.Message);
        }
    }

    /// <summary>审批摘要：写清目标库 + 逐语句的目标表，人批的时候才知道自己在批什么。</summary>
    private static string BuildSummary(DbConnectionConfig config, string sql, SqlWriteTargetAnalysis analysis)
    {
        var tables = analysis.AllTargetTables;
        var tableText = tables.Count > 0 ? string.Join(", ", tables) : "（无法识别目标表）";

        var perStatement = analysis.Statements.Count > 0
            ? string.Join("；", analysis.Statements.Select(s => $"{s.StatementType} → {string.Join(", ", s.TargetTables)}"))
            : analysis.Reason ?? "（无法逐语句识别）";

        // SQL 原文别整条塞进摘要：几百行的语句会把审批卡撑爆，
        // 人扫一眼反而看不出重点。截断 + 标出原长度。
        var snippet = Collapse(sql);
        if (snippet.Length > MaxSqlCharsInSummary)
        {
            snippet = string.Concat(snippet.AsSpan(0, MaxSqlCharsInSummary), $"…（全文 {sql.Length} 字）");
        }

        return $"对 {config.Server}/{config.Database} 执行写操作。目标表：{tableText}。逐语句：{perStatement}。SQL：{snippet}";
    }

    /// <summary>审批摘要里最多放多少字 SQL。</summary>
    private const int MaxSqlCharsInSummary = 300;

    /// <summary>把空白压成单个空格。</summary>
    private static string Collapse(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// 从审批的 payload 里取出当初绑定的 connectionId。
    /// 取不到（旧格式 / 解析失败 / 审批不存在）返回 null —— 前置校验跳过，
    /// 交给闸门的 payload 哈希比对兜底。
    /// </summary>
    private async Task<Guid?> GetBoundConnectionIdAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        try
        {
            var approval = await approvals.GetAsync(approvalId, cancellationToken);
            if (approval?.Payload is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(approval.Payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("connectionId", out var element) &&
                element.TryGetGuid(out var connectionId))
            {
                return connectionId;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DbExecuteToolResult Fail(string message, Guid approvalId = default, string status = "Error")
        => new(false, approvalId, status, null, message, message);

    /// <summary>
    /// 落一条数据库调用记录（专表：把 SQL 原文、目标库、目标表摊成列）。
    ///
    /// **附加记录失败绝不能把主流程带崩** —— 业务结果比附加记录重要，
    /// 记录写不进去就吞掉（通用工具审计还在，事件账本也还在）。
    /// 作用域没解析出来时不记：记录必须有项目归属，否则归属就是空的。
    /// </summary>
    private async Task RecordDbCallAsync(
        string toolName,
        ProjectScope? scope,
        Guid? taskId,
        DbConnectionConfig? config,
        string? sql,
        string? kind,
        IReadOnlyList<string>? targetTables,
        int? rowsAffected,
        string? outcome,
        Guid? approvalId,
        string? callerId,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        if (dbCalls is null || scope is null)
        {
            return;
        }

        try
        {
            await dbCalls.RecordAsync(new DbCallRecordEntry(
                AuditId: null,
                ProjectId: scope.ProjectId,
                TaskId: taskId ?? scope.TaskId,
                SessionId: scope.SessionId,
                ConnectionId: config?.Id,
                ToolName: toolName,
                Server: config?.Server,
                Database: config?.Database,
                Sql: sql,
                Kind: kind,
                TargetTables: targetTables,
                RowsAffected: rowsAffected,
                Outcome: outcome,
                ApprovalId: approvalId,
                CallerId: callerId,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);
        }
        catch (Exception)
        {
            // 记不上就算了 —— 不能让主流程因为附加记录失败而失败。
        }
    }

    private Task AuditAsync(
        string toolName,
        string? callerId,
        Guid? projectId,
        Guid? taskId,
        string arguments,
        bool requiredApproval,
        Guid? approvalId,
        string outcome,
        string? summary,
        DateTimeOffset started,
        int? rowsAffected,
        CancellationToken cancellationToken)
        => audit.RecordAsync(new ToolAuditEntry(
            toolName, callerId, projectId, taskId, arguments,
            RequiredApproval: requiredApproval, ApprovalId: approvalId,
            Outcome: outcome,
            ResultSummary: summary,
            RowsAffected: rowsAffected,
            DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);
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
