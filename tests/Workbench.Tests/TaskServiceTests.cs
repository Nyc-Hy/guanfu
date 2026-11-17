using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Tests;

public class TaskServiceTests
{
    [Fact]
    public async Task 创建任务会写一条初始流水()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        Assert.Equal(TaskStatus.Draft, task.Status);
        Assert.Equal("写登录接口", task.Title);

        var events = await host.Db.StatusEvents
            .Where(e => e.TaskId == task.Id)
            .ToListAsync();

        Assert.Single(events);
        Assert.Equal(TaskStatus.Draft, events[0].ToStatus);
    }

    [Fact]
    public async Task 合法迁移会留下前后状态()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady, "PRD 已上传", TaskEventSource.Agent, "agent-1");

        var events = await host.Db.StatusEvents
            .Where(e => e.TaskId == task.Id)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(TaskStatus.Draft, events[0].ToStatus);
        Assert.Equal(TaskStatus.DocsReady, events[1].ToStatus);
        Assert.Equal(TaskStatus.Draft, events[1].FromStatus);
        Assert.Equal("agent-1", events[1].Actor);
        Assert.Equal("PRD 已上传", events[1].Note);
    }

    [Fact]
    public async Task 非法迁移被拒绝且状态不变()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await Assert.ThrowsAsync<InvalidStatusTransitionException>(
            () => host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done));

        var after = await host.Tasks.GetAsync(task.Id);
        Assert.Equal(TaskStatus.Draft, after!.Status);
    }

    [Fact]
    public async Task 认领会占用租约并挡住其他agent()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var claimed = await host.Tasks.ClaimAsync(task.Id, "agent-1", 30);

        Assert.Equal("agent-1", claimed.Task.ClaimedBy);
        Assert.NotNull(claimed.Task.LeaseExpiresAt);

        var conflict = await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Tasks.ClaimAsync(task.Id, "agent-2", 30));
        Assert.Contains("已被", conflict.Message);
    }

    [Fact]
    public async Task 租约过期后可以被其他agent重新认领()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.ClaimAsync(task.Id, "agent-1", 1);
        host.Clock.Advance(TimeSpan.FromMinutes(5));

        var reclaimed = await host.Tasks.ClaimAsync(task.Id, "agent-2", 30);

        Assert.Equal("agent-2", reclaimed.Task.ClaimedBy);
    }

    [Fact]
    public async Task 上下文包含计划文档产物与允许的迁移()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.SetPlanAsync(task.Id, "背景", "理解", "拆解", "验收", "风险", "agent-1");
        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.CodeChange, "改了 3 个文件", "diff...");
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady, null, TaskEventSource.Agent, "agent-1");

        var context = await host.Tasks.GetContextAsync(task.Id);

        Assert.NotNull(context.Plan);
        Assert.Equal("理解", context.Plan!.Understanding);
        Assert.Single(context.Artifacts);
        Assert.Contains(TaskStatus.Planned.ToString(), context.AllowedTransitions);
        Assert.NotEmpty(context.RecentEvents);
    }

    [Fact]
    public async Task 计划会按版本递增()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var v1 = await host.Tasks.SetPlanAsync(task.Id, null, "第一版", null, null, null, "agent-1");
        var v2 = await host.Tasks.SetPlanAsync(task.Id, null, "第二版", null, null, null, "agent-1");

        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
    }

    [Fact]
    public async Task 空项目的进度是零而不是NaN()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("还没有任务的项目");

        var summary = await host.Projects.GetAsync(project.Id);

        // 除零会得到 NaN，而 NaN 不是合法 JSON，会让列表接口整个 500
        Assert.Equal(0d, summary!.Progress);
        Assert.True(double.IsFinite(summary.Progress));
        Assert.Equal(0, summary.TotalTasks);
    }

    [Fact]
    public async Task 项目进度按状态权重计算()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var a = await host.Tasks.CreateAsync(project.Id, "A");
        var b = await host.Tasks.CreateAsync(project.Id, "B");

        // 状态机不允许跳步，必须顺着合法路径走
        await WalkToAsync(host, a.Id, TaskStatus.Done);
        await WalkToAsync(host, b.Id, TaskStatus.InDev);

        var summary = await host.Projects.GetAsync(project.Id);

        // (1.0 + 0.5) / 2 = 0.75
        Assert.Equal(0.75, summary!.Progress, 3);
        Assert.Equal(1, summary.DoneTasks);
        Assert.Equal(2, summary.TotalTasks);
    }

    private static async Task WalkToAsync(WorkbenchTestHost host, Guid taskId, TaskStatus target)
    {
        var path = new[]
        {
            TaskStatus.DocsReady, TaskStatus.Planned, TaskStatus.InDev,
            TaskStatus.Testing, TaskStatus.Review, TaskStatus.Done
        };

        foreach (var next in path)
        {
            await host.Tasks.ChangeStatusAsync(taskId, next);
            if (next == target)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task 终态会释放认领租约()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.ClaimAsync(task.Id, "agent-1", 30);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);
        var done = await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);

        Assert.Null(done.ClaimedBy);
        Assert.Null(done.LeaseExpiresAt);
    }
}
