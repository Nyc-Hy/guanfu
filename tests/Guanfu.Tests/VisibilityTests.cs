using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 个人数据隔离。
///
/// 团队部署时「每个人只能看到自己的东西」——
/// 这条规则落在**服务层**（REST、MCP、界面三条路一样严）。
///
/// 这里钉三件事：
/// - 普通成员只看得到**自己的**活，外加**还没分派的**（不然没人能认领）；
/// - 管理员看全部；
/// - 审批只能个人批：普通成员批别人的单子要被拒；
///   管理员能代批，但要写理由、且账上留「谁替谁批」。
/// </summary>
public sealed class VisibilityTests
{
    /// <summary>可编程的「当前身份」。</summary>
    private sealed class FakeScope(MemberScope? scope) : ICurrentMemberScope
    {
        public MemberScope? Current { get; set; } = scope;
    }

    private static MemberScope Member(Guid id, MemberRole role = MemberRole.Member) =>
        new(id, "u" + id.ToString("N")[..4], "成员" + id.ToString("N")[..4], role);

    [Fact]
    public async Task 普通成员只看得到自己的活()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("测试项目");
        var mine = await host.Tasks.CreateAsync(project.Id, "我的活");
        var theirs = await host.Tasks.CreateAsync(project.Id, "别人的活");
        var open = await host.Tasks.CreateAsync(project.Id, "还没分派的活");

        // 布置：分派是管理员的动作，用没有身份的口径做（单机模式）。
        var setup = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, new FakeScope(null));

        await setup.SetAssignmentAsync(mine.Id, "爱丽丝", assigneeMemberId: alice.Id);
        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: bob.Id);

        // 换成爱丽丝的身份再看一遍
        var scope = new FakeScope(Member(alice.Id));

        var tasks = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, scope);

        var visible = await tasks.ListAsync(projectId: project.Id);

        var keys = visible.Select(t => t.Title).ToList();

        Assert.Contains("我的活", keys);
        Assert.Contains("还没分派的活", keys);
        Assert.DoesNotContain("别人的活", keys);
    }

    [Fact]
    public async Task 管理员看得到全部()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var boss = await members.CreateAsync("boss", "老板", "password-1", MemberRole.Admin);
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("测试项目");
        var theirs = await host.Tasks.CreateAsync(project.Id, "别人的活");

        var setup = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, new FakeScope(null));

        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: bob.Id);

        var scope = new FakeScope(Member(boss.Id, MemberRole.Admin));

        var tasks = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, scope);

        var visible = await tasks.ListAsync(projectId: project.Id);

        Assert.Contains(visible, t => t.Title == "别人的活");
    }

    [Fact]
    public async Task 没人登录时什么都看得见_单人本机模式()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "某个活");

        var tasks = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, new FakeScope(null));

        var visible = await tasks.ListAsync(projectId: project.Id);

        Assert.Single(visible);
    }

    [Fact]
    public async Task 普通成员不能把活分给别人()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "某个活");

        var scope = new FakeScope(Member(alice.Id));

        var tasks = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, scope);

        await Assert.ThrowsAsync<GuanfuException>(
            () => tasks.SetAssignmentAsync(task.Id, "鲍勃", assigneeMemberId: bob.Id));

        // 分给自己是可以的
        var updated = await tasks.SetAssignmentAsync(task.Id, "爱丽丝", assigneeMemberId: alice.Id);
        Assert.Equal(alice.Id, updated.AssigneeMemberId);
    }

    [Fact]
    public async Task 审批只能本人批()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);
        var approvals = new ApprovalService(host.Db, host.Clock, null, new FakeScope(Member(Guid.NewGuid())));

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");

        var approval = await approvals.RequestAsync(
            "db_execute", "db.write", "{}", "写库", "agent-a",
            assigneeMemberId: alice.Id);

        // 别人的身份来批 → 拒
        var other = new ApprovalService(host.Db, host.Clock, null, new FakeScope(Member(Guid.NewGuid())));

        await Assert.ThrowsAsync<GuanfuException>(
            () => other.DecideAsync(approval.Id, true, "别人"));
    }

    [Fact]
    public async Task 管理员能代批但要写理由()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var boss = await members.CreateAsync("boss", "老板", "password-2", MemberRole.Admin);

        var requester = new ApprovalService(host.Db, host.Clock, null, new FakeScope(Member(boss.Id)));

        var approval = await requester.RequestAsync(
            "db_execute", "db.write", "{}", "写库", "agent-a",
            assigneeMemberId: alice.Id);

        var admin = new ApprovalService(host.Db, host.Clock, null, new FakeScope(Member(boss.Id, MemberRole.Admin)));

        // 不写理由 → 拒（代批必须留痕）
        await Assert.ThrowsAsync<GuanfuException>(
            () => admin.DecideAsync(approval.Id, true, "老板"));

        var decided = await admin.DecideAsync(approval.Id, true, "老板", "爱丽丝不在，我先批了");

        Assert.Equal("老板", decided.DecidedBy);
        Assert.Equal(alice.Id, decided.OnBehalfOfMemberId);
        Assert.Equal("爱丽丝不在，我先批了", decided.DecisionReason);
    }

    [Fact]
    public async Task 未分派的活可以自己认领()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "没人要的活");

        var tasks = new TaskService(
            host.Db, host.Clock, host.Git, host.Reviews, host.BranchGuard, null, null, null, null, new FakeScope(null));

        var claimed = await tasks.ClaimForMemberAsync(task.Id, alice.Id);

        Assert.Equal(alice.Id, claimed.AssigneeMemberId);
        Assert.Equal("爱丽丝", claimed.Assignee);

        // 已经有主人了 —— 别人不能再抢
        await Assert.ThrowsAsync<GuanfuException>(
            () => tasks.ClaimForMemberAsync(task.Id, bob.Id));
    }

    [Fact]
    public async Task 普通成员只看得到自己的待批()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var requester = new ApprovalService(host.Db, host.Clock, null, new FakeScope(null));

        await requester.RequestAsync("db_execute", "db.write", "{}", "爱丽丝的活", "agent-a",
            assigneeMemberId: alice.Id);
        await requester.RequestAsync("db_execute", "db.write", "{}", "鲍勃的活", "agent-b",
            assigneeMemberId: bob.Id);

        var asAlice = new ApprovalService(host.Db, host.Clock, null, new FakeScope(Member(alice.Id)));
        var visible = await asAlice.ListAsync();

        Assert.Single(visible);
        Assert.Equal("爱丽丝的活", visible[0].Summary);
    }
}
