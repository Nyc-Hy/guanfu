using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 评审工作流：七个维度、逐维度回写、门禁、多轮历史。
/// </summary>
public class ReviewServiceTests
{
    [Fact]
    public async Task 开评审会建七个维度的待办()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);

        var round = await host.Reviews.StartAsync(task.Id, "agent-1");

        Assert.Equal(1, round.Round);
        Assert.Equal(nameof(ReviewVerdict.Pending), round.Verdict);
        Assert.Equal(7, round.Items.Count);
        Assert.All(round.Items, i => Assert.Equal(nameof(ReviewStatus.Pending), i.Status));
        Assert.Contains(round.Items, i => i.Dimension == "code_quality");
        Assert.Contains(round.Items, i => i.Dimension == "over_operation");
    }

    [Fact]
    public async Task 七个维度都通过则汇总为通过()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await host.Reviews.StartAsync(task.Id);

        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, true, "没问题");
        }

        var current = await host.Reviews.GetCurrentAsync(task.Id);
        Assert.Equal(nameof(ReviewVerdict.Passed), current!.Verdict);
        Assert.NotNull(current.FinishedAt);
    }

    [Fact]
    public async Task 有一个维度不通过则汇总为不通过()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await host.Reviews.StartAsync(task.Id);

        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, key != "security", "检查过了");
        }

        var current = await host.Reviews.GetCurrentAsync(task.Id);
        Assert.Equal(nameof(ReviewVerdict.Failed), current!.Verdict);

        var gate = await host.Reviews.GetGateAsync(task.Id);
        Assert.True(gate.HasReview);
        Assert.Equal(new[] { "是否有安全问题" }, gate.FailedDimensions);
    }

    [Fact]
    public async Task 维度没评完汇总仍是待评审()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await host.Reviews.StartAsync(task.Id);
        await host.Reviews.SubmitAsync(task.Id, "code_quality", true);

        var current = await host.Reviews.GetCurrentAsync(task.Id);
        Assert.Equal(nameof(ReviewVerdict.Pending), current!.Verdict);
        Assert.Null(current.FinishedAt);

        var gate = await host.Reviews.GetGateAsync(task.Id);
        Assert.Equal(6, gate.PendingDimensions.Count);
    }

    [Fact]
    public async Task 评审不过不许结项()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, task.Id);
        await host.Reviews.StartAsync(task.Id);

        var ex = await Assert.ThrowsAsync<ReviewGateException>(
            () => host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done));

        Assert.Contains("待评审", ex.Message);

        var after = await host.Tasks.GetAsync(task.Id);
        Assert.Equal(TaskStatus.Review, after!.Status);
    }

    [Fact]
    public async Task 有维度不通过也不许结项()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, task.Id);
        await host.Reviews.StartAsync(task.Id);

        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, key != "comments", "检查过了");
        }

        var ex = await Assert.ThrowsAsync<ReviewGateException>(
            () => host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done));

        Assert.Contains("不通过", ex.Message);
    }

    [Fact]
    public async Task 全部维度通过后可以结项()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, task.Id);
        await host.Reviews.StartAsync(task.Id);

        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, true, "没问题");
        }

        var done = await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 没开过评审的老流程不受门禁影响()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, task.Id);

        var done = await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 多轮评审都留history()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);

        await host.Reviews.StartAsync(task.Id);
        await host.Reviews.SubmitAsync(task.Id, "code_quality", false, "写法有问题");

        await host.Reviews.StartAsync(task.Id);
        await host.Reviews.SubmitAsync(task.Id, "code_quality", true, "改好了");

        var rounds = await host.Reviews.ListAsync(task.Id);

        Assert.Equal(2, rounds.Count);
        Assert.Equal(1, rounds[0].Round);
        Assert.Equal(2, rounds[1].Round);
        Assert.Equal(nameof(ReviewStatus.Failed), rounds[0].Items[0].Status);
        Assert.Equal(nameof(ReviewStatus.Passed), rounds[1].Items[0].Status);
    }

    [Fact]
    public async Task 评审包带维度清单与任务上下文()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await host.Tasks.SetPlanAsync(task.Id, null, "要做登录接口", null, "能登录", null, "agent");
        await host.Reviews.StartAsync(task.Id);

        var pack = await host.Reviews.GetPackAsync(task.Id);

        Assert.Equal(7, pack.Dimensions.Count);
        Assert.All(pack.Dimensions, d => Assert.NotEmpty(d.Checklist));
        Assert.Equal("要做登录接口", pack.Plan!.Understanding);
        Assert.Equal(task.Id, pack.Task.Id);
    }

    [Fact]
    public async Task 未知维度报错并给出合法值()
    {
        using var host = new WorkbenchTestHost();
        var task = await CreateTaskAsync(host);
        await host.Reviews.StartAsync(task.Id);

        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Reviews.SubmitAsync(task.Id, "no_such_dimension", true));

        Assert.Contains("code_quality", ex.Message);
    }

    private static async Task<TaskSummaryDto> CreateTaskAsync(WorkbenchTestHost host)
    {
        var project = await host.Projects.CreateAsync("测试项目");
        return await host.Tasks.CreateAsync(project.Id, "写登录接口");
    }

    private static async Task MoveToReviewAsync(WorkbenchTestHost host, Guid taskId)
    {
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Review);
    }
}
