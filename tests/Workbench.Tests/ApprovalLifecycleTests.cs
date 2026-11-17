using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 审批的生命周期：去重、撤销、执行中断回收，以及「理由写对字段」。
///
/// 审批是写操作的唯一闸门，它的状态错了就等于闸门错了：
/// 批错的撤不掉、执行方崩了永远卡在 Executing、同一个操作刷屏待审列表，
/// 都会让人不再相信这个列表。
/// </summary>
public class ApprovalLifecycleTests
{
    private const string Tool = "db_execute";
    private const string Kind = "write";
    private const string Payload = "{\"sql\":\"UPDATE Orders SET Qty=1\"}";

    private static async Task<(WorkbenchTestHost Host, Guid ProjectId)> SetupAsync()
    {
        var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        return (host, project.Id);
    }

    // ---- 9. 驳回理由写进 DecisionReason ----

    [Fact]
    public async Task 驳回理由落在决策理由字段而不是结果摘要()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var decided = await host.Approvals.DecideAsync(request.Id, false, "张三", "范围太大，先拆小");

        Assert.Equal(nameof(ApprovalStatus.Rejected), decided.Status);
        Assert.Equal("范围太大，先拆小", decided.DecisionReason);
        Assert.Equal("张三", decided.DecidedBy);
        // 老实现把理由塞进了 resultSummary（SyncTracked 的第三个形参），
        // 于是「为什么驳回」在界面上永远空着，而「执行结果」里躺着一句驳回理由。
        Assert.Null(decided.ResultSummary);

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal("范围太大，先拆小", row.DecisionReason);
        Assert.Null(row.ResultSummary);
    }

    [Fact]
    public async Task 批准时决定的理由与时间也写对字段()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var decided = await host.Approvals.DecideAsync(request.Id, true, "李四", "看过了，可以");

        Assert.Equal(nameof(ApprovalStatus.Approved), decided.Status);
        Assert.Equal("看过了，可以", decided.DecisionReason);
        Assert.Equal("李四", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
        Assert.Null(decided.ResultSummary);
    }

    [Fact]
    public async Task 执行结果摘要仍然写在结果字段()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "李四");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        var executed = await host.Approvals.MarkExecutedAsync(request.Id, "影响 3 行");

        Assert.Equal(nameof(ApprovalStatus.Executed), executed.Status);
        Assert.Equal("影响 3 行", executed.ResultSummary);
    }

    // ---- 10. 待审去重 ----

    [Fact]
    public async Task 同一个操作重复申请返回既有待审单()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var first = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var second = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单（重试）", projectId: projectId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await host.Db.Approvals.CountAsync());
    }

    [Fact]
    public async Task 参数不同的操作不去重()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var first = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var other = await host.Approvals.RequestAsync(
            Tool, Kind, "{\"sql\":\"DELETE FROM Orders\"}", "删订单", projectId: projectId);

        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public async Task 别的项目的同名操作不去重()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;
        var otherProject = await host.Projects.CreateAsync("另一个项目");

        var first = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var other = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: otherProject.Id);

        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public async Task 已驳回的单子不被复用()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var first = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(first.Id, false, "张三", "不行");

        var again = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);

        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(nameof(ApprovalStatus.Pending), again.Status);
    }

    [Fact]
    public async Task 已过期的单子不被复用()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var first = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId, ttlMinutes: 5);
        host.Clock.Advance(TimeSpan.FromMinutes(10));

        var again = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);

        Assert.NotEqual(first.Id, again.Id);
    }

    // ---- 11. 人工撤销 ----

    [Fact]
    public async Task 待审的单子可以撤销()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var revoked = await host.Approvals.RevokeAsync(request.Id, "点错了");

        Assert.Equal(nameof(ApprovalStatus.Rejected), revoked.Status);
        Assert.Contains("人工撤销", revoked.DecisionReason);
        Assert.Contains("点错了", revoked.DecisionReason);
        Assert.Equal("human", revoked.DecidedBy);
        Assert.NotNull(revoked.DecidedAt);
    }

    [Fact]
    public async Task 撤销不给理由时也留痕()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        var revoked = await host.Approvals.RevokeAsync(request.Id);

        Assert.Equal("人工撤销", revoked.DecisionReason);
    }

    [Fact]
    public async Task 批了但还没执行的单子可以撤销且再也用不掉()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");

        var revoked = await host.Approvals.RevokeAsync(request.Id, "批错了");
        Assert.Equal(nameof(ApprovalStatus.Rejected), revoked.Status);

        var consume = await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);
        Assert.False(consume.Ok);
    }

    [Fact]
    public async Task 正在执行的单子撤不回来()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        var consume = await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);
        Assert.True(consume.Ok);

        var ex = await Assert.ThrowsAsync<WorkbenchException>(() => host.Approvals.RevokeAsync(request.Id, "反悔"));
        Assert.Contains(nameof(ApprovalStatus.Executing), ex.Message);

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Executing, row.Status);
    }

    [Fact]
    public async Task 已执行的单子撤不回来()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);
        await host.Approvals.MarkExecutedAsync(request.Id, "影响 3 行");

        await Assert.ThrowsAsync<WorkbenchException>(() => host.Approvals.RevokeAsync(request.Id));

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Executed, row.Status);
        // 撤销失败不能把执行结果抹掉
        Assert.Equal("影响 3 行", row.ResultSummary);
    }

    [Fact]
    public async Task 已经驳回或过期的单子不需要撤销()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var rejected = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(rejected.Id, false, "张三", "不行");
        await Assert.ThrowsAsync<WorkbenchException>(() => host.Approvals.RevokeAsync(rejected.Id));

        var expired = await host.Approvals.RequestAsync(
            Tool, Kind, "{\"sql\":\"UPDATE Other\"}", "改别的", projectId: projectId, ttlMinutes: 1);
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.Approvals.ExpireOverdueAsync();
        await Assert.ThrowsAsync<WorkbenchException>(() => host.Approvals.RevokeAsync(expired.Id));
    }

    [Fact]
    public async Task 撤销不存在的审批报NotFound()
    {
        var (host, _) = await SetupAsync();
        using var scope = host;

        await Assert.ThrowsAsync<NotFoundException>(() => host.Approvals.RevokeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task 撤销之后重新申请是一条新单()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var first = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.RevokeAsync(first.Id, "点错了");

        var again = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);

        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(nameof(ApprovalStatus.Pending), again.Status);
    }

    // ---- 12. Executing 中断回收 ----

    [Fact]
    public async Task 执行方崩了的审批超时后被回收()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        host.Clock.Advance(TimeSpan.FromMinutes(ApprovalService.ExecutingTimeoutMinutes + 1));
        var expired = await host.Approvals.ExpireOverdueAsync();

        Assert.Equal(1, expired);

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Expired, row.Status);
        Assert.Contains("执行中断", row.DecisionReason);

        // 执行方没收尾 → 这次写操作可能已经生效了，必须标「结果未知」交人工确认
        Assert.True(row.OutcomeUnknown);
        Assert.Equal("system", row.DecidedBy);
    }

    [Fact]
    public async Task 还在执行时限内的审批不被回收()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        host.Clock.Advance(TimeSpan.FromMinutes(ApprovalService.ExecutingTimeoutMinutes - 1));
        Assert.Equal(0, await host.Approvals.ExpireOverdueAsync());

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Executing, row.Status);
    }

    [Fact]
    public async Task 老数据没有执行开始时刻时退回决定时刻判定()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        // 升级前的库里没有 ExecutingStartedAt 这一列的值
        await host.Db.Approvals
            .Where(a => a.Id == request.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecutingStartedAt, (DateTimeOffset?)null));

        host.Clock.Advance(TimeSpan.FromMinutes(ApprovalService.ExecutingTimeoutMinutes + 1));
        Assert.Equal(1, await host.Approvals.ExpireOverdueAsync());

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Expired, row.Status);
    }

    [Fact]
    public async Task 待审超时与执行中断一起回收并返回总数()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var stale = await host.Approvals.RequestAsync(
            Tool, Kind, "{\"sql\":\"UPDATE A\"}", "改A", projectId: projectId, ttlMinutes: 5);

        var interrupted = await host.Approvals.RequestAsync(
            Tool, Kind, "{\"sql\":\"UPDATE B\"}", "改B", projectId: projectId, ttlMinutes: 60);
        await host.Approvals.DecideAsync(interrupted.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(interrupted.Id, Tool, Kind, "{\"sql\":\"UPDATE B\"}", projectId);

        host.Clock.Advance(TimeSpan.FromMinutes(ApprovalService.ExecutingTimeoutMinutes + 1));
        Assert.Equal(2, await host.Approvals.ExpireOverdueAsync());

        Assert.Equal(nameof(ApprovalStatus.Expired), (await host.Approvals.GetAsync(stale.Id))!.Status);
        Assert.Equal(nameof(ApprovalStatus.Expired), (await host.Approvals.GetAsync(interrupted.Id))!.Status);
    }

    [Fact]
    public async Task 被回收的执行单不能再被消费()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        host.Clock.Advance(TimeSpan.FromMinutes(ApprovalService.ExecutingTimeoutMinutes + 1));
        await host.Approvals.ExpireOverdueAsync();

        var consume = await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);
        Assert.False(consume.Ok);
        Assert.Contains(nameof(ApprovalStatus.Expired), consume.Status);
    }

    // ---- 13. 重启对账：结果未知 ----

    [Fact]
    public async Task 重启时上个进程遗留的执行被标成结果未知()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        // 十分钟后进程崩了，重启
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        var marked = await host.Approvals.ReconcileInterruptedAsync(host.Clock.UtcNow);

        Assert.Equal(1, marked);

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Expired, row.Status);
        Assert.True(row.OutcomeUnknown);
        Assert.Equal("system", row.DecidedBy);
        // 理由要说清「可能已经生效」—— 跟执行失败不是一回事
        Assert.Contains("结果未知", row.DecisionReason);
        Assert.Contains("可能已经生效", row.DecisionReason);
    }

    [Fact]
    public async Task 重启后本进程正在跑的审批不被对账收走()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        // 进程启动时刻
        var processStartedAt = host.Clock.UtcNow;

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        Assert.Equal(0, await host.Approvals.ReconcileInterruptedAsync(processStartedAt));

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Executing, row.Status);
        Assert.False(row.OutcomeUnknown);
    }

    [Fact]
    public async Task 待审的审批不因重启被标成结果未知()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);

        host.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await host.Approvals.ReconcileInterruptedAsync(host.Clock.UtcNow));

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == request.Id);
        Assert.Equal(ApprovalStatus.Pending, row.Status);
        Assert.False(row.OutcomeUnknown);
    }

    [Fact]
    public async Task 结果未知的审批在列表里带得出来()
    {
        var (host, projectId) = await SetupAsync();
        using var scope = host;

        var request = await host.Approvals.RequestAsync(Tool, Kind, Payload, "改订单", projectId: projectId);
        await host.Approvals.DecideAsync(request.Id, true, "张三");
        await host.Approvals.TryConsumeAsync(request.Id, Tool, Kind, Payload, projectId);

        host.Clock.Advance(TimeSpan.FromMinutes(10));
        await host.Approvals.ReconcileInterruptedAsync(host.Clock.UtcNow);

        var list = await host.Approvals.ListAsync();
        var row = Assert.Single(list.Where(a => a.Id == request.Id));
        Assert.True(row.OutcomeUnknown);
    }
}