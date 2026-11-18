using Guanfu.Application.Services;

namespace Guanfu.Tests;

/// <summary>
/// 审批要带得上关联任务与项目。
///
/// 人批一条写操作时，判断该不该批的依据是「这是哪个需求的活」。
/// ApprovalDto 里光有 TaskId（GUID）不行 —— 界面上不许露 GUID，
/// 得把任务编号（REQ-001）和项目名一起带出来，人才不用点进去确认。
/// </summary>
public sealed class ApprovalLinkTests
{
    [Fact]
    public async Task 审批列表带得上任务编号与项目名()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("演示项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var service = new ApprovalService(host.Db, host.Clock);
        await service.RequestAsync(
            "shell_run",
            "shell.write",
            "{\"command\":\"dotnet test\"}",
            "在 演示项目 执行：dotnet test",
            requestedBy: "agent-demo",
            taskId: task.Id,
            projectId: project.Id);

        var approval = Assert.Single(await service.ListAsync());

        Assert.Equal(task.Id, approval.TaskId);
        Assert.Equal("REQ-001", approval.TaskKey);
        Assert.Equal("写登录接口", approval.TaskTitle);
        Assert.Equal("演示项目", approval.ProjectName);
    }

    [Fact]
    public async Task 没关联任务的审批不会瞎编任务信息()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("演示项目");

        var service = new ApprovalService(host.Db, host.Clock);
        await service.RequestAsync(
            "shell_run", "shell.write", "{}", "跑个命令", requestedBy: "agent-demo", projectId: project.Id);

        var approval = Assert.Single(await service.ListAsync());

        Assert.Null(approval.TaskId);
        Assert.Null(approval.TaskKey);
        Assert.Null(approval.TaskTitle);
        Assert.Equal("演示项目", approval.ProjectName);
    }

    [Fact]
    public async Task 单条查询也带得上任务编号()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("演示项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var service = new ApprovalService(host.Db, host.Clock);
        var created = await service.RequestAsync(
            "shell_run", "shell.write", "{}", "跑个命令", taskId: task.Id, projectId: project.Id);

        var fetched = await service.GetAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal("REQ-001", fetched!.TaskKey);
        Assert.Equal("写登录接口", fetched.TaskTitle);
    }
}
