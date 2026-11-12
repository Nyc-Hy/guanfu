using System.ComponentModel;
using ModelContextProtocol.Server;
using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Mcp;

/// <summary>
/// 里程碑、度量与项目配置。给 agent 用来理解「这批任务归哪一组」「进度怎么样」。
/// </summary>
[McpServerToolType]
public sealed class PlanningMcpTools(
    IMilestoneService milestones,
    IMetricsService metrics,
    IProjectService projects,
    ITaskService tasks)
{
    [McpServerTool(Name = "workbench_list_milestones")]
    [Description("列出项目下的里程碑，含任务数、完成数与进度")]
    public async Task<IReadOnlyList<MilestoneDto>> ListMilestonesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
        => await milestones.ListAsync(projectId, cancellationToken);

    [McpServerTool(Name = "workbench_assign_milestone")]
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
            throw new WorkbenchException($"任务 {taskId} 未找到。");
        }

        return task;
    }

    [McpServerTool(Name = "workbench_get_metrics")]
    [Description("取项目度量：状态分布、近 8 周流速、平均周期、测试通过率、平均覆盖率、待审数、停滞数")]
    public async Task<ProjectMetricsDto> GetMetricsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
        => await metrics.GetProjectMetricsAsync(projectId, cancellationToken);

    [McpServerTool(Name = "workbench_get_project_config")]
    [Description("读取项目配置：仓库路径、远端、默认分支、shell 白名单、agent 启动命令、通知地址")]
    public async Task<ProjectConfigDto> GetProjectConfigAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
        => await projects.GetConfigAsync(projectId, cancellationToken);
}
