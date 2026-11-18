using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 多 agent 协作的交接点：一个活从谁转到谁 —— 多 agent 并行时这是协作的痕迹。
/// </summary>
public class AgentHandoffTests
{
    [Fact]
    public async Task 活从一个agent转手到另一个要留痕()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a");
        await host.Tasks.ClaimAsync(task.Id, "agent-b");

        var events = await host.Db.StatusEvents.AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.EventType == TaskEventTypes.Handoff)
            .ToListAsync();

        var handoff = Assert.Single(events);
        Assert.Equal("agent-b", handoff.Actor);
        Assert.Contains("agent-a", handoff.Note);
    }

    [Fact]
    public async Task 接手的人要看得见上一个是谁与为什么交()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "分支不对，接不了");

        var outcome = await host.Tasks.ClaimAsync(task.Id, "agent-b");

        Assert.Equal("agent-a", outcome.PreviousHolder);
        Assert.Equal("分支不对，接不了", outcome.HandoffReason);

        // 交接事件里也要有这几句（人从流水里查得到）
        var handoff = await host.Db.StatusEvents.AsNoTracking()
            .SingleAsync(e => e.TaskId == task.Id && e.EventType == TaskEventTypes.Handoff);

        Assert.Contains("agent-a", handoff.Note);
        Assert.Contains("分支不对", handoff.Note);
    }

    [Fact]
    public async Task 领活时留的上下文也是交接理由()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", note: "先看看这块的登录逻辑");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a");   // 放手时没写理由

        // 上一位接活时留的上下文，是他放手的背景
        var outcome = await host.Tasks.ClaimAsync(task.Id, "agent-b");
        Assert.Equal("agent-a", outcome.PreviousHolder);
        Assert.Equal("先看看这块的登录逻辑", outcome.HandoffReason);
    }

    [Fact]
    public async Task 放掉活之后别人就能接()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        // 不放掉的话，别人接不了（租约还在）
        await Assert.ThrowsAsync<GuanfuException>(
            async () => await host.Tasks.ClaimAsync(task.Id, "agent-b"));

        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "环境有问题");

        var outcome = await host.Tasks.ClaimAsync(task.Id, "agent-b");
        Assert.Equal("agent-b", outcome.Task.ClaimedBy);
    }

    [Fact]
    public async Task 同一个agent再领一次不算交接()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a");
        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        var handoffs = await host.Db.StatusEvents.AsNoTracking()
            .CountAsync(e => e.TaskId == task.Id && e.EventType == TaskEventTypes.Handoff);

        Assert.Equal(0, handoffs);
    }

}
