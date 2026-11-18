using Guanfu.Domain;

namespace Guanfu.Tests;

public class MetricsServiceTests
{
    [Fact]
    public async Task 空项目不会算出NaN()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("空项目");

        var metrics = await host.Metrics.GetProjectMetricsAsync(project.Id);

        Assert.Equal(0, metrics.TotalTasks);
        Assert.Equal(0d, metrics.Progress);
        Assert.True(double.IsFinite(metrics.Progress));
    }

    [Fact]
    public async Task 状态分布只统计顶层任务()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var parent = await host.Tasks.CreateAsync(project.Id, "父任务");
        await host.Tasks.CreateAsync(project.Id, "子任务", null, parent.Id);

        var metrics = await host.Metrics.GetProjectMetricsAsync(project.Id);

        Assert.Equal(1, metrics.TotalTasks);
    }

    [Fact]
    public async Task 完成的任务计入流速与已完成数()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "任务一");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);

        var metrics = await host.Metrics.GetProjectMetricsAsync(project.Id);

        Assert.Equal(1, metrics.DoneTasks);
        Assert.Equal(8, metrics.Throughput.Count);
        Assert.Equal(1, metrics.Throughput[^1].Completed);
        Assert.NotNull(metrics.AvgCycleHours);
    }

    [Fact]
    public async Task 测试通过率取最近一次回写()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "任务一");

        await host.Tasks.ReportTestResultAsync(task.Id, 10, 10, null, 0.8d, "agent");
        var metrics = await host.Metrics.GetProjectMetricsAsync(project.Id);

        Assert.Equal(1d, metrics.TestPassRate);
        Assert.Equal(0.8d, metrics.AvgTestCoverage);
    }

    [Fact]
    public async Task 阻塞与停滞分别统计()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "任务一");

        // Draft 不能直接进 Blocked（草稿不算阻塞，要先有文档）
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Blocked);
        host.Clock.Advance(TimeSpan.FromHours(100));

        var metrics = await host.Metrics.GetProjectMetricsAsync(project.Id);

        Assert.Equal(1, metrics.BlockedTasks);
        Assert.Equal(1, metrics.StaleTasks);
        Assert.Equal(0, metrics.ActiveTasks);
    }

    [Fact]
    public async Task 待审数量按项目统计()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Approvals.RequestAsync("db_execute", "db.write", "{}", "写操作", "agent", null, project.Id);

        var metrics = await host.Metrics.GetProjectMetricsAsync(project.Id);

        Assert.Equal(1, metrics.OpenApprovals);
    }
}
