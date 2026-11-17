using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Contracts;
using Workbench.Domain;
using Workbench.Application.Services;

namespace Workbench.Mcp;

/// <summary>
/// 工作台自身的 MCP 工具。给外部 agent（Claude Code / Codex 等）调用。
/// </summary>
[McpServerToolType]
public sealed class WorkbenchMcpTools(
    IProjectService projects,
    ITaskService tasks,
    IApprovalService approvals,
    IToolAuditService audit,
    ITaskDependencyService dependencies,
    IExportService exports,
    IAppDbContext? db = null,
    IProjectScopeResolver? scopeResolver = null,
    IAgentSessionService? sessions = null,
    IAuditChainArchiveService? archive = null,
    IAgentSessionAccessor? sessionAccessor = null,
    ITaskUsageService? usage = null)
{
    /// <summary>
    /// 算出这次调用算在哪个项目上。
    ///
    /// 优先用会话绑定的项目（agent 在哪个仓库干活就落到哪个项目），
    /// 没有会话时才看调用方显式传的 projectId。两边都对不上就拒 ——
    /// 不能让 agent 拿着 A 项目的会话去改 B 项目。
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

        throw new WorkbenchException(
            "当前调用没有绑定项目，也没有传 projectId。请先调用 workbench_bind_session。");
    }

    /// <summary>
    /// 「项目本来就可以不指定」的工具用这个：解析不出就返回 null，调用方保持不过滤。
    /// 显式传了 projectId 但跟会话冲突时仍然抛异常 —— 对账的规矩不能松。
    /// </summary>
    private async Task<Guid?> ResolveOptionalProjectIdAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (scopeResolver is null)
        {
            return projectId;
        }

        var scope = await scopeResolver.ResolveOrNullAsync(projectId, cancellationToken);
        return scope?.ProjectId;
    }

    /// <summary>
    /// 任务 id 二选一：直接给 id，或者给编号（如 REQ-001）在会话绑定的项目里找。
    ///
    /// agent 只知道人嘴里的编号（「把 REQ-001 做了」），不知道数据库里的 GUID。
    /// 逼它先查一次再传 GUID 是白折腾 —— 编号能直接落到任务上才对。
    /// </summary>
    private async Task<Guid> ResolveTaskIdAsync(
        Guid? taskId,
        string? taskKey,
        CancellationToken cancellationToken)
    {
        if (taskId is Guid id)
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(taskKey))
        {
            throw new WorkbenchException("taskId 和 taskKey 至少要给一个。");
        }

        var scope = scopeResolver is null
            ? null
            : await scopeResolver.ResolveOrNullAsync(null, cancellationToken);

        if (scope is null)
        {
            throw new WorkbenchException(
                "只给了 taskKey，但当前调用没有绑定项目，无法确定在哪个项目里找这个编号。" +
                "先调 workbench_bind_session 绑定你所在的仓库，或者直接给 taskId。");
        }

        var found = await tasks.ListAsync(
            scope.ProjectId, null, null, null, taskKey, null, cancellationToken);

        var task = found.FirstOrDefault();
        if (task is null)
        {
            throw new WorkbenchException($"项目「{scope.ProjectName}」里没有编号为 {taskKey} 的任务。");
        }

        return task.Id;
    }

    [McpServerTool(Name = "workbench_list_projects")]
    [Description("列出工作台里所有项目，含进度、任务统计、停滞标记与 git 分支")]
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken)
        => await projects.ListAsync(cancellationToken);

    [McpServerTool(Name = "workbench_get_project")]
    [Description("获取单个项目的详情与配置")]
    public async Task<ProjectSummaryDto?> GetProjectAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);
        return await projects.GetAsync(scope.ProjectId, cancellationToken);
    }

    [McpServerTool(Name = "workbench_create_task")]
    [Description(
        "在项目下创建一个任务项，初始状态为 Draft。type 是类型：" +
        "requirement(需求，默认) / bug(缺陷) / optimization(优化)，" +
        "编号按类型分开分配（REQ-001 / BUG-001 / OPT-001，各类型独立计数）。" +
        "key 不给就自动分配下一个。milestoneId 把任务挂到某个迭代版本（如「12月版本」）下。" +
        "branch 是绑定的开发分支 —— 绑了分支的任务，只有仓库当前就在那个分支上才会运行")]
    public async Task<TaskSummaryDto> CreateTaskAsync(
        Guid? projectId = null,
        string? title = null,
        string? description = null,
        Guid? parentTaskId = null,
        Guid? milestoneId = null,
        string? type = null,
        string? branch = null,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new WorkbenchException("任务标题不能为空。");
        }

        // 不给类型就是需求；给了但认不出来要报错，不能默默当需求处理
        var parsedType = TaskType.Requirement;
        if (!string.IsNullOrWhiteSpace(type) && !TaskTypes.TryParse(type, out parsedType))
        {
            throw new WorkbenchException(
                $"未知任务类型: {type}。合法值: {string.Join("、", TaskTypes.Names)}");
        }

        var scope = await ResolveScopeAsync(projectId, cancellationToken);

        return await tasks.CreateAsync(
            scope.ProjectId, title!, description, parentTaskId, milestoneId, parsedType, branch, key, cancellationToken);
    }

    [McpServerTool(Name = "workbench_list_tasks")]
    [Description(
        "按项目 / 状态 / 关键字 / 迭代版本 / 编号 / 类型 / 分支列出任务。" +
        "key 传了就是精确匹配编号，type 传了只出该类型（requirement / bug / optimization），" +
        "branch 传了只出绑定该分支的任务。limit/offset 用来分页 —— 任务多的时候一定要给 limit，" +
        "否则返回体会把 agent 的上下文撑爆。")]
    public async Task<IReadOnlyList<TaskSummaryDto>> ListTasksAsync(
        Guid? projectId = null,
        string? status = null,
        string? keyword = null,
        Guid? milestoneId = null,
        string? key = null,
        string? type = null,
        CancellationToken cancellationToken = default,
        string? branch = null,
        int? limit = null,
        int? offset = null,
        bool includeArchived = false)
    {
        TaskStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!UiText.TryParseStatus(status, out var value))
            {
                throw new WorkbenchException($"未知状态「{status}」。合法值: {string.Join(", ", Enum.GetNames<TaskStatus>())}");
            }

            parsed = value;
        }

        TaskType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!TaskTypes.TryParse(type, out var value))
            {
                throw new WorkbenchException(
                    $"未知任务类型: {type}。合法值: {string.Join("、", TaskTypes.Names)}");
            }

            parsedType = value;
        }

        // 有会话就按会话绑定的项目过滤（这正是绑定的价值）；没会话时保持「列全部」。
        var effectiveProjectId = await ResolveOptionalProjectIdAsync(projectId, cancellationToken);

        return await tasks.ListAsync(
            effectiveProjectId, parsed, keyword, milestoneId, key, parsedType, cancellationToken,
            branch, limit, offset, includeArchived);
    }

    [McpServerTool(Name = "workbench_list_events")]
    [Description(
        "列任务的事件流水（新到旧）。账本是工作台的三大能力之一 —— " +
        "想看一个任务完整的时间线、或者回溯「当时用的是哪版计划」，用这个。")]
    public async Task<IReadOnlyList<StatusEventDto>> ListEventsAsync(
        Guid taskId,
        int limit = 50,
        CancellationToken cancellationToken = default)
        => await tasks.ListEventsAsync(taskId, limit, cancellationToken);

    [McpServerTool(Name = "workbench_archive_task")]
    [Description(
        "归档 / 取消归档一个任务。归档不是删除 —— 流水、产物、评审全都留着，" +
        "只是默认不再出现在列表里。误建的任务需要个清理路径。")]
    public async Task<TaskSummaryDto> ArchiveTaskAsync(
        Guid taskId,
        bool archived = true,
        CancellationToken cancellationToken = default)
        => await tasks.SetArchivedAsync(taskId, archived, cancellationToken);

    [McpServerTool(Name = "workbench_get_task")]
    [Description("获取单个任务的详情")]
    public async Task<TaskSummaryDto?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
        => await tasks.GetAsync(taskId, cancellationToken);

    [McpServerTool(Name = "workbench_get_task_by_key")]
    [Description(
        "按编号找任务（如 REQ-001，或人嘴里说的 req001 —— 连字符可有可无）。" +
        "人说话是「看下 req001」，agent 得能落到具体任务上。找不到时 ok=false 且 error 说明。")]
    public async Task<TaskLookupResult> GetTaskByKeyAsync(
        Guid? projectId = null,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new WorkbenchException("任务编号不能为空。");
        }

        var scope = await ResolveScopeAsync(projectId, cancellationToken);

        var found = await tasks.ListAsync(scope.ProjectId, null, null, null, key, null, cancellationToken);

        if (found.Count == 0)
        {
            // 把项目里现有的编号列出来，agent 能自己改对，而不是卡在这。
            // 用 SQL 粗筛（只取 Key 列 + 字母前缀 Like），不再把整个项目的任务全量加载进内存。
            var keys = await ListAvailableKeysAsync(scope.ProjectId, key!, cancellationToken);

            return TaskLookupResult.Fail(
                $"项目里没有编号为 {key} 的任务。", keys);
        }

        return new TaskLookupResult(true, null, found);
    }

    /// <summary>
    /// 找不到编号时，列出可供纠正的现有编号。
    /// 有 db 就走 SQL 粗筛：按编号的字母前缀（REQ / BUG / OPT）过滤，只 Select Key 列，
    /// 避免「为了列几个编号把全项目任务连同 DTO 全量 ToList」的开销。
    /// 没有 db（旧宿主）退回原来的全量列举。
    /// </summary>
    private async Task<IReadOnlyList<string>> ListAvailableKeysAsync(
        Guid projectId, string key, CancellationToken cancellationToken)
    {
        if (db is not null)
        {
            var prefix = new string(key.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();

            var query = db.Tasks.AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.Key != null);

            if (prefix.Length > 0)
            {
                query = query.Where(t => t.Key!.StartsWith(prefix));
            }

            return await query
                .Select(t => t.Key!)
                .OrderBy(k => k)
                .ToListAsync(cancellationToken);
        }

        var available = await tasks.ListAsync(projectId, null, null, null, null, null, cancellationToken);
        return available
            .Select(t => t.Key)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!)
            .ToList();
    }

    [McpServerTool(Name = "workbench_claim_task")]
    [Description(
        "领取一个待认领任务，带租约。taskId 不给就用 taskKey（如 REQ-001）——在会话绑定的项目里找；" +
        "agentId 不给就用当前会话的 agent。已被他人持有且租约未过期时返回 ok=false 而不是报错。" +
        "认领成功后任务自动挂到本次会话上，之后的调用都归属到这个任务。" +
        "worktree：你在哪个工作目录干活。**不传就是用项目的主工作区（仓库根）——" +
        "主工作区同一时刻只允许一个 agent 占着**（两个人共用一个目录会互相踩文件）。" +
        "要跟别人并行，就先 `git worktree add <目录>`，再把那个目录填进来。")]
    public async Task<ClaimResult> ClaimTaskAsync(
        Guid? taskId = null,
        string? agentId = null,
        int leaseMinutes = 30,
        string? taskKey = null,
        string? worktree = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = scopeResolver is null
                ? null
                : await scopeResolver.ResolveOrNullAsync(null, cancellationToken);

            var resolvedId = await ResolveTaskIdAsync(taskId, taskKey, cancellationToken);

            var resolvedAgentId = agentId ?? scope?.AgentId;
            if (string.IsNullOrWhiteSpace(resolvedAgentId))
            {
                throw new WorkbenchException(
                    "agentId 不能为空：当前没有会话可以继承 agent 身份，请显式传 agentId。");
            }

            var task = await tasks.ClaimAsync(resolvedId, resolvedAgentId, leaseMinutes, cancellationToken, worktree);

            // 认领成功 → 任务挂到本次会话上。之后这个会话的所有调用都自动归属这个任务，
            // 审计与事件账本不用 agent 自己填 taskId。
            if (scope?.SessionId is Guid sessionId && sessions is not null)
            {
                await sessions.AttachTaskAsync(sessionId, task.Id, cancellationToken);
            }

            return new ClaimResult(true, task, null, null);
        }
        catch (WorkbenchException ex)
        {
            var current = taskId is Guid id ? await tasks.GetAsync(id, cancellationToken) : null;
            return new ClaimResult(false, current, ex.Message, current?.ClaimedBy);
        }
    }

    [McpServerTool(Name = "workbench_get_context")]
    [Description(
        "一次性取回干活所需的全部上下文：任务信息、关联文档、已有计划、产物、最近事件、当前允许的状态迁移、git 分支与 HEAD。" +
        "先调这个，再决定做什么，避免反复零散调用。" +
        "taskId 不给就用 taskKey（如 REQ-001）——在会话绑定的项目里找。")]
    public async Task<TaskContextDto> GetContextAsync(
        Guid? taskId = null,
        int eventLimit = 20,
        string? taskKey = null,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveTaskIdAsync(taskId, taskKey, cancellationToken);
        return await tasks.GetContextAsync(id, eventLimit, cancellationToken);
    }

    [McpServerTool(Name = "workbench_set_plan")]
    [Description("回写需求理解与开发计划（由 agent 生成，工作台只存不生成）")]
    public async Task<PlanDto> SetPlanAsync(
        Guid taskId,
        string? background = null,
        string? understanding = null,
        string? breakdown = null,
        string? acceptanceCriteria = null,
        string? risks = null,
        string? author = null,
        CancellationToken cancellationToken = default)
        => await tasks.SetPlanAsync(
            taskId, background, understanding, breakdown, acceptanceCriteria, risks, author, cancellationToken);

    [McpServerTool(Name = "workbench_update_status")]
    [Description(
        "推进任务状态。非法迁移不会被执行，会返回 ok=false 与当前允许的迁移列表，" +
        "请据此重试。合法流转: Draft→DocsReady→Planned→InDev→Testing→Review→Done，" +
        "任意态可进 Blocked，Review 可打回 InDev。" +
        "注意: Review→Done 还有一道评审门禁 —— 开过评审的任务必须当前轮七个维度全部通过才能结项。")]
    public async Task<StatusChangeResult> UpdateStatusAsync(
        Guid taskId,
        string status,
        string? note = null,
        string? actor = null,
        string source = "Agent",
        CancellationToken cancellationToken = default)
    {
        if (!UiText.TryParseStatus(status, out var target))
        {
            return StatusChangeResult.Failed(
                $"未知状态「{status}」。合法值: {string.Join(", ", Enum.GetNames<TaskStatus>())}",
                Enum.GetNames<TaskStatus>().ToList());
        }

        var eventSource = Enum.TryParse<TaskEventSource>(source, ignoreCase: true, out var parsedSource)
            ? parsedSource
            : TaskEventSource.Agent;

        try
        {
            // 会话 id 用来给产出标来源（进 Review 时采工作区要用）。
            // 采工作区本身已经下沉到 TaskService —— 所有入口（这里 / 界面 / REST）
            // 都得走到同一处，不然只有 MCP 那条路会采。
            var sessionId = await ResolveSessionIdAsync(cancellationToken);

            var task = await tasks.ChangeStatusAsync(taskId, target, note, eventSource, actor, cancellationToken, sessionId);

            return StatusChangeResult.Succeeded(task);
        }
        catch (InvalidStatusTransitionException ex)
        {
            // 不抛异常：MCP 会把异常吞成一句泛化信息，agent 就看不到「当前允许什么」了
            return StatusChangeResult.Failed(ex.Message, ex.Allowed.Select(s => s.ToString()).ToList());
        }
        catch (WorkbenchException ex)
        {
            return StatusChangeResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 当前调用属于哪个会话。查不到就返回 null。
    ///
    /// 会话只是「附加信息」（顺着它能查到是谁干的），不是采集的前提 ——
    /// 所以这里自己吞异常，不让它影响采集。
    /// </summary>
    private async Task<Guid?> ResolveSessionIdAsync(CancellationToken cancellationToken)
    {
        if (scopeResolver is null)
        {
            return null;
        }

        try
        {
            var scope = await scopeResolver.ResolveOrNullAsync(null, cancellationToken);
            return scope?.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [McpServerTool(Name = "workbench_add_artifact")]
    [Description("上传开发过程产物（代码变更摘要、测试报告、部署单、commit 等）")]
    public async Task<ArtifactDto> AddArtifactAsync(
        Guid taskId,
        string title,
        string type = "Other",
        string? content = null,
        string? externalPath = null,
        string? commitSha = null,
        string? branch = null,
        string? author = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var artifactType))
        {
            throw new WorkbenchException($"未知产物类型: {type}。合法值: {string.Join(", ", Enum.GetNames<ArtifactType>())}");
        }

        return await tasks.AddArtifactAsync(
            taskId, artifactType, title, content, externalPath, commitSha, branch, author, cancellationToken);
    }

    [McpServerTool(Name = "workbench_report_test_result")]
    [Description("回写单元测试结果（总数 / 通过数 / 失败明细 / 覆盖率）")]
    public async Task<TestResultDto> ReportTestResultAsync(
        Guid taskId,
        int total,
        int passed,
        string? failures = null,
        double? coverage = null,
        string? author = null,
        CancellationToken cancellationToken = default)
        => await tasks.ReportTestResultAsync(taskId, total, passed, failures, coverage, author, cancellationToken);

    [McpServerTool(Name = "workbench_post_event")]
    [Description("往任务流水里写一条事件（不改变状态）")]
    public async Task<StatusEventDto> PostEventAsync(
        Guid taskId,
        string message,
        string source = "Agent",
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var eventSource = Enum.TryParse<TaskEventSource>(source, ignoreCase: true, out var parsedSource)
            ? parsedSource
            : TaskEventSource.Agent;

        return await tasks.PostEventAsync(taskId, message, eventSource, actor, cancellationToken);
    }

    [McpServerTool(Name = "workbench_list_approvals")]
    [Description("查询审批请求（默认列出待审）")]
    public async Task<IReadOnlyList<ApprovalDto>> ListApprovalsAsync(
        string? status = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        ApprovalStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var value))
            {
                throw new WorkbenchException($"未知审批状态: {status}。合法值: {string.Join(", ", Enum.GetNames<ApprovalStatus>())}");
            }

            parsed = value;
        }

        // projectId 本来就是可选筛选项：绑了会话就按会话的项目过滤，没会话就不过滤（全列）。
        var effectiveProjectId = await ResolveOptionalProjectIdAsync(projectId, cancellationToken);

        return await approvals.ListAsync(parsed, effectiveProjectId, cancellationToken);
    }

    [McpServerTool(Name = "workbench_get_usage")]
    [Description(
        "查用量：工具调用次数、用到的工具种类、事件条数、租约时长（这些是工作台自己数的），" +
        "以及 token / 费用（**agent 自报**的值，工作台不调模型、数不出 token）。" +
        "不给 taskId 就是整个项目的汇总（按任务、按 agent）。")]
    public async Task<UsageToolResult> GetUsageAsync(
        Guid? taskId = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (usage is null)
        {
            return UsageToolResult.Fail("当前宿主没有启用用量统计。");
        }

        try
        {
            if (taskId is Guid id)
            {
                return new UsageToolResult { Ok = true, Task = await usage.GetTaskUsageAsync(id, cancellationToken) };
            }

            var scope = await ResolveScopeAsync(projectId, cancellationToken);
            return new UsageToolResult
            {
                Ok = true,
                Project = await usage.GetProjectUsageAsync(scope.ProjectId, 20, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            return UsageToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_report_cost")]
    [Description(
        "报告这个任务**这次又用了**多少 token / 费用（增量，不是累计总额 —— 工作台会累加成总额）。" +
        "工作台不调模型，token 与费用只能由你报 —— 它会标成「自报」，跟工作台自己数的" +
        "工具调用次数不是一回事。")]
    public async Task<UsageToolResult> ReportCostAsync(
        Guid taskId,
        long? tokens = null,
        decimal? cost = null,
        string? currency = null,
        CancellationToken cancellationToken = default)
    {
        if (usage is null)
        {
            return UsageToolResult.Fail("当前宿主没有启用用量统计。");
        }

        try
        {
            var result = await usage.ReportCostAsync(
                taskId, tokens, cost, currency, "agent", cancellationToken);

            return new UsageToolResult { Ok = true, Task = result };
        }
        catch (Exception ex)
        {
            return UsageToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_verify_audit_chain")]
    [Description(
        "验审计哈希链：检查工具调用审计有没有被人在数据库文件层面改过。" +
        "链是每条审计串起来的哈希 —— 改一条内容、删一条记录，链都会断，" +
        "这个工具能把断在哪一条指出来。")]
    public async Task<AuditChainResult> VerifyAuditChainAsync(
        CancellationToken cancellationToken = default)
    {
        var report = await audit.VerifyChainAsync(cancellationToken);

        return new AuditChainResult(
            true,
            report.Intact,
            report.Total,
            report.Chained,
            report.FirstBrokenId,
            report.Intact ? null : report.Reason);
    }

    [McpServerTool(Name = "workbench_verify_audit_archive")]
    [Description(
        "把审计库跟**外部归档**对账：归档文件在工作台数据目录之外，" +
        "里面记着当初的链头。库里的记录要是被删过或被回滚过，这个工具能看出来。" +
        "没配置归档路径时返回 configured=false —— 那说明只验得了库内的链。")]
    public async Task<AuditArchiveCheckResult> VerifyAuditArchiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (archive is null)
        {
            return new AuditArchiveCheckResult(false, false, false, 0, 0, 0, "当前部署没有启用审计归档服务。");
        }

        var report = await archive.VerifyAsync(cancellationToken);

        return new AuditArchiveCheckResult(
            true,
            report.Configured,
            report.Intact,
            report.ArchivedSequence,
            report.CurrentSequence,
            report.ArchivedCount,
            report.Reason);
    }

    [McpServerTool(Name = "workbench_list_audit")]
    [Description(
        "查工具调用审计（谁在什么时候调了哪个工具、结果如何）。" +
        "审计表一直在写，但以前没有入口能查 —— 补上了。")]
    public async Task<IReadOnlyList<ToolAuditDto>> ListAuditAsync(
        string? toolName = null,
        Guid? taskId = null,
        Guid? projectId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        // projectId 本来就是可选筛选项：绑了会话就按会话的项目过滤，没会话就不过滤（全列）。
        var effectiveProjectId = await ResolveOptionalProjectIdAsync(projectId, cancellationToken);

        return await audit.ListAsync(toolName, taskId, effectiveProjectId, limit, cancellationToken);
    }

    [McpServerTool(Name = "workbench_get_approval")]
    [Description("获取单个审批请求的详情（含 SQL 原文）")]
    public async Task<ApprovalDto?> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken)
        => await approvals.GetAsync(approvalId, cancellationToken);

    [McpServerTool(Name = "workbench_await_approval")]
    [Description("阻塞等待人工审批结果。超时返回当前状态（仍为 Pending），可再次调用继续等待。")]
    public async Task<ApprovalDto> AwaitApprovalAsync(
        Guid approvalId,
        int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
        => await approvals.AwaitAsync(approvalId, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "workbench_revoke_approval")]
    [Description(
        "撤销一条还没执行的审批（Pending / Approved 可撤；已 Executing / Executed 的撤不回——操作已落地）。" +
        "误批了、或情况变了不想让 agent 拿这条审批续跑时用它。reason 会记进审批的撤销原因。" +
        "返回 ok=true 与撤销后的审批；失败时 error 说明原因（审批不存在 / 已执行 / 刚被占用），据此判断下一步。" +
        "撤销人记在审计里：传了 callerId 就记它，没传就记当前会话的 agent —— 不会记成「human」。")]
    public async Task<ApprovalRevokeResult> RevokeApprovalAsync(
        Guid approvalId,
        string? reason = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var actor = await ResolveActorAsync(callerId, cancellationToken);
            var dto = await approvals.RevokeAsync(approvalId, reason, cancellationToken, actor);
            return new ApprovalRevokeResult(true, dto);
        }
        catch (Exception ex)
        {
            // 不抛异常：MCP 会把异常吞成一句泛化信息，agent 就看不到「为什么撤不掉」了。
            return new ApprovalRevokeResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// 这次调用算在谁头上：工具显式传的 callerId → 会话里的 agent → 请求头里的 agent。
    ///
    /// 撤销是「谁改的门禁」，审计里必须是真的那个人 —— 记成「human」就是假账。
    /// </summary>
    private async Task<string?> ResolveActorAsync(string? callerId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(callerId))
        {
            return callerId.Trim();
        }

        if (sessionAccessor is null)
        {
            return null;
        }

        try
        {
            var session = await sessionAccessor.CurrentAsync(cancellationToken);
            return session?.AgentId ?? sessionAccessor.CurrentAgentId;
        }
        catch (Exception)
        {
            // 查不到会话就退回请求头里的 agent 标识；都没有就留 null（按「human」记）。
            return sessionAccessor.CurrentAgentId;
        }
    }

    [McpServerTool(Name = "workbench_assign_task")]
    [Description(
        "给任务分派负责人 / 优先级 / 截止日 / 权重。" +
        "priority 取 low / normal / high / critical；weight 是进度权重（默认 1）。" +
        "不传的字段保持原值。")]
    public async Task<TaskSummaryDto> AssignTaskAsync(
        Guid taskId,
        string? assignee = null,
        string? priority = null,
        DateTimeOffset? dueAt = null,
        double? weight = null,
        CancellationToken cancellationToken = default)
    {
        TaskPriority? parsed = null;
        if (!string.IsNullOrWhiteSpace(priority))
        {
            if (!Enum.TryParse<TaskPriority>(priority, ignoreCase: true, out var value))
            {
                throw new WorkbenchException(
                    $"未知优先级: {priority}。合法值: {string.Join("、", Enum.GetNames<TaskPriority>())}");
            }

            parsed = value;
        }

        return await tasks.SetAssignmentAsync(taskId, assignee, parsed, dueAt, weight, cancellationToken);
    }

    [McpServerTool(Name = "workbench_list_dependencies")]
    [Description(
        "列出任务的前置依赖与它阻塞了谁。" +
        "DependsOnDone 为 false 的前置任务没做完 —— 这时候不许领取任务、也不许推进到 InDev。")]
    public async Task<IReadOnlyList<TaskDependencyDto>> ListDependenciesAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
        => await dependencies.ListAsync(taskId, cancellationToken);

    [McpServerTool(Name = "workbench_add_task_dependency")]
    [Description(
        "加一条任务依赖：dependsOnTaskId 必须先完成，本任务才能开工。" +
        "type 传 blockedby（本任务依赖它，默认）或 blocks（本任务阻塞它）。" +
        "会检查环，A 依赖 B、B 依赖 A 这类会被拒。")]
    public async Task<TaskDependencyDto> AddDependencyAsync(
        Guid taskId,
        Guid dependsOnTaskId,
        string? type = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = DependencyType.BlockedBy;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<DependencyType>(type, ignoreCase: true, out var value))
            {
                throw new WorkbenchException(
                    $"未知依赖方向: {type}。合法值: {string.Join("、", Enum.GetNames<DependencyType>())}");
            }

            parsed = value;
        }

        return await dependencies.AddAsync(taskId, dependsOnTaskId, parsed, note, cancellationToken);
    }

    [McpServerTool(Name = "workbench_remove_task_dependency")]
    [Description("移除一条任务依赖（两个方向都试）。返回 ok 与 removed；依赖不存在时 ok=false 且 error 给出原因。")]
    public async Task<DependencyRemoveResult> RemoveDependencyAsync(
        Guid taskId,
        Guid dependsOnTaskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await dependencies.RemoveAsync(taskId, dependsOnTaskId, cancellationToken);
            return new DependencyRemoveResult(true, true);
        }
        catch (Exception ex)
        {
            return new DependencyRemoveResult(false, false, ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_export_events")]
    [Description(
        "导出任务的事件流水（账本）。format 传 markdown（默认）/ csv / json。" +
        "format=json 时返回结构化对象，其余返回文本。")]
    public async Task<ExportToolResult> ExportEventsAsync(
        Guid taskId,
        string format = "markdown",
        CancellationToken cancellationToken = default)
    {
        var text = await exports.ExportTaskEventsAsync(taskId, format, cancellationToken);
        return new ExportToolResult(true, format, text);
    }

    [McpServerTool(Name = "workbench_export_review")]
    [Description(
        "导出评审报告。round 不给就是最新一轮。format 传 markdown（默认）/ json。")]
    public async Task<ExportToolResult> ExportReviewAsync(
        Guid taskId,
        int? round = null,
        string format = "markdown",
        CancellationToken cancellationToken = default)
    {
        var text = await exports.ExportReviewReportAsync(taskId, round, format, cancellationToken);
        return new ExportToolResult(true, format, text, round);
    }
}

/// <summary>
/// 状态迁移结果。非法迁移不抛异常，而是带上「当前允许的迁移列表」返回，
/// 这样 agent 读得到、能自我纠正（MCP 会把异常吞成一句泛化信息）。
/// </summary>
public sealed record StatusChangeResult(
    bool Ok,
    TaskSummaryDto? Task,
    string? Error = null,
    IReadOnlyList<string>? AllowedTransitions = null)
{
    public static StatusChangeResult Succeeded(TaskSummaryDto task) => new(true, task);

    public static StatusChangeResult Failed(string error, IReadOnlyList<string>? allowed = null) =>
        new(false, null, error, allowed);
}

/// <summary>任务认领结果。</summary>
public sealed record ClaimResult(
    bool Ok,
    TaskSummaryDto? Task,
    string? Error = null,
    string? HeldBy = null);

/// <summary>
/// 按编号查任务的结果。找不到时不抛异常 —— MCP 会把异常吞成一句泛化信息，
/// agent 看不到「项目里到底有哪些编号」。所以把现有编号一并带回去，让它能自己纠正。
/// </summary>
public sealed record TaskLookupResult(
    bool Ok,
    string? Error = null,
    IReadOnlyList<TaskSummaryDto>? Tasks = null,
    IReadOnlyList<string>? AvailableKeys = null)
{
    public static TaskLookupResult Fail(string error, IReadOnlyList<string>? available = null) =>
        new(false, error, null, available);
}

/// <summary>撤销审批的结果。撤不掉时不抛异常，带上原因让 agent 能判断下一步。</summary>
public sealed record ApprovalRevokeResult(
    bool Ok,
    ApprovalDto? Approval,
    string? Error = null);

/// <summary>
/// 移除依赖的结果。
///
/// 原来这里返回裸 bool —— agent 拿到 false 分不清是「没删掉」还是「压根没有这条依赖」。
/// </summary>
public sealed record DependencyRemoveResult(
    bool Ok,
    bool Removed,
    string? Error = null);

/// <summary>导出结果。格式与内容都带回来，agent 不用再猜拿到的是哪种形态。</summary>
public sealed record ExportToolResult(
    bool Ok,
    string Format,
    string Content,
    int? Round = null,
    string? Error = null);

/// <summary>审计哈希链的校验结果。</summary>
/// <summary>审计归档对账的结果。</summary>
public sealed record AuditArchiveCheckResult(
    bool Ok,
    bool Configured,
    bool Intact,
    long ArchivedSequence,
    long CurrentSequence,
    int ArchivedCount,
    string? Error = null);

public sealed record AuditChainResult(
    bool Ok,
    bool Intact,
    int Total,
    int Chained,
    Guid? FirstBrokenId,
    string? Error = null);

/// <summary>用量查询/上报的返回形状。</summary>
public sealed record UsageToolResult
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public TaskUsageDto? Task { get; init; }

    public ProjectUsageDto? Project { get; init; }

    public static UsageToolResult Fail(string error) => new() { Ok = false, Error = error };
}
