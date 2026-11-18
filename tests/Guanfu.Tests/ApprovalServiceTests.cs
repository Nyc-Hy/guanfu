using Guanfu.Domain;

namespace Guanfu.Tests;

public class ApprovalServiceTests
{
    [Fact]
    public async Task 审批请求默认待审并带过期时间()
    {
        using var host = new GuanfuTestHost();

        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "\"UPDATE ...\"", "对 db 执行写操作", "agent-1", null, null, 30);

        Assert.Equal("Pending", approval.Status);
        Assert.Equal("agent-1", approval.RequestedBy);
        Assert.True(approval.ExpiresAt > approval.CreatedAt);
    }

    [Fact]
    public async Task 批准后状态变为Approved()
    {
        using var host = new GuanfuTestHost();
        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写操作", "agent-1");

        var decided = await host.Approvals.DecideAsync(approval.Id, true, "human", "可以");

        Assert.Equal("Approved", decided.Status);
        Assert.Equal("human", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
    }

    [Fact]
    public async Task 驳回必须填理由()
    {
        using var host = new GuanfuTestHost();
        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写操作", "agent-1");

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Approvals.DecideAsync(approval.Id, false, "human", null));

        var rejected = await host.Approvals.DecideAsync(approval.Id, false, "human", "这条不能跑");

        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal("这条不能跑", rejected.DecisionReason);
    }

    [Fact]
    public async Task 过期审批不能被批准()
    {
        using var host = new GuanfuTestHost();
        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写操作", "agent-1", null, null, 1);

        host.Clock.Advance(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Approvals.DecideAsync(approval.Id, true, "human"));

        var expired = await host.Approvals.GetAsync(approval.Id);
        Assert.Equal("Expired", expired!.Status);
    }

    [Fact]
    public async Task 已处理的审批不能二次决定()
    {
        using var host = new GuanfuTestHost();
        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写操作", "agent-1");

        await host.Approvals.DecideAsync(approval.Id, true, "human");

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Approvals.DecideAsync(approval.Id, false, "human", "反悔"));
    }

    [Fact]
    public async Task 等待审批会在决定后立即返回()
    {
        using var host = new GuanfuTestHost();
        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写操作", "agent-1");

        var waiter = host.Approvals.AwaitAsync(approval.Id, 10);

        await Task.Delay(200);
        await host.Approvals.DecideAsync(approval.Id, true, "human");

        var result = await waiter;

        Assert.Equal("Approved", result.Status);
    }

    [Fact]
    public async Task 等待超时返回仍在待审()
    {
        using var host = new GuanfuTestHost();
        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写操作", "agent-1");

        var result = await host.Approvals.AwaitAsync(approval.Id, 1);

        Assert.Equal("Pending", result.Status);
    }
}
