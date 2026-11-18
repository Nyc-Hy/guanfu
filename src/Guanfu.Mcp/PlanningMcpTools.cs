using System.ComponentModel;
using ModelContextProtocol.Server;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Mcp;

/// <summary>
/// 里程碑、度量与项目配置。给 agent 用来理解「这批任务归哪一组」「进度怎么样」。
/// </summary>
[McpServerToolType]
public sealed class PlanningMcpTools(
    IMilestoneService milestones,
    IMetricsService metrics,
    IProjectService projects,
    ITaskService tasks,
    IProjectSpecService? specs = null,
    IDispatchService? dispatch = null,
    IProjectScopeResolver? scopeResolver = null)
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

        throw new GuanfuException(
            "当前调用没有绑定项目，也没有传 projectId。请先调用 guanfu_bind_session。");
    }

    [McpServerTool(Name = "guanfu_list_milestones")]
    [Description("列出项目下的里程碑，含任务数、完成数与进度")]
    public async Task<IReadOnlyList<MilestoneDto>> ListMilestonesAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);
        return await milestones.ListAsync(scope.ProjectId, cancellationToken);
    }

    [McpServerTool(Name = "guanfu_assign_milestone")]
    [Description("把任务挂到里程碑下；milestoneId 传空表示从里程碑里摘出来")]
    public async Task<TaskSummaryDto> AssignMilestoneAsync(
        Guid taskId,
        Guid? milestoneId = null,
        CancellationToken cancellationToken = default)
    {
        await milestones.AssignTaskAsync(taskId, milestoneId, cancellationToken);

        var task = await tasks.GetAsync(taskId, cancellationToken);
        if (task is null)
        {
            throw new GuanfuException($"任务 {taskId} 未找到。");
        }

        return task;
    }

    [McpServerTool(Name = "guanfu_get_metrics")]
    [Description("取项目度量：状态分布、近 8 周流速、平均周期、测试通过率、平均覆盖率、待审数、停滞数")]
    public async Task<ProjectMetricsDto> GetMetricsAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);
        return await metrics.GetProjectMetricsAsync(scope.ProjectId, cancellationToken);
    }

    [McpServerTool(Name = "guanfu_get_project_config")]
    [Description("读取项目配置：仓库路径、远端、默认分支、shell 白名单、agent 启动命令、通知地址")]
    public async Task<ProjectConfigDto> GetProjectConfigAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);
        return await projects.GetConfigAsync(scope.ProjectId, cancellationToken);
    }

    [McpServerTool(Name = "guanfu_get_project_spec")]
    [Description(
        "读取**规约**（人定的规矩）：项目目标、完成定义（DoD，逐条）、" +
        "提交信息约定、测试命令、分支命名约定、红线与禁忌。" +
        "**开工前先读它** —— 「什么算做完」「提交怎么写」「测试怎么跑」在这儿，" +
        "别自己猜。评审时它就是尺子。" +
        "给 taskId 时返回**对这个活真正生效**的那份（项目级 + 它所在里程碑的那一层，" +
        "里程碑的优先、DoD 两层叠加）；不给就返回项目级的。" +
        "没填过会返回空规约（不是错误）。已绑定会话时不用传 projectId。")]
    public async Task<ProjectSpecDto> GetProjectSpecAsync(
        Guid? projectId = null,
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);

        if (specs is null)
        {
            return ProjectSpecDto.Empty(scope.ProjectId);
        }

        if (taskId is Guid id)
        {
            return await specs.GetEffectiveAsync(id, cancellationToken);
        }

        return await specs.GetAsync(scope.ProjectId, cancellationToken);
    }

    [McpServerTool(Name = "guanfu_get_dispatch_board")]
    [Description(
        "调度板：这个项目**现在什么能干什么卡着谁在干**。" +
        "三个桶：ready（前置全清、没人占着 —— 可以直接领）、" +
        "inProgress（有人领着，租约还活着）、" +
        "blocked（还有前置没做完，带上卡它的是谁、阻塞链多深）。" +
        "选活之前先看这个，别自己 list 一堆任务再猜。" +
        "已绑定会话时不用传 projectId。")]
    public async Task<DispatchBoardDto> GetDispatchBoardAsync(
        Guid? projectId = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);

        if (dispatch is null)
        {
            return new DispatchBoardDto(scope.ProjectId, [], [], [], 0, 0, 0);
        }

        return await dispatch.GetAsync(scope.ProjectId, limit, cancellationToken);
    }

    [McpServerTool(Name = "guanfu_suggest_tasks")]
    [Description(
        "「先干哪个」：把这个项目里**现在能干的活**排个序，并给出理由。" +
        "排序看四件事：优先级、权重、等了多久（防饥饿）、做完能解锁几个任务（别人在等它）。" +
        "每条都带 reasons 说明为什么排在这儿 —— agent 看得懂理由，人也看得懂凭什么。" +
        "已经在干的活不会出现在列表里。" +
        "已绑定会话时不用传 projectId。")]
    public async Task<SuggestionToolResult> SuggestTasksAsync(
        Guid? projectId = null,
        int limit = 10,
        string? excludeAgentId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(projectId, cancellationToken);

        if (dispatch is null)
        {
            return new SuggestionToolResult { Ok = true, Suggestions = [] };
        }

        // 「谁在问」也要带进去 —— 指派给他的活排前面（人工指派也是干预）
        var suggestions = await dispatch.SuggestAsync(
            scope.ProjectId, limit, excludeAgentId, cancellationToken, scope.AgentId);

        return new SuggestionToolResult { Ok = true, Suggestions = suggestions };
    }

    [McpServerTool(Name = "guanfu_create_project")]
    [Description(
        "创建一个新项目。name 必填，description 可选。返回 ok=true 与项目信息（含 id）；" +
        "失败时 error 给出原因（如名称为空），据此修正后重试。")]
    public async Task<ProjectCreateResult> CreateProjectAsync(
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await projects.CreateAsync(name, description, cancellationToken);
            return new ProjectCreateResult(true, project);
        }
        catch (Exception ex)
        {
            return new ProjectCreateResult(false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "guanfu_create_milestone")]
    [Description(
        "在项目下创建里程碑。kind 传 release（迭代版本，默认）或 group（需求组）；" +
        "parentId 把它挂到父里程碑下（构成 版本→需求组 层级）；dueAt 是截止日。" +
        "返回 ok=true 与里程碑；失败时 error 给出原因（如种类非法、父里程碑不存在），据此修正后重试。")]
    public async Task<MilestoneOpResult> CreateMilestoneAsync(
        Guid? projectId = null,
        string? name = null,
        string? description = null,
        DateTimeOffset? dueAt = null,
        Guid? parentId = null,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new GuanfuException("里程碑名称不能为空。");
            }

            var scope = await ResolveScopeAsync(projectId, cancellationToken);

            var parsedKind = MilestoneKind.Release;
            if (!string.IsNullOrWhiteSpace(kind) &&
                !Enum.TryParse<MilestoneKind>(kind, ignoreCase: true, out parsedKind))
            {
                return new MilestoneOpResult(
                    false, null,
                    $"未知里程碑种类: {kind}。合法值: {string.Join("、", Enum.GetNames<MilestoneKind>())}");
            }

            var dto = await milestones.CreateAsync(
                scope.ProjectId, name!, description, dueAt, cancellationToken, parentId, parsedKind);
            return new MilestoneOpResult(true, dto);
        }
        catch (Exception ex)
        {
            return new MilestoneOpResult(false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "guanfu_update_milestone")]
    [Description(
        "更新里程碑。不传的字段保持原值。要**清空**父里程碑用 clearParent=true，要**清空**截止日用 clearDue=true" +
        "——因为 parentId/dueAt 传 null 表示「不改」，没法表达「清空」，所以单独给两个布尔。" +
        "kind 传 release / group 改种类。返回 ok=true 与更新后的里程碑；失败时 error 给出原因。")]
    public async Task<MilestoneOpResult> UpdateMilestoneAsync(
        Guid milestoneId,
        string? name = null,
        string? description = null,
        DateTimeOffset? dueAt = null,
        int? order = null,
        Guid? parentId = null,
        string? kind = null,
        bool clearParent = false,
        bool clearDue = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            MilestoneKind? parsedKind = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                if (!Enum.TryParse<MilestoneKind>(kind, ignoreCase: true, out var value))
                {
                    return new MilestoneOpResult(
                        false, null,
                        $"未知里程碑种类: {kind}。合法值: {string.Join("、", Enum.GetNames<MilestoneKind>())}");
                }

                parsedKind = value;
            }

            var dto = await milestones.UpdateAsync(
                milestoneId, name, description, dueAt, order, cancellationToken,
                parentId, parsedKind, clearParent, clearDue);
            return new MilestoneOpResult(true, dto);
        }
        catch (Exception ex)
        {
            return new MilestoneOpResult(false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "guanfu_delete_milestone")]
    [Description(
        "删除一个里程碑。返回 ok=true；失败时 error 给出原因（如里程碑不存在、下面还挂着任务或子里程碑），" +
        "据此先清理关联再重试。")]
    public async Task<MilestoneDeleteResult> DeleteMilestoneAsync(
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await milestones.DeleteAsync(milestoneId, cancellationToken);
            return new MilestoneDeleteResult(true);
        }
        catch (Exception ex)
        {
            return new MilestoneDeleteResult(false, ex.Message);
        }
    }
}

/// <summary>创建项目的结果。</summary>
public sealed record ProjectCreateResult(
    bool Ok,
    ProjectSummaryDto? Project,
    string? Error = null);

/// <summary>里程碑创建 / 更新的结果。</summary>
public sealed record MilestoneOpResult(
    bool Ok,
    MilestoneDto? Milestone,
    string? Error = null);

/// <summary>里程碑删除的结果。</summary>
public sealed record MilestoneDeleteResult(
    bool Ok,
    string? Error = null);

/// <summary>调度建议的返回形状。</summary>
public sealed record SuggestionToolResult
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<DispatchSuggestionDto> Suggestions { get; init; } = [];
}
