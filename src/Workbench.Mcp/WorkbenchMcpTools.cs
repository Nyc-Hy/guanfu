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

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchMcp(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "workbench",
                    Version = "0.1.0",
                    Title = "开发工作台"
                };

                options.ServerInstructions =
                    "开发工作台是状态中枢与审批门禁，不提供 AI 服务。" +
                    "典型流程：workbench_claim_task 领任务 → workbench_get_context 取上下文 → " +
                    "workbench_set_plan 回写计划 → 开发 → workbench_report_test_result 回写测试 → " +
                    "workbench_update_status 推进状态。写数据库必须走 db_execute（需人工审批）。";
            })
            .WithToolsFromAssembly(typeof(WorkbenchMcpTools).Assembly, WorkbenchJson.Options)
            .WithHttpTransport();

        return services;
    }
}

/// <summary>
/// 工作台自身的 MCP 工具。给外部 agent（Claude Code / Codex 等）调用。
/// </summary>
[McpServerToolType]
public sealed class WorkbenchMcpTools(
    IProjectService projects,
    ITaskService tasks,
    IApprovalService approvals)
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
    [Description("在项目下创建一个需求任务，初始状态为 Draft")]
    public async Task<TaskSummaryDto> CreateTaskAsync(
        Guid projectId,
        string title,
        string? description = null,
        Guid? parentTaskId = null,
        CancellationToken cancellationToken = default)
        => await tasks.CreateAsync(projectId, title, description, parentTaskId, cancellationToken);

    [McpServerTool(Name = "workbench_list_tasks")]
    [Description("按项目 / 状态 / 关键字列出任务")]
    public async Task<IReadOnlyList<TaskSummaryDto>> ListTasksAsync(
        Guid? projectId = null,
        string? status = null,
        string? keyword = null,
        CancellationToken cancellationToken = default)
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

        return await tasks.ListAsync(projectId, parsed, keyword, cancellationToken);
    }

    [McpServerTool(Name = "workbench_get_task")]
    [Description("获取单个任务的详情")]
    public async Task<TaskSummaryDto?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
        => await tasks.GetAsync(taskId, cancellationToken);

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
        "任意态可进 Blocked，Review 可打回 InDev。")]
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
