using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 并发写入口的回归测试：状态迁移、租约、计划版本、编号粗筛。
///
/// 这些地方的老实现都是「先读后写」，两个请求各自读到同一份旧值，
/// 后写的把先写的盖掉。现在改成条件更新（WHERE 旧值），影响行数 0 就是冲突。
///
/// 怎么在测试里**确定性**地造出竞争：让 A 的 DbContext 先把任务读进变更跟踪
/// （相当于 A 已经读完了旧值），再让 B 用另一个 context 把库里的值改掉，
/// 然后 A 才提交。EF 的 identity map 会把跟踪里的旧值原样交给 A ——
/// 这正是生产上「读完之后、写之前被人改了」的那一刻。
/// </summary>
public class TaskConcurrencyTests
{
    /// <summary>另开一个 context，共用同一条内存 SQLite 连接（= 同一个库）。</summary>
    private static WorkbenchDbContext NewContext(WorkbenchTestHost host)
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite(host.Db.Database.GetDbConnection())
            .Options;

        return new WorkbenchDbContext(options);
    }

    private static TaskService NewTaskService(WorkbenchTestHost host, IAppDbContext db) =>
        new(db, host.Clock, host.Git, host.Reviews, host.BranchGuard);

    private static async Task<Guid> CreateTaskAsync(WorkbenchTestHost host)
    {
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        return task.Id;
    }

    private static async Task MoveToReviewAsync(WorkbenchTestHost host, Guid taskId)
    {
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Review);
    }

    // ---- 1. 状态变更的条件更新 ----

    [Fact]
    public async Task 状态被并发改掉时后写的请求报冲突而不是覆盖()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, taskId);

        using var dbA = NewContext(host);
        using var dbB = NewContext(host);
        var serviceA = NewTaskService(host, dbA);
        var serviceB = NewTaskService(host, dbB);

        // A 读到 Review（读进跟踪，等于「读完的那一刻」）
        await dbA.Tasks.FirstAsync(t => t.Id == taskId);

        // B 抢先把任务打回 InDev
        var moved = await serviceB.ChangeStatusAsync(taskId, TaskStatus.InDev);
        Assert.Equal(TaskStatus.InDev, moved.Status);

        // A 再想 Review → Done：条件更新影响 0 行，必须报冲突
        var ex = await Assert.ThrowsAsync<StatusConflictException>(
            () => serviceA.ChangeStatusAsync(taskId, TaskStatus.Done));

        // 冲突信息要能让人直接决定下一步：当前状态 + 允许迁移列表
        Assert.Contains(nameof(TaskStatus.InDev), ex.Message);
        Assert.Contains(nameof(TaskStatus.Testing), ex.Message);

        // MCP 层既有的 catch(InvalidStatusTransitionException) 仍然接得住
        Assert.IsAssignableFrom<InvalidStatusTransitionException>(ex);

        // 库里的状态是 B 写的那个，没有被 A 盖掉
        var current = await host.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Equal(TaskStatus.InDev, current.Status);
    }

    [Fact]
    public async Task 冲突时不会留下半条流水()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, taskId);

        using var dbA = NewContext(host);
        using var dbB = NewContext(host);
        var serviceA = NewTaskService(host, dbA);

        await dbA.Tasks.FirstAsync(t => t.Id == taskId);
        await NewTaskService(host, dbB).ChangeStatusAsync(taskId, TaskStatus.InDev);

        var before = await host.Db.StatusEvents.CountAsync(e => e.TaskId == taskId);

        await Assert.ThrowsAsync<StatusConflictException>(
            () => serviceA.ChangeStatusAsync(taskId, TaskStatus.Done));

        // 迁移没生效就不该有对应流水（账本不能记没发生的事）
        Assert.Equal(before, await host.Db.StatusEvents.CountAsync(e => e.TaskId == taskId));
    }

    [Fact]
    public async Task 终态在同一条更新里清掉租约()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, taskId);
        await host.Tasks.ClaimAsync(taskId, "agent-1");

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);

        Assert.Equal(TaskStatus.Done, done.Status);
        Assert.Null(done.ClaimedBy);
        Assert.Null(done.LeaseExpiresAt);

        var row = await host.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Null(row.ClaimedByAgentId);
        Assert.Null(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task 结项后别的请求不能把旧租约写回来()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await MoveToReviewAsync(host, taskId);

        using var dbA = NewContext(host);
        var serviceA = NewTaskService(host, dbA);

        // A 读到「Review + 无租约」
        await dbA.Tasks.FirstAsync(t => t.Id == taskId);

        // B 认领后结项（终态清租约）
        await host.Tasks.ClaimAsync(taskId, "agent-2");
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);

        // A 的旧快照再提交：状态条件不满足，直接冲突，租约不会被复活
        await Assert.ThrowsAsync<StatusConflictException>(
            () => serviceA.ChangeStatusAsync(taskId, TaskStatus.Done));

        var row = await host.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Equal(TaskStatus.Done, row.Status);
        Assert.Null(row.ClaimedByAgentId);
    }

    // ---- 2. 释放租约的条件更新 ----

    [Fact]
    public async Task 不是持有者就不能释放租约()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await host.Tasks.ClaimAsync(taskId, "agent-1");

        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Tasks.ReleaseAsync(taskId, "agent-2"));

        Assert.Contains("agent-2", ex.Message);

        var row = await host.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Equal("agent-1", row.ClaimedByAgentId);
        Assert.NotNull(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task 租约过期被别人接管后原持有者释放不掉新租约()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await host.Tasks.ClaimAsync(taskId, "agent-1", leaseMinutes: 5);

        using var dbA = NewContext(host);
        var serviceA = NewTaskService(host, dbA);

        // A 读到「自己持有租约」
        await dbA.Tasks.FirstAsync(t => t.Id == taskId);

        // 租约过期，B 接管
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        await host.Tasks.ClaimAsync(taskId, "agent-2");

        // A 这时才来释放：WHERE 持有者 = agent-1 影响 0 行
        await Assert.ThrowsAsync<WorkbenchException>(
            () => serviceA.ReleaseAsync(taskId, "agent-1"));

        var row = await host.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Equal("agent-2", row.ClaimedByAgentId);
        Assert.NotNull(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task 持有者自己能正常释放()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);
        await host.Tasks.ClaimAsync(taskId, "agent-1");

        await host.Tasks.ReleaseAsync(taskId, "agent-1");

        var row = await host.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId);
        Assert.Null(row.ClaimedByAgentId);
        Assert.Null(row.LeaseExpiresAt);
    }

    [Fact]
    public async Task 释放不存在的任务报NotFound()
    {
        using var host = new WorkbenchTestHost();

        await Assert.ThrowsAsync<NotFoundException>(
            () => host.Tasks.ReleaseAsync(Guid.NewGuid(), "agent-1"));
    }

    // ---- 3. 计划版本重号 ----

    [Fact]
    public async Task 连续回写计划版本号递增()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);

        var v1 = await host.Tasks.SetPlanAsync(taskId, "背景", null, null, null, null, "agent-1");
        var v2 = await host.Tasks.SetPlanAsync(taskId, "背景2", null, null, null, null, "agent-1");
        var v3 = await host.Tasks.SetPlanAsync(taskId, "背景3", null, null, null, null, "agent-1");

        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
        Assert.Equal(3, v3.Version);
    }

    [Fact]
    public async Task 计划版本撞车时重试而不是把异常甩给调用方()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);

        using var dbA = NewContext(host);
        var serviceA = NewTaskService(host, dbA);

        // 造出「另一个请求已经插了 v1」的场面：同一个 context 里先挂一条 v1 未保存。
        // 服务算出 Max+1 = 1，两条一起提交 → (TaskId,Version) 唯一索引挡住 →
        // DbUpdateException → 清跟踪重算，第二次成功。
        dbA.Plans.Add(new Plan { TaskId = taskId, Version = 1, Background = "别人抢先写的" });

        var plan = await serviceA.SetPlanAsync(taskId, "我的计划", null, null, null, null, "agent-1");

        Assert.Equal(1, plan.Version);
        Assert.Equal("我的计划", plan.Background);

        // 撞车的那条不能留下来：库里只有一条 v1，而且是重试后写的那条
        var rows = await host.Db.Plans.AsNoTracking().Where(p => p.TaskId == taskId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("我的计划", rows[0].Background);
    }

    [Fact]
    public async Task 计划版本唯一索引真的存在()
    {
        using var host = new WorkbenchTestHost();
        var taskId = await CreateTaskAsync(host);

        host.Db.Plans.Add(new Plan { TaskId = taskId, Version = 1 });
        await host.Db.SaveChangesAsync();

        host.Db.Plans.Add(new Plan { TaskId = taskId, Version = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => host.Db.SaveChangesAsync());
    }

    // ---- 7. 编号粗筛不区分大小写 ----

    [Fact]
    public async Task 混合大小写的编号也算重号()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "第一个", key: "rEq-001");

        // 老实现的粗筛只比 prefix/lower/upper 三种写法，"rEq-001" 三种都不命中，
        // 于是 "REQ-001" 被当成新编号放进了库。
        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Tasks.CreateAsync(project.Id, "第二个", key: "REQ-001"));

        Assert.Contains("已经有了", ex.Message);
    }

    [Fact]
    public async Task 少了连字符的编号也算重号()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "第一个", key: "REQ-001");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Tasks.CreateAsync(project.Id, "第二个", key: "req001"));
    }

    [Fact]
    public async Task 不同前缀的编号互不影响()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "需求", key: "REQ-001");

        var bug = await host.Tasks.CreateAsync(project.Id, "缺陷", type: TaskType.Bug);

        Assert.Equal("BUG-001", bug.Key);
    }

    // ---- 8. PendingDependencies 不再是恒 0 的摆设 ----

    [Fact]
    public async Task 未完成前置数在摘要与上下文里都算得出来()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var prerequisite = await host.Tasks.CreateAsync(project.Id, "前置");
        var task = await host.Tasks.CreateAsync(project.Id, "后续");
        await host.Dependencies.AddAsync(task.Id, prerequisite.Id);

        Assert.Equal(1, (await host.Tasks.GetAsync(task.Id))!.PendingDependencies);
        Assert.Equal(1, (await host.Tasks.GetContextAsync(task.Id)).Task.PendingDependencies);

        var list = await host.Tasks.ListAsync(project.Id);
        Assert.Equal(1, list.First(t => t.Id == task.Id).PendingDependencies);

        // 前置做完 → 不再欠着
        await host.Tasks.ChangeStatusAsync(prerequisite.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(prerequisite.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(prerequisite.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(prerequisite.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(prerequisite.Id, TaskStatus.Review);
        await host.Tasks.ChangeStatusAsync(prerequisite.Id, TaskStatus.Done);

        Assert.Equal(0, (await host.Tasks.GetAsync(task.Id))!.PendingDependencies);
    }
}