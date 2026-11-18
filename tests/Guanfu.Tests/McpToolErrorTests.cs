using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>
/// MCP 工具的错误表现。
///
/// 关键约束：**工具不能抛异常**。MCP SDK 会把异常吞成一句
/// "An error occurred invoking 'xxx'."，agent 看不到错在哪，只能瞎猜。
/// 所以工具要么返回结构化错误，要么由最外层的过滤器兜住。
/// </summary>
public class McpToolErrorTests
{
    private static GuanfuMcpTools Tasks(GuanfuTestHost host) =>
        new(host.Projects, host.Tasks, host.Approvals, host.Audit, host.Dependencies, host.Exports);

    private static ReviewMcpTools Reviews(GuanfuTestHost host) =>
        new(host.Reviews);

    [Fact]
    public async Task 按编号查任务查不到时返回错误而不是抛异常()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var result = await Tasks(host).GetTaskByKeyAsync(project.Id, "REQ-001");

        Assert.False(result.Ok);
        Assert.Contains("没有编号", result.Error);
        Assert.NotNull(result.AvailableKeys);
    }

    [Fact]
    public async Task 按编号查任务查不到时把现有编号带回去()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var result = await Tasks(host).GetTaskByKeyAsync(project.Id, "REQ-009");

        Assert.False(result.Ok);
        Assert.Equal(["REQ-001"], result.AvailableKeys);
    }

    [Fact]
    public async Task 按编号查任务认得人嘴里的写法()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var result = await Tasks(host).GetTaskByKeyAsync(project.Id, "req001");

        Assert.True(result.Ok);
        Assert.Single(result.Tasks!);
        Assert.Equal("REQ-001", result.Tasks![0].Key);
    }

    [Fact]
    public async Task 没开过评审就取评审包时返回错误而不是抛异常()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var result = await Reviews(host).GetReviewPackAsync(task.Id);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Null(result.Pack);
    }

    [Fact]
    public async Task 未知维度回写时返回错误而不是抛异常()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        await host.Reviews.StartAsync(task.Id);

        var result = await Reviews(host).SubmitReviewAsync(task.Id, "no_such_dimension", true);

        Assert.False(result.Ok);
        Assert.Contains("code_quality", result.Error);
    }

    [Fact]
    public async Task 提交评审成功后同时带回当前轮汇总()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        await host.Reviews.StartAsync(task.Id);

        var result = await Reviews(host).SubmitReviewAsync(task.Id, "code_quality", true, "没问题");

        Assert.True(result.Ok);
        Assert.Equal(nameof(Domain.ReviewStatus.Passed), result.Item!.Status);
        Assert.NotNull(result.Round);
        Assert.Equal(nameof(Domain.ReviewVerdict.Pending), result.Round!.Verdict);
    }

    [Fact]
    public async Task 列评审历史在没评审时返回空而不是抛异常()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var result = await Reviews(host).ListReviewsAsync(task.Id);

        Assert.True(result.Ok);
        Assert.Empty(result.Rounds!);
    }
}
