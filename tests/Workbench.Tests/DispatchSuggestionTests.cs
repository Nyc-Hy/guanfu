using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 调度建议：「先干哪个」。
///
/// 调度板回答「什么能干」，这个回答「先干哪个」——
/// 同样能干的活有五个，挑哪个、为什么挑，是两回事。
/// 排序必须**可解释**：agent 看得懂理由，人也看得懂凭什么。
/// </summary>
public class DispatchSuggestionTests
{
    [Fact]
    public async Task 紧急的活排前面()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var normal = await host.Tasks.CreateAsync(project.Id, "普通的活");
        var critical = await host.Tasks.CreateAsync(project.Id, "紧急的活");
        await host.Tasks.SetAssignmentAsync(critical.Id, priority: TaskPriority.Critical);

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(critical.Id, suggestions[0].TaskId);
        Assert.Contains(suggestions[0].Reasons, r => r.Contains("紧急"));
    }

    [Fact]
    public async Task 等得久的活会被提上来()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var waiting = await host.Tasks.CreateAsync(project.Id, "等了很久的");

        // 过几天才建第二个活 —— 一个等得久、一个刚建
        host.Clock.Advance(TimeSpan.FromDays(6));
        var fresh = await host.Tasks.CreateAsync(project.Id, "刚建的");

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.Equal(waiting.Id, suggestions[0].TaskId);
        Assert.Contains(suggestions[0].Reasons, r => r.Contains("等"));
    }

    [Fact]
    public async Task 能解锁别人的活排前面()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var blocker = await host.Tasks.CreateAsync(project.Id, "前置");
        var dependent = await host.Tasks.CreateAsync(project.Id, "依赖它的");
        await host.Dependencies.AddAsync(dependent.Id, blocker.Id);

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.Equal(blocker.Id, suggestions[0].TaskId);
        Assert.Equal(1, suggestions[0].Unblocks);
        Assert.Contains(suggestions[0].Reasons, r => r.Contains("解锁"));
    }

    [Fact]
    public async Task 前置没做完与有人在干的都不在候选里()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var blocker = await host.Tasks.CreateAsync(project.Id, "前置");
        var dependent = await host.Tasks.CreateAsync(project.Id, "被挡的");
        await host.Dependencies.AddAsync(dependent.Id, blocker.Id);

        var held = await host.Tasks.CreateAsync(project.Id, "有人在干的");
        await host.Tasks.ClaimAsync(held.Id, "agent-a");

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.DoesNotContain(suggestions, s => s.TaskId == dependent.Id);
        Assert.DoesNotContain(suggestions, s => s.TaskId == held.Id);
        Assert.Contains(suggestions, s => s.TaskId == blocker.Id);
    }

    [Fact]
    public async Task 主工作区被占时要提示开自己的worktree()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "能干的活");

        var service = new DispatchService(host.Db, host.Clock);
        var before = await service.SuggestAsync(project.Id);
        Assert.Null(before[0].StationHint);

        // 另一个 agent 占了主工作区
        var holder = await host.Tasks.CreateAsync(project.Id, "占着的活");
        await host.Tasks.ClaimAsync(holder.Id, "agent-a");

        var after = await service.SuggestAsync(project.Id);
        var suggestion = after.First(s => s.TaskId == task.Id);
        Assert.NotNull(suggestion.StationHint);
        Assert.Contains("worktree", suggestion.StationHint);
    }

    [Fact]
    public async Task 人工排的活排最前()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var normal = await host.Tasks.CreateAsync(project.Id, "普通的活");
        var pinned = await host.Tasks.CreateAsync(project.Id, "人排的活");

        await host.Tasks.SetPinnedAsync(pinned.Id, true);

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.Equal(pinned.Id, suggestions[0].TaskId);
        Assert.Contains(suggestions[0].Reasons, r => r.Contains("人工排"));
        Assert.Null(suggestions.Single(s => s.TaskId == normal.Id).PinnedOrder);
    }

    [Fact]
    public async Task 排进计划的顺序就是人点的顺序()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var first = await host.Tasks.CreateAsync(project.Id, "先做的");
        var second = await host.Tasks.CreateAsync(project.Id, "后做的");
        var third = await host.Tasks.CreateAsync(project.Id, "最后做的");

        await host.Tasks.SetPinnedAsync(first.Id, true);
        await host.Tasks.SetPinnedAsync(second.Id, true);
        await host.Tasks.SetPinnedAsync(third.Id, true);

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.Equal([first.Id, second.Id, third.Id], suggestions.Select(x => x.TaskId).ToArray());
    }

    [Fact]
    public async Task 移出计划就回到按分排()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var pinned = await host.Tasks.CreateAsync(project.Id, "排过的");
        var other = await host.Tasks.CreateAsync(project.Id, "没排的");

        await host.Tasks.SetPinnedAsync(pinned.Id, true);
        await host.Tasks.SetPinnedAsync(pinned.Id, false);

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.All(suggestions, s => Assert.Null(s.PinnedOrder));
        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, s => s.TaskId == other.Id);
    }

    [Fact]
    public async Task 指派给谁的活排在谁前面()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var mine = await host.Tasks.CreateAsync(project.Id, "指派给我的");
        var others = await host.Tasks.CreateAsync(project.Id, "别人的");

        await host.Tasks.SetAssignmentAsync(mine.Id, assignee: "agent-a");

        var suggestions = await new DispatchService(host.Db, host.Clock)
            .SuggestAsync(project.Id, forAgentId: "agent-a");

        Assert.Equal(mine.Id, suggestions[0].TaskId);
        Assert.Contains(suggestions[0].Reasons, r => r.Contains("指派给你"));
        Assert.Equal("agent-a", suggestions[0].Assignee);
        Assert.Contains(suggestions, s => s.TaskId == others.Id);
    }

    [Fact]
    public async Task 每条建议都要有理由()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        await host.Tasks.CreateAsync(project.Id, "一");

        var suggestions = await new DispatchService(host.Db, host.Clock).SuggestAsync(project.Id);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, s => Assert.NotEmpty(s.Reasons));
    }

    [Fact]
    public async Task 可以排除某个agent手上的活()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a");

        var service = new DispatchService(host.Db, host.Clock);

        var all = await service.SuggestAsync(project.Id);
        Assert.Contains(all, s => s.TaskId == task.Id);

        var excluded = await service.SuggestAsync(project.Id, excludeAgentId: "agent-a");
        Assert.DoesNotContain(excluded, s => s.TaskId == task.Id);
    }
}
