using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 用量。只记**观复数得出来的**：工具调用次数、工具种类、事件条数、租约时长。
/// token 与费用刻意不记 —— 观复不调模型，不知道用的哪个模型什么套餐，
/// 自报的数字在审计上站不住。
/// </summary>
public class TaskUsageTests
{
    [Fact]
    public async Task 工具调用次数是观复自己数的()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        // 审计里记三条这个任务的工具调用
        for (var i = 0; i < 3; i++)
        {
            await host.Audit.RecordAsync(new ToolAuditEntry(
                "fs_read", "agent", project.Id, task.Id, "a.txt",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success", ResultSummary: null, RowsAffected: null, DurationMs: 1));
        }

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent", project.Id, null, ".",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: null, RowsAffected: null, DurationMs: 1));

        var usage = await new TaskUsageService(host.Db, host.Clock).GetTaskUsageAsync(task.Id);

        Assert.Equal(3, usage.ToolCalls);
        Assert.Equal(1, usage.UniqueTools);
    }

    [Fact]
    public async Task 项目用量按任务和按agent汇总()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var first = await host.Tasks.CreateAsync(project.Id, "一");
        var second = await host.Tasks.CreateAsync(project.Id, "二");

        await host.Tasks.ClaimAsync(first.Id, "agent-a");
        await host.Tasks.ClaimAsync(second.Id, "agent-b");

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_read", "agent-a", project.Id, first.Id, "a.txt",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: null, RowsAffected: null, DurationMs: 1));

        var board = await new TaskUsageService(host.Db, host.Clock).GetProjectUsageAsync(project.Id);

        Assert.Equal(2, board.TotalTasks);
        Assert.Equal(1, board.TotalToolCalls);
        Assert.Equal(2, board.Agents.Count);
        Assert.Contains(board.Agents, a => a.AgentId == "agent-a" && a.ToolCalls == 1);
    }

    [Fact]
    public async Task 没设上限就不算超()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        var budget = await new TaskUsageService(host.Db, host.Clock)
            .GetCallBudgetAsync(project.Id, task.Id);

        Assert.False(budget.Over);
        Assert.Equal(0, budget.Budget);
    }

    [Fact]
    public async Task 超过每任务调用上限要拦得住()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        await host.Projects.UpdateConfigAsync(project.Id, maxToolCallsPerTask: 2);
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        var service = new TaskUsageService(host.Db, host.Clock);

        for (var i = 0; i < 2; i++)
        {
            await host.Audit.RecordAsync(new ToolAuditEntry(
                "fs_read", "agent", project.Id, task.Id, "a.txt",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success", ResultSummary: null, RowsAffected: null, DurationMs: 1));
        }

        var budget = await service.GetCallBudgetAsync(project.Id, task.Id);

        Assert.True(budget.Over);
        Assert.Equal(2, budget.Used);
        Assert.Equal(2, budget.Budget);
    }
}
