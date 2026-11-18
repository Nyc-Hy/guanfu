using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;

namespace Guanfu.Tests;

public class MilestoneServiceTests
{
    [Fact]
    public async Task 创建里程碑后列表能看到()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var created = await host.Milestones.CreateAsync(project.Id, "第一期");

        var list = await host.Milestones.ListAsync(project.Id);
        Assert.Single(list);
        Assert.Equal("第一期", list[0].Name);
        Assert.Equal(created.Id, list[0].Id);
    }

    [Fact]
    public async Task 里程碑进度按任务状态算()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "第一期");

        var t1 = await host.Tasks.CreateAsync(project.Id, "任务一");
        var t2 = await host.Tasks.CreateAsync(project.Id, "任务二");
        await host.Milestones.AssignTaskAsync(t1.Id, milestone.Id);
        await host.Milestones.AssignTaskAsync(t2.Id, milestone.Id);

        await host.Tasks.ChangeStatusAsync(t1.Id, TaskStatus.DocsReady);

        var list = await host.Milestones.ListAsync(project.Id);
        Assert.Equal(2, list[0].TotalTasks);
        Assert.Equal(0, list[0].DoneTasks);
        Assert.True(list[0].Progress > 0);
        Assert.True(list[0].Progress < 1);
    }

    [Fact]
    public async Task 列表里每个里程碑只算自己名下的任务()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Milestones.CreateAsync(project.Id, "第一期");
        var second = await host.Milestones.CreateAsync(project.Id, "第二期");

        var t1 = await host.Tasks.CreateAsync(project.Id, "任务一");
        var t2 = await host.Tasks.CreateAsync(project.Id, "任务二");
        var t3 = await host.Tasks.CreateAsync(project.Id, "任务三");
        await host.Milestones.AssignTaskAsync(t1.Id, first.Id);
        await host.Milestones.AssignTaskAsync(t2.Id, first.Id);
        await host.Milestones.AssignTaskAsync(t3.Id, second.Id);
        await host.Tasks.ChangeStatusAsync(t3.Id, TaskStatus.DocsReady);

        var list = await host.Milestones.ListAsync(project.Id);
        var firstDto = list.Single(m => m.Id == first.Id);
        var secondDto = list.Single(m => m.Id == second.Id);

        Assert.Equal(2, firstDto.TotalTasks);
        Assert.Equal(1, secondDto.TotalTasks);
        Assert.Equal(0d, firstDto.Progress);
        Assert.True(secondDto.Progress > 0);
    }

    [Fact]
    public async Task 删里程碑只摘任务不删任务()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "第一期");
        var task = await host.Tasks.CreateAsync(project.Id, "任务一");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        await host.Milestones.DeleteAsync(milestone.Id);

        var tasks = await host.Db.Tasks.Where(t => t.Id == task.Id).ToListAsync();
        Assert.Single(tasks);
        Assert.Null(tasks[0].MilestoneId);

        var milestones = await host.Milestones.ListAsync(project.Id);
        Assert.Empty(milestones);
    }

    [Fact]
    public async Task 任务只能挂到同项目的里程碑()
    {
        using var host = new GuanfuTestHost();
        var projectA = await host.Projects.CreateAsync("项目A");
        var projectB = await host.Projects.CreateAsync("项目B");

        var milestoneB = await host.Milestones.CreateAsync(projectB.Id, "B的里程碑");
        var taskA = await host.Tasks.CreateAsync(projectA.Id, "A的任务");

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Milestones.AssignTaskAsync(taskA.Id, milestoneB.Id));
    }

    [Fact]
    public async Task 里程碑不存在时挂载失败()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "任务一");

        await Assert.ThrowsAsync<NotFoundException>(
            () => host.Milestones.AssignTaskAsync(task.Id, Guid.NewGuid()));
    }
}
