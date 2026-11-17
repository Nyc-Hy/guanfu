using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 调度视图：现在什么能干、什么卡着、谁在干。
///
/// agent-native PM 缺的就是这块 —— 以前 agent 得自己 list 一堆任务、
/// 自己猜哪个能开工、自己算「我是不是该等那个」。
/// </summary>
public class DispatchServiceTests
{
    [Fact]
    public async Task 没有依赖的活是能干活的()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "能干的活");

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        Assert.Equal(1, board.ReadyCount);
        Assert.Single(board.Ready);
        Assert.Equal(task.Id, board.Ready[0].Id);
        Assert.Equal(0, board.Ready[0].BlockDepth);
    }

    [Fact]
    public async Task 前置没做完的活是卡着的()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var blocker = await host.Tasks.CreateAsync(project.Id, "前置");
        var task = await host.Tasks.CreateAsync(project.Id, "被挡的");

        await host.Dependencies.AddAsync(task.Id, blocker.Id);

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        // 前置任务本身是能干的（它没有依赖）—— 能干的桶里应该有它
        Assert.Equal(1, board.ReadyCount);
        Assert.Equal(1, board.BlockedCount);
        Assert.Equal(task.Id, board.Blocked[0].Id);
        Assert.Equal(1, board.Blocked[0].PendingDependencies);
        Assert.Equal([blocker.Key], board.Blocked[0].BlockedBy);
        Assert.Equal(1, board.Blocked[0].BlockDepth);
    }

    [Fact]
    public async Task 前置做完了就解锁()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var blocker = await host.Tasks.CreateAsync(project.Id, "前置");
        var task = await host.Tasks.CreateAsync(project.Id, "被挡的");

        await host.Dependencies.AddAsync(task.Id, blocker.Id);

        // 前置走到「已完成」
        await host.Tasks.ChangeStatusAsync(blocker.Id, Domain.TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(blocker.Id, Domain.TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(blocker.Id, Domain.TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(blocker.Id, Domain.TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(blocker.Id, Domain.TaskStatus.Review);
        await host.Tasks.ChangeStatusAsync(blocker.Id, Domain.TaskStatus.Done);

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        Assert.Equal(1, board.ReadyCount);
        Assert.Equal(0, board.BlockedCount);
    }

    [Fact]
    public async Task 领着活的不算能干()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "被领的");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        Assert.Equal(0, board.ReadyCount);
        Assert.Equal(1, board.InProgressCount);
        Assert.Equal("agent-a", board.InProgress[0].ClaimedBy);
    }

    [Fact]
    public async Task 租约过期就又能干了()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "租约过的");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", leaseMinutes: 10);
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        Assert.Equal(1, board.ReadyCount);
        Assert.Equal(0, board.InProgressCount);
    }

    [Fact]
    public async Task 阻塞链的深度要算出来()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");

        var bottom = await host.Tasks.CreateAsync(project.Id, "最底层");
        var middle = await host.Tasks.CreateAsync(project.Id, "中间层");
        var top = await host.Tasks.CreateAsync(project.Id, "最上层");

        // bottom ← middle ← top
        await host.Dependencies.AddAsync(middle.Id, bottom.Id);
        await host.Dependencies.AddAsync(top.Id, middle.Id);

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        var topItem = board.Blocked.Single(t => t.Id == top.Id);
        var middleItem = board.Blocked.Single(t => t.Id == middle.Id);

        Assert.Equal(2, topItem.BlockDepth);
        Assert.Equal(1, middleItem.BlockDepth);

        // 等得越久的越靠前
        Assert.Equal(top.Id, board.Blocked[0].Id);
    }

    [Fact]
    public async Task 做完的和归档的不占位子()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var done = await host.Tasks.CreateAsync(project.Id, "做完了的");
        var archived = await host.Tasks.CreateAsync(project.Id, "归档的");

        await host.Tasks.ChangeStatusAsync(done.Id, Domain.TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(done.Id, Domain.TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(done.Id, Domain.TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(done.Id, Domain.TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(done.Id, Domain.TaskStatus.Review);
        await host.Tasks.ChangeStatusAsync(done.Id, Domain.TaskStatus.Done);
        await host.Tasks.SetArchivedAsync(archived.Id, true);

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        Assert.Equal(0, board.ReadyCount);
        Assert.Equal(0, board.BlockedCount);
    }

    [Fact]
    public async Task 工位要标出是不是主工作区()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "有工位的");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");

        var board = await new DispatchService(host.Db, host.Clock).GetAsync(project.Id);

        // 没绑仓库的项目 → 工位为空 → 算主工作区
        Assert.True(board.InProgress[0].IsMainCheckout);
    }
}
