using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// agent 的工作包：「我现在该干什么」。
///
/// 这是**给 agent 的**一份：不是「这个项目里有什么」，而是「跟我有关的部分是什么」。
///
/// agent 最容易漏的就是「我卡着谁」那一项 ——
/// 看板上看得到全局的阻塞关系，但没人告诉这个 agent：
/// 你手上这个活做完，有人在等着。
/// </summary>
public sealed class MyWorkServiceTests
{
    private static async Task<(GuanfuTestHost Host, MyWorkService Service, Guid ProjectId)> SetupAsync()
    {
        var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var service = new MyWorkService(
            host.Db,
            host.Clock,
            new DispatchService(host.Db, host.Clock),
            host.Reviews,
            host.Specs);

        return (host, service, project.Id);
    }

    [Fact]
    public async Task 工作包带上我正做的活()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        var task = await host.Tasks.CreateAsync(projectId, "我的活");
        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        var work = await service.GetAsync(projectId, "agent-a");

        Assert.Equal("agent-a", work.AgentId);
        Assert.NotNull(work.CurrentTask);
        Assert.Equal(task.Id, work.CurrentTask!.Id);
        Assert.Single(work.MyTasks);
    }

    [Fact]
    public async Task 没人持有活时工作包也是空的但不是崩()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        await host.Tasks.CreateAsync(projectId, "没人要的活");

        var work = await service.GetAsync(projectId, "agent-a");

        Assert.Null(work.CurrentTask);
        Assert.Empty(work.MyTasks);
    }

    [Fact]
    public async Task 工作包说得清我卡着谁()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        var mine = await host.Tasks.CreateAsync(projectId, "我做的活");
        var waiting = await host.Tasks.CreateAsync(projectId, "等我的活");

        await host.Dependencies.AddAsync(waiting.Id, mine.Id);
        await host.Tasks.ClaimAsync(mine.Id, "agent-a");

        var work = await service.GetAsync(projectId, "agent-a");

        var blocking = Assert.Single(work.Blocking);
        Assert.Equal(waiting.Id, blocking.TaskId);
        Assert.Equal(waiting.Key, blocking.TaskKey);
        Assert.Equal(mine.Key, blocking.WaitingOnKey);
        Assert.Equal("agent-a", blocking.WaitingOnHolder);
    }

    [Fact]
    public async Task 工作包记得我放掉过什么()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        var task = await host.Tasks.CreateAsync(projectId, "放掉的活");
        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "卡在依赖的服务没起来");

        var work = await service.GetAsync(projectId, "agent-a");

        var handed = Assert.Single(work.HandedOver);
        Assert.Equal(task.Id, handed.TaskId);
        Assert.Equal("卡在依赖的服务没起来", handed.Reason);
    }

    [Fact]
    public async Task 工作包带上建议与规约()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        var spec = await host.Specs.UpdateAsync(projectId, new ProjectSpecInput(
            Purpose: "12月版本要支持登录",
            DefinitionOfDone: ["有单测覆盖新增逻辑"],
            TestCommand: "dotnet test"));

        await host.Tasks.CreateAsync(projectId, "可以干的活");

        var work = await service.GetAsync(projectId, "agent-a");

        Assert.NotEmpty(work.Suggestions);
        Assert.Equal("12月版本要支持登录", work.Spec!.Purpose);
        Assert.Equal(spec.DefinitionOfDone, work.Spec.DefinitionOfDone);
    }

    [Fact]
    public async Task 工作包说得出我认领了哪个维度还没交()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        var task = await host.Tasks.CreateAsync(projectId, "要评审的活");
        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);
        await host.Reviews.StartAsync(task.Id, "agent-a");

        await host.Reviews.ClaimDimensionAsync(task.Id, "security", "agent-a");

        var work = await service.GetAsync(projectId, "agent-a");

        Assert.NotNull(work.Review);
        Assert.Equal("是否有安全问题", Assert.Single(work.PendingReviewDimensions));
    }

    [Fact]
    public async Task 不知道是谁在问就说清()
    {
        var (host, service, projectId) = await SetupAsync();
        using var _ = host;

        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => service.GetAsync(projectId, "  "));

        Assert.Contains("得先说是谁", ex.Message);
    }
}
