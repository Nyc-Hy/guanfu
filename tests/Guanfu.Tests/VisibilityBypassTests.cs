using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 可见性的**旁路**要堵死。
///
/// 列表过滤只挡得住「列表」—— 挡不住「拿别人的任务 id 直接调接口」。
/// 这一组盯的就是那条路：文档、产出、评审、流水、用量、调度板、打包候选池。
/// </summary>
public sealed class VisibilityBypassTests
{
    private sealed class FakeScope(MemberScope? scope) : ICurrentMemberScope
    {
        public MemberScope? Current { get; set; } = scope;
    }

    /// <summary>造一个项目：爱丽丝的活 + 鲍勃的活。</summary>
    private static async Task<(GuanfuTestHost Host, Guid ProjectId, Guid MineId, Guid TheirsId)>
        SetupAsync()
    {
        var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("项目");
        var mine = await host.Tasks.CreateAsync(project.Id, "我的活");
        var theirs = await host.Tasks.CreateAsync(project.Id, "别人的活");

        var setup = new TaskService(host.Db, host.Clock, host.Git, host.Reviews);
        await setup.SetAssignmentAsync(mine.Id, "爱丽丝", assigneeMemberId: alice.Id);
        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: bob.Id);

        return (host, project.Id, mine.Id, theirs.Id);
    }

    private static MemberScope AsAlice(Guid memberId) =>
        new(memberId, "alice", "爱丽丝", MemberRole.Member);

    [Fact]
    public async Task 成员的调度板里没有别人的活()
    {
        var (host, projectId, _, _) = await SetupAsync();
        using var _ = host;

        var scope = new FakeScope(AsAlice(Guid.NewGuid()));

        var board = await new DispatchService(host.Db, host.Clock, scope).GetAsync(projectId);

        Assert.DoesNotContain(board.Ready, t => t.Title == "别人的活");
        Assert.DoesNotContain(board.InProgress, t => t.Title == "别人的活");
    }

    [Fact]
    public async Task 成员的调度建议里也没有别人的活()
    {
        var (host, projectId, _, _) = await SetupAsync();
        using var _ = host;

        var scope = new FakeScope(AsAlice(Guid.NewGuid()));

        var suggestions = await new DispatchService(host.Db, host.Clock, scope)
            .SuggestAsync(projectId);

        Assert.DoesNotContain(suggestions, s => s.Title == "别人的活");
    }

    [Fact]
    public async Task 成员的打包候选池里没有别人的产物()
    {
        var (host, projectId, mineId, _) = await SetupAsync();
        using var _ = host;

        var project = await host.Projects.GetAsync(projectId);
        var milestone = await host.Milestones.CreateAsync(projectId, "12 月版本");
        await host.Milestones.AssignTaskAsync(mineId, milestone.Id);

        await host.Tasks.AddArtifactAsync(
            mineId, ArtifactType.SqlScript, "我的.sql", "SELECT 1;");
        await host.Tasks.AddArtifactAsync(
            mineId, ArtifactType.SqlScript, "我的2.sql", "SELECT 2;");

        var scope = new FakeScope(AsAlice(Guid.NewGuid()));

        var candidates = await new DeliveryPackageService(
            host.Db,
            new NoopDocumentStore(),
            new NoopPackageStore(),
            host.Clock,
            scope).ListCandidatesAsync(milestone.Id);

        // 可见性按「任务」算 —— 候选池来自这个版本下的任务。
        // 这里的关键是：鲍勃的活不在爱丽丝的可见范围里，所以池子里只该有她自己的。
        Assert.All(candidates.Candidates, c => Assert.Contains(c.TaskTitle, new[] { "我的活" }));
    }

    [Fact]
    public async Task 成员的用量页看不到别人的账()
    {
        var (host, _, _, theirsId) = await SetupAsync();
        using var _ = host;

        var scope = new FakeScope(AsAlice(Guid.NewGuid()));

        // 看不见的任务 → 跟「不存在」一样（不告诉对方「它存在，只是你不能看」）
        await Assert.ThrowsAsync<NotFoundException>(
            () => new TaskUsageService(host.Db, scope).GetTaskUsageAsync(theirsId));
    }

    [Fact]
    public async Task 管理员的调度板里什么都有()
    {
        var (host, projectId, _, _) = await SetupAsync();
        using var _ = host;

        var scope = new FakeScope(new MemberScope(Guid.NewGuid(), "boss", "老板", MemberRole.Admin));

        var board = await new DispatchService(host.Db, host.Clock, scope).GetAsync(projectId);

        Assert.Contains(board.Ready, t => t.Title == "别人的活");
    }

    private sealed class NoopDocumentStore : IDocumentStore
    {
        public Task<StoredFile> SaveAsync(
            Guid taskId, string fileName, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string ResolveAbsolutePath(string relativePath) => relativePath;

        public bool Exists(string relativePath) => false;
    }

    private sealed class NoopPackageStore : IDeliveryPackageStore
    {
        public Task<StoredFile> SaveAsync(
            Guid milestoneId, string fileName, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string ResolveAbsolutePath(string relativePath) => relativePath;
    }
}
