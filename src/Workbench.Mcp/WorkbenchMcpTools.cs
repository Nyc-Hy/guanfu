using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
    IExportService exports)
{
    [McpServerTool(Name = "workbench_list_projects")]
    [Description("列出工作台里所有项目，含进度、任务统计、停滞标记与 git 分支")]
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken)
        => await projects.ListAsync(cancellationToken);

    [McpServerTool(Name = "workbench_get_project")]
    [Description("获取单个项目的详情与配置")]
    public async Task<ProjectSummaryDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
        => await projects.GetAsync(projectId, cancellationToken);

    [McpServerTool(Name = "workbench_create_task")]
    [Description(
        "在项目下创建一个任务项，初始状态为 Draft。type 是类型：" +
        "requirement(需求，默认) / bug(缺陷) / optimization(优化)，" +
        "编号按类型分开分配（REQ-001 / BUG-001 / OPT-001，各类型独立计数）。" +
        "key 不给就自动分配下一个。milestoneId 把任务挂到某个迭代版本（如「12月版本」）下。" +
        "branch 是绑定的开发分支 —— 绑了分支的任务，只有仓库当前就在那个分支上才会运行")]
    public async Task<TaskSummaryDto> CreateTaskAsync(
        Guid projectId,
        string title,
        string? description = null,
        Guid? parentTaskId = null,
        Guid? milestoneId = null,
        string? type = null,
        string? branch = null,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        // 不给类型就是需求；给了但认不出来要报错，不能默默当需求处理
        var parsedType = TaskType.Requirement;
        if (!string.IsNullOrWhiteSpace(type) && !TaskTypes.TryParse(type, out parsedType))
        {
            throw new WorkbenchException(
                $"未知任务类型: {type}。合法值: {string.Join("、", TaskTypes.Names)}");
        }

        return await tasks.CreateAsync(
            projectId, title, description, parentTaskId, milestoneId, parsedType, branch, key, cancellationToken);
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
            if (!Enum.TryParse<TaskStatus>(status, ignoreCase: true, out var value))
            {
                throw new WorkbenchException($"未知状态: {status}。合法值: {string.Join(", ", Enum.GetNames<TaskStatus>())}");
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

        return await tasks.ListAsync(
            projectId, parsed, keyword, milestoneId, key, parsedType, cancellationToken,
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
        Guid projectId,
        string key,
        CancellationToken cancellationToken = default)
    {
        var found = await tasks.ListAsync(projectId, null, null, null, key, null, cancellationToken);

        if (found.Count == 0)
        {
            // 把项目里现有的编号列出来，agent 能自己改对，而不是卡在这
            var available = await tasks.ListAsync(projectId, null, null, null, null, null, cancellationToken);
            var keys = available
                .Select(t => t.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .ToList();

            return TaskLookupResult.Fail(
                $"项目里没有编号为 {key} 的任务。", keys);
        }

        return new TaskLookupResult(true, null, found);
    }

    [McpServerTool(Name = "workbench_claim_task")]
    [Description("领取一个待认领任务，带租约；已被他人持有且租约未过期时返回 ok=false 而不是报错")]
    public async Task<ClaimResult> ClaimTaskAsync(
        Guid taskId,
        string agentId,
        int leaseMinutes = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await tasks.ClaimAsync(taskId, agentId, leaseMinutes, cancellationToken);
            return new ClaimResult(true, task, null, null);
        }
        catch (WorkbenchException ex)
        {
            var current = await tasks.GetAsync(taskId, cancellationToken);
            return new ClaimResult(false, current, ex.Message, current?.ClaimedBy);
        }
    }

    [McpServerTool(Name = "workbench_get_context")]
    [Description(
        "一次性取回干活所需的全部上下文：任务信息、关联文档、已有计划、产物、最近事件、当前允许的状态迁移、git 分支与 HEAD。" +
        "先调这个，再决定做什么，避免反复零散调用。")]
    public async Task<TaskContextDto> GetContextAsync(
        Guid taskId,
        int eventLimit = 20,
        CancellationToken cancellationToken = default)
        => await tasks.GetContextAsync(taskId, eventLimit, cancellationToken);

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
        if (!Enum.TryParse<TaskStatus>(status, ignoreCase: true, out var target))
        {
            return StatusChangeResult.Failed(
                $"未知状态: {status}",
                Enum.GetNames<TaskStatus>().ToList());
        }

        var eventSource = Enum.TryParse<TaskEventSource>(source, ignoreCase: true, out var parsedSource)
            ? parsedSource
            : TaskEventSource.Agent;

        try
        {
            var task = await tasks.ChangeStatusAsync(taskId, target, note, eventSource, actor, cancellationToken);
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

        return await approvals.ListAsync(parsed, projectId, cancellationToken);
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
        => await audit.ListAsync(toolName, taskId, projectId, limit, cancellationToken);

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
    [Description("移除一条任务依赖（两个方向都试）。")]
    public async Task<bool> RemoveDependencyAsync(
        Guid taskId,
        Guid dependsOnTaskId,
        CancellationToken cancellationToken = default)
    {
        await dependencies.RemoveAsync(taskId, dependsOnTaskId, cancellationToken);
        return true;
    }

    [McpServerTool(Name = "workbench_export_events")]
    [Description(
        "导出任务的事件流水（账本）。format 传 markdown（默认）/ csv / json。" +
        "format=json 时返回结构化对象，其余返回文本。")]
    public async Task<object> ExportEventsAsync(
        Guid taskId,
        string format = "markdown",
        CancellationToken cancellationToken = default)
    {
        var text = await exports.ExportTaskEventsAsync(taskId, format, cancellationToken);

        return format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? new { ok = true, format = "json", content = text }
            : new { ok = true, format, content = text };
    }

    [McpServerTool(Name = "workbench_export_review")]
    [Description(
        "导出评审报告。round 不给就是最新一轮。format 传 markdown（默认）/ json。")]
    public async Task<object> ExportReviewAsync(
        Guid taskId,
        int? round = null,
        string format = "markdown",
        CancellationToken cancellationToken = default)
    {
        var text = await exports.ExportReviewReportAsync(taskId, round, format, cancellationToken);

        return new { ok = true, format, round, content = text };
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
