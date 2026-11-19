using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 搜产物，以及**搜索不能成为可见性的旁路**。
///
/// 交付物得能被搜到 ——「这段 SQL 在哪个任务里」答不出来，
/// 脚本一多就没人找得到它是哪个版本带来的。
///
/// 但搜索**不能成为绕开隔离的后门**：普通成员搜得到的，
/// 必须都是他本来就看得见的。
/// </summary>
public sealed class ArtifactSearchTests
{
    private sealed class FakeScope(MemberScope? scope) : ICurrentMemberScope
    {
        public MemberScope? Current { get; set; } = scope;
    }

    [Fact]
    public async Task 能搜到产物内容()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写迁移脚本的活");

        await host.Tasks.AddArtifactAsync(
            task.Id, ArtifactType.SqlScript, "回滚.sql", "DELETE FROM orders WHERE id = 42;");

        var artifacts = new ArtifactService(host.Db, host.Clock);
        var hits = await artifacts.SearchAsync("delete from orders");

        var hit = Assert.Single(hits);
        Assert.Equal("回滚.sql", hit.Title);
        Assert.Equal("SQL 脚本", UiText.ArtifactTypeLabel(ArtifactType.SqlScript));
        Assert.Contains("DELETE FROM orders", hit.Snippet);
    }

    [Fact]
    public async Task 能按标题搜到()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");

        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.SqlScript, "建表.sql", "CREATE TABLE t(id int);");

        var artifacts = new ArtifactService(host.Db, host.Clock);
        var hits = await artifacts.SearchAsync("建表");

        Assert.Single(hits);
    }

    [Fact]
    public async Task 搜不到时返回空()
    {
        using var host = new GuanfuTestHost();
        var artifacts = new ArtifactService(host.Db, host.Clock);

        Assert.Empty(await artifacts.SearchAsync("没有的东西"));
        Assert.Empty(await artifacts.SearchAsync("  "));
    }

    [Fact]
    public async Task 成员搜不到别人的产物()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("搜索项目");
        var mine = await host.Tasks.CreateAsync(project.Id, "我的活");
        var theirs = await host.Tasks.CreateAsync(project.Id, "别人的活");

        await host.Tasks.AddArtifactAsync(mine.Id, ArtifactType.SqlScript, "我的.sql", "SELECT 'mine';");
        await host.Tasks.AddArtifactAsync(theirs.Id, ArtifactType.SqlScript, "他的.sql", "SELECT 'his';");

        // 归属：爱丽丝的活归爱丽丝，鲍勃的活归鲍勃
        var setup = new TaskService(host.Db, host.Clock, host.Git, host.Reviews);
        await setup.SetAssignmentAsync(mine.Id, "爱丽丝", assigneeMemberId: alice.Id);
        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: bob.Id);

        var scope = new FakeScope(new MemberScope(alice.Id, "alice", "爱丽丝", MemberRole.Member));
        var artifacts = new ArtifactService(host.Db, host.Clock, scope);

        var hits = await artifacts.SearchAsync("SELECT");

        var hit = Assert.Single(hits);
        Assert.Equal("我的.sql", hit.Title);
    }

    [Fact]
    public async Task 管理员搜得到全部产物()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var boss = await members.CreateAsync("boss", "老板", "password-2", MemberRole.Admin);

        var project = await host.Projects.CreateAsync("搜索项目");
        var mine = await host.Tasks.CreateAsync(project.Id, "爱丽丝的活");
        var theirs = await host.Tasks.CreateAsync(project.Id, "鲍勃的活");

        await host.Tasks.AddArtifactAsync(mine.Id, ArtifactType.SqlScript, "a.sql", "SELECT 'a';");
        await host.Tasks.AddArtifactAsync(theirs.Id, ArtifactType.SqlScript, "b.sql", "SELECT 'b';");

        var setup = new TaskService(host.Db, host.Clock, host.Git, host.Reviews);
        await setup.SetAssignmentAsync(mine.Id, "爱丽丝", assigneeMemberId: alice.Id);
        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: null);

        var scope = new FakeScope(new MemberScope(boss.Id, "boss", "老板", MemberRole.Admin));
        var artifacts = new ArtifactService(host.Db, host.Clock, scope);

        var hits = await artifacts.SearchAsync("SELECT");

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SQL执行记录的搜索也不该成为旁路()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("搜索项目");
        var mine = await host.Tasks.CreateAsync(project.Id, "我的活");
        var theirs = await host.Tasks.CreateAsync(project.Id, "别人的活");

        var setup = new TaskService(host.Db, host.Clock, host.Git, host.Reviews);
        await setup.SetAssignmentAsync(mine.Id, "爱丽丝", assigneeMemberId: alice.Id);
        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: bob.Id);

        var calls = new DbCallService(host.Db, host.Clock);

        await calls.RecordAsync(new DbCallRecordEntry(
            null, project.Id, mine.Id, null, null, "db_execute", null, "我的库", "SELECT 1", "Read",
            [], null, "success", null, "agent-a", 1));
        await calls.RecordAsync(new DbCallRecordEntry(
            null, project.Id, theirs.Id, null, null, "db_execute", null, "他的库", "SELECT 2", "Read",
            [], null, "success", null, "agent-b", 1));

        var scope = new FakeScope(new MemberScope(alice.Id, "alice", "爱丽丝", MemberRole.Member));
        var scoped = new DbCallService(host.Db, host.Clock, scope);

        var hits = await scoped.SearchAsync("SELECT");

        var hit = Assert.Single(hits);
        Assert.Equal("我的库", hit.Database);
    }
}
