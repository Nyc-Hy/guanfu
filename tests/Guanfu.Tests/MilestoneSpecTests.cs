using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 规约的层级：项目级 + 里程碑级。
///
/// 「12 月版本要额外写迁移说明」这类只对某个版本成立的要求，
/// 不该塞进项目级规约里污染所有活 —— 但它又是真的要做完的事，
/// 所以它得是一等实体，而不是一段备注。
/// </summary>
public class MilestoneSpecTests
{
    [Fact]
    public async Task 生效的完成定义是两层叠加()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        var specs = new ProjectSpecService(host.Db, host.Clock);

        await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["有单测覆盖", "文档同步更新"]));

        await specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["额外写迁移说明"]));

        var effective = await specs.GetEffectiveDefinitionOfDoneAsync(task.Id);

        Assert.Equal(["有单测覆盖", "文档同步更新", "额外写迁移说明"], effective);
    }

    [Fact]
    public async Task 里程碑的规约只影响它自己的活()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var inside = await host.Tasks.CreateAsync(project.Id, "版本里的");
        var outside = await host.Tasks.CreateAsync(project.Id, "版本外的");
        await host.Milestones.AssignTaskAsync(inside.Id, milestone.Id);

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["额外写迁移说明"]));

        Assert.Equal(["额外写迁移说明"], await specs.GetEffectiveDefinitionOfDoneAsync(inside.Id));
        Assert.Empty(await specs.GetEffectiveDefinitionOfDoneAsync(outside.Id));
    }

    [Fact]
    public async Task 生效的规约里里程碑那层优先()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            Purpose: "项目目标", TestCommand: "dotnet test"));

        await specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            Purpose: "这个版本的目标", TestCommand: "dotnet test --filter Milestone"));

        var effective = await specs.GetEffectiveAsync(task.Id);

        Assert.Equal("这个版本的目标", effective.Purpose);
        Assert.Equal("dotnet test --filter Milestone", effective.TestCommand);
    }

    [Fact]
    public async Task 里程碑必须属于这个项目()
    {
        using var host = new GuanfuTestHost();
        var mine = await host.Projects.CreateAsync("我的");
        var other = await host.Projects.CreateAsync("别人的");
        var foreign = await host.Milestones.CreateAsync(other.Id, "别的项目的版本");

        var specs = new ProjectSpecService(host.Db, host.Clock);

        await Assert.ThrowsAsync<GuanfuException>(
            async () => await specs.UpdateForMilestoneAsync(
                mine.Id, foreign.Id, new ProjectSpecInput(Purpose: "x")));
    }

    [Fact]
    public async Task 里程碑级的完成定义也要进结项门禁()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["额外写迁移说明"]));

        // 评审服务要拿到同一份规约（同一个口径）
        var reviews = new ReviewService(host.Db, host.Clock, host.Git, specs);
        var tasks = new TaskService(host.Db, host.Clock, host.Git, reviews, null, null, null, specs);

        await tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);

        await reviews.StartAsync(task.Id, "评审人");
        foreach (var key in ReviewDimensions.Keys)
        {
            await reviews.SubmitAsync(task.Id, key, true, "无问题", "单测通过", null, "评审人");
        }

        // 里程碑级的 DoD 一条没判 → 不许结项
        var blocked = await Assert.ThrowsAsync<ReviewGateException>(
            async () => await tasks.ChangeStatusAsync(task.Id, TaskStatus.Done));

        Assert.Contains("额外写迁移说明", blocked.Message);
    }

    [Fact]
    public async Task 里程碑级的判定也能放行结项()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["额外写迁移说明"]));

        var reviews = new ReviewService(host.Db, host.Clock, host.Git, specs);
        var tasks = new TaskService(host.Db, host.Clock, host.Git, reviews, null, null, null, specs);

        await tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);

        await reviews.StartAsync(task.Id, "评审人");
        foreach (var key in ReviewDimensions.Keys)
        {
            await reviews.SubmitAsync(task.Id, key, true, "无问题", "单测通过", null, "评审人");
        }

        await reviews.SubmitSpecVerdictAsync(task.Id, "额外写迁移说明", true, "写了", "评审人");

        var done = await tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }
}
