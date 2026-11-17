using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Mcp;

namespace Workbench.Tests;

/// <summary>
/// 评审的多 agent 分工。
///
/// 一轮七个维度，一个 agent 串行评下来很慢 —— 开子 agent 并行评才是常态。
/// 但没有认领，两个 agent 会评同一个维度，后写的把先写的结论盖掉，
/// 先写的还以为自己评过了（而且白评）。
///
/// 所以维度与完成定义都能**认领**（带租约）：
/// 认了就是「这个我在做，别重复」；租约到期还没交结论，别人可以接手。
/// </summary>
public sealed class ReviewClaimTests
{
    private static async Task<(WorkbenchTestHost Host, TaskSummaryDto Task, ReviewRoundDto Round)> SetupAsync()
    {
        var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "被评审的活");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);

        var round = await host.Reviews.StartAsync(task.Id, "e2e-agent");
        return (host, task, round);
    }

    [Fact]
    public async Task 认领维度后能看到是谁在评()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        var item = await host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-a");

        Assert.Equal("agent-a", item.ClaimedBy);
        Assert.NotNull(item.ClaimedUntil);

        // 评审包里也要看得见 —— 别的 agent 才知道该换哪个维度
        var pack = await host.Reviews.GetPackAsync(task.Id);
        var claimed = pack.Round.Items.Single(i => i.Dimension == "code_quality");
        Assert.Equal("agent-a", claimed.ClaimedBy);
    }

    [Fact]
    public async Task 别人认领的维度不能再认领()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        await host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-a");

        var ex = await Assert.ThrowsAsync<WorkbenchException>(() =>
            host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-b"));

        Assert.Contains("agent-a", ex.Message);
    }

    [Fact]
    public async Task 别人认领的维度不能替他交结论()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        await host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-a");

        var ex = await Assert.ThrowsAsync<WorkbenchException>(() =>
            host.Reviews.SubmitAsync(task.Id, "code_quality", true, reviewer: "agent-b"));

        Assert.Contains("agent-a", ex.Message);
    }

    [Fact]
    public async Task 自己认领的维度能自己交结论()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        await host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-a");

        var item = await host.Reviews.SubmitAsync(task.Id, "code_quality", true, reviewer: "agent-a");

        Assert.Equal("Passed", item.Status);
        // 交完就收工 —— 维度不该看起来还被人占着
        Assert.Null(item.ClaimedBy);
    }

    [Fact]
    public async Task 租约到期后别人能接手()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        await host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-a", leaseMinutes: 30);

        // agent-a 掉线了：租约过期
        host.Clock.Advance(TimeSpan.FromMinutes(31));

        var item = await host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-b");

        Assert.Equal("agent-b", item.ClaimedBy);
    }

    [Fact]
    public async Task 已经有结论的维度不用再认领()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        await host.Reviews.SubmitAsync(task.Id, "code_quality", true, reviewer: "agent-a");

        var ex = await Assert.ThrowsAsync<WorkbenchException>(() =>
            host.Reviews.ClaimDimensionAsync(task.Id, "code_quality", "agent-b"));

        Assert.Contains("已经有结论", ex.Message);
    }

    [Fact]
    public async Task 完成定义也能认领()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        await host.Specs.UpdateAsync(
            (await host.Projects.ListAsync()).First().Id,
            new ProjectSpecInput(DefinitionOfDone: ["有单测覆盖新增逻辑"]));

        var verdict = await host.Reviews.ClaimSpecItemAsync(task.Id, "有单测覆盖新增逻辑", "agent-a");

        Assert.Equal("agent-a", verdict.ClaimedBy);

        // 别的 agent 不能替他判
        var ex = await Assert.ThrowsAsync<WorkbenchException>(() =>
            host.Reviews.SubmitSpecVerdictAsync(task.Id, "有单测覆盖新增逻辑", true, reviewer: "agent-b"));

        Assert.Contains("agent-a", ex.Message);

        // 认领信息要在待判定清单里看得见
        var items = await host.Reviews.GetSpecItemsAsync(task.Id);
        Assert.Equal("agent-a", items.Single(i => i.Text == "有单测覆盖新增逻辑").ClaimedBy);
    }

    [Fact]
    public async Task MCP工具能认领维度()
    {
        var (host, task, _) = await SetupAsync();
        using var _ = host;

        var tools = new ReviewMcpTools(host.Reviews);

        var result = await tools.ClaimReviewDimensionAsync(task.Id, "security", "agent-a");

        Assert.True(result.Ok);
        Assert.Equal("agent-a", result.Item!.ClaimedBy);

        var again = await tools.ClaimReviewDimensionAsync(task.Id, "security", "agent-b");
        Assert.False(again.Ok);
        Assert.Contains("agent-a", again.Error!);
    }
}
