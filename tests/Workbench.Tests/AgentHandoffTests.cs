using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 多 agent 协作的两件事：
/// - **交接**：一个活从谁转到谁 —— 多 agent 并行时这是协作的痕迹；
/// - **开销增量**：agent 按次上报，工作台累加成总额并逐次留痕。
/// </summary>
public class AgentHandoffTests
{
    [Fact]
    public async Task 活从一个agent转手到另一个要留痕()
    {
        using var host = new WorkbenchTestHost();
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
    public async Task 同一个agent再领一次不算交接()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a");
        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        var handoffs = await host.Db.StatusEvents.AsNoTracking()
            .CountAsync(e => e.TaskId == task.Id && e.EventType == TaskEventTypes.Handoff);

        Assert.Equal(0, handoffs);
    }

    [Fact]
    public async Task 开销按次累加并逐次留痕()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        var service = new TaskUsageService(host.Db, host.Clock);

        await service.ReportCostAsync(task.Id, 1_000, 1.0m, "CNY", "agent-a");
        await service.ReportCostAsync(task.Id, 2_500, 2.5m, "CNY", "agent-a");

        var usage = await service.GetTaskUsageAsync(task.Id);
        Assert.Equal(3_500, usage.ReportedTokens);
        Assert.Equal(3.5m, usage.ReportedCost);

        // 逐次留痕（增量 + 总额都在事件里）
        var events = await host.Db.StatusEvents.AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.EventType == TaskEventTypes.CostReported)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Contains("2500", events[1].Payload);
        Assert.Contains("3500", events[1].Payload);
    }
}
