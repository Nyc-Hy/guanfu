using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>
/// agent 侧的离线补报。
///
/// 场景：agent 在观复够不着的时候把活干完了（网络不通、观复在维护、
/// 或者活是上一轮会话干的），等能连上了才把结果报上来。
///
/// 这时它报的是**实际发生时间**，不是「现在」。
/// 账本上两个时间都得有：
/// - 发生时间（OccurredAt）—— 事实在什么时候发生的；
/// - 记录时间（CreatedAt）—— 观复什么时候知道的。
///
/// 只记一个就是在骗人：流水看着像那会儿干的，其实不是。
/// </summary>
public sealed class OfflineBackfillTests
{
    private static async Task<(GuanfuTestHost Host, TaskSummaryDto Task)> NewTaskAsync()
    {
        var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "补报的任务");
        return (host, task);
    }

    [Fact]
    public async Task 补报时事件带上实际发生时间()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var happenedAt = host.Clock.UtcNow.AddHours(-3);
        var recordedAt = host.Clock.UtcNow;

        var plan = await host.Tasks.SetPlanAsync(
            task.Id, "背景", "理解", "拆解", "验收", null, "agent-1", happenedAt);

        // 计划本身记的是「什么时候想明白的」
        Assert.Equal(happenedAt.ToUniversalTime(), plan.CreatedAt.ToUniversalTime(), TimeSpan.FromSeconds(1));

        var events = await host.Tasks.ListEventsAsync(task.Id);
        var planEvent = events.Single(e => e.EventType == TaskEventTypes.PlanSaved);

        // 发生时间与记录时间都在，且不相同
        Assert.NotNull(planEvent.OccurredAt);
        Assert.Equal(happenedAt.ToUniversalTime(), planEvent.OccurredAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(recordedAt.ToUniversalTime(), planEvent.CreatedAt.ToUniversalTime(), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task 不补报时不挂发生时间()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        await host.Tasks.SetPlanAsync(task.Id, "背景", "理解", "拆解", "验收", null, "agent-1");

        var events = await host.Tasks.ListEventsAsync(task.Id);
        var planEvent = events.Single(e => e.EventType == TaskEventTypes.PlanSaved);

        // 没补报就别到处挂「补报」标签
        Assert.Null(planEvent.OccurredAt);
    }

    [Fact]
    public async Task 未来的时间被拒()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var future = host.Clock.UtcNow.AddHours(1);

        var ex = await Assert.ThrowsAsync<GuanfuException>(() =>
            host.Tasks.SetPlanAsync(task.Id, "背景", null, null, null, null, "agent-1", future));

        Assert.Contains("不能在未来", ex.Message);
    }

    [Fact]
    public async Task 离谱的过去时间被拒()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var tooOld = host.Clock.UtcNow.AddYears(-3);

        var ex = await Assert.ThrowsAsync<GuanfuException>(() =>
            host.Tasks.SetPlanAsync(task.Id, "背景", null, null, null, null, "agent-1", tooOld));

        Assert.Contains("太早", ex.Message);
    }

    [Fact]
    public async Task 补报的产物与测试结果也带实际发生时间()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var happenedAt = host.Clock.UtcNow.AddHours(-2);

        var artifact = await host.Tasks.AddArtifactAsync(
            task.Id, ArtifactType.CodeChange, "离线写的补丁", "diff --git", null, null, null, "agent-1", happenedAt);

        Assert.Equal(happenedAt.ToUniversalTime(), artifact.CreatedAt.ToUniversalTime(), TimeSpan.FromSeconds(1));

        var test = await host.Tasks.ReportTestResultAsync(
            task.Id, 10, 9, "用例A失败", 0.9d, "agent-1", happenedAt);

        Assert.Equal(happenedAt.ToUniversalTime(), test.ReportedAt.ToUniversalTime(), TimeSpan.FromSeconds(1));

        var events = await host.Tasks.ListEventsAsync(task.Id);
        Assert.All(
            events.Where(e => e.OccurredAt is not null),
            e => Assert.Equal(
                happenedAt.ToUniversalTime(),
                e.OccurredAt!.Value.ToUniversalTime(),
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 补报的流水事件带发生时间()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var happenedAt = host.Clock.UtcNow.AddMinutes(-30);

        var evt = await host.Tasks.PostEventAsync(
            task.Id, "离线时把环境装好了", TaskEventSource.Agent, "agent-1", happenedAt);

        Assert.NotNull(evt.OccurredAt);
        Assert.Equal(happenedAt.ToUniversalTime(), evt.OccurredAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task 补报的时间要进事件载荷()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var happenedAt = host.Clock.UtcNow.AddHours(-1);
        await host.Tasks.PostEventAsync(task.Id, "离线补记", TaskEventSource.Agent, "agent-1", happenedAt);

        var events = await host.Tasks.ListEventsAsync(task.Id);
        var evt = events.First(e => e.Note == "离线补记");

        // 机器可读：消费方按字段取，不用去猜自由文本
        Assert.Contains("occurredAt", evt.Payload!);
    }

    [Fact]
    public async Task MCP工具能带实际发生时间()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var tools = new GuanfuMcpTools(
            host.Projects, host.Tasks, host.Approvals, host.Audit, host.Dependencies, host.Exports);

        var happenedAt = host.Clock.UtcNow.AddHours(-4);
        var plan = await tools.SetPlanAsync(task.Id, "背景", "理解", null, null, null, "agent-1", happenedAt);

        Assert.Equal(happenedAt.ToUniversalTime(), plan.CreatedAt.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task 补报不改任务的活动时间线()
    {
        var (host, task) = await NewTaskAsync();
        using var _ = host;

        var happenedAt = host.Clock.UtcNow.AddHours(-5);
        await host.Tasks.SetPlanAsync(task.Id, "背景", null, null, null, null, "agent-1", happenedAt);

        var reloaded = await host.Tasks.GetAsync(task.Id);

        // 「观复什么时候知道」还是现在 —— 补报的是过去，不是把活跃时间往前挪
        Assert.NotNull(reloaded);
        Assert.True(
            reloaded!.LastActivityAt is null ||
            reloaded.LastActivityAt.Value.ToUniversalTime() > happenedAt.ToUniversalTime(),
            "活跃时间不该被补报的时间盖掉。");
    }
}
