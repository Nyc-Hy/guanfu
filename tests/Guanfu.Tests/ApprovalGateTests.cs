using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 审批闸门。
///
/// 核心承诺：**一条审批只能执行一次，而且只能执行当初批的那件事**。
///
/// 当前数据库写入仍通过同一道审批闸门，旧工具的审批记录也必须与新工具隔离。
/// </summary>
public class ApprovalGateTests
{
    private const string Sql = "UPDATE dbo.Orders SET Status = 1";

    private static Task<ApprovalGateResult> AcquireAsync(
        GuanfuTestHost host, Guid projectId, Guid approvalId, string payload = Sql) =>
        host.Gate.AcquireAsync(
            "db_execute", "db.write", payload, "数据库写入", "tester", null,
            projectId, approvalId, waitSeconds: 1);

    private static async Task<Guid> ApproveAsync(
        GuanfuTestHost host, Guid projectId, string tool = "db_execute", string kind = "db.write")
    {
        var request = await host.Approvals.RequestAsync(tool, kind, Sql, "待审操作", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, approve: true, decidedBy: "tester");
        return request.Id;
    }

    [Fact]
    public async Task 批过的审批不能换SQL复用()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var approvalId = await ApproveAsync(host, project.Id);

        var result = await AcquireAsync(host, project.Id, approvalId, Sql.Replace("= 1", "= 2"));

        Assert.False(result.Approved);
        Assert.Contains("不一致", result.Message);
        Assert.Equal("Approved", (await host.Approvals.GetAsync(approvalId))!.Status);
    }

    [Fact]
    public async Task 旧工具审批不能拿去执行数据库写入()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var approvalId = await ApproveAsync(host, project.Id, "shell_run", "shell.write");

        var result = await AcquireAsync(host, project.Id, approvalId);

        Assert.False(result.Approved);
        Assert.Contains("shell_run", result.Message);
    }

    [Fact]
    public async Task 批过的审批不能跨项目复用()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var other = await host.Projects.CreateAsync("Q");
        var approvalId = await ApproveAsync(host, project.Id);

        var result = await AcquireAsync(host, other.Id, approvalId);

        Assert.False(result.Approved);
        Assert.Contains("别的项目", result.Message);
    }

    [Fact]
    public async Task 一条审批只能执行一次()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var approvalId = await ApproveAsync(host, project.Id);

        Assert.True((await AcquireAsync(host, project.Id, approvalId)).Approved);
        await host.Gate.CompleteAsync(approvalId, "已完成");

        Assert.False((await AcquireAsync(host, project.Id, approvalId)).Approved);
    }

    [Fact]
    public async Task 并发执行只有一次能过()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var approvalId = await ApproveAsync(host, project.Id);

        var results = await Task.WhenAll(
            AcquireAsync(host, project.Id, approvalId),
            AcquireAsync(host, project.Id, approvalId));

        Assert.Single(results.Where(r => r.Approved));
    }

    [Fact]
    public async Task 过期审批不能执行()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var approvalId = await ApproveAsync(host, project.Id);
        host.Clock.Advance(TimeSpan.FromHours(2));

        Assert.False((await AcquireAsync(host, project.Id, approvalId)).Approved);
    }

    [Fact]
    public async Task 过期状态会真的落库()
    {
        using var host = new GuanfuTestHost();

        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", Sql, "摘要", ttlMinutes: 1);

        host.Clock.Advance(TimeSpan.FromMinutes(2));

        var dto = await host.Approvals.GetAsync(approval.Id);
        Assert.Equal("Expired", dto!.Status);

        // 关键：库里也得是 Expired，不能永远停在 Pending
        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Expired, row.Status);
    }

    [Fact]
    public async Task 待审列表里不会留着过期单子()
    {
        using var host = new GuanfuTestHost();

        await host.Approvals.RequestAsync("db_execute", "db.write", Sql, "摘要", ttlMinutes: 1);
        host.Clock.Advance(TimeSpan.FromMinutes(2));

        var pending = await host.Approvals.ListAsync(ApprovalStatus.Pending);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task 两个人同时决定只有一个成功()
    {
        using var host = new GuanfuTestHost();

        var approval = await host.Approvals.RequestAsync(
            "db_execute", "db.write", Sql, "摘要");

        await Assert.ThrowsAsync<GuanfuException>(
            () => Task.WhenAll(
                host.Approvals.DecideAsync(approval.Id, true, "甲"),
                host.Approvals.DecideAsync(approval.Id, false, "乙", "不行")));

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.NotEqual(ApprovalStatus.Pending, row.Status);
    }

}
