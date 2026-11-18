using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 会话绑定：agent 的对话怎么跟项目接上。
///
/// 这条链路是整套东西的地基 —— 没有它，审计里的项目/任务归属就只能靠 agent 自觉填。
/// </summary>
public sealed class SessionBindingTests
{
    [Fact]
    public async Task 仓库落在项目绑定范围内就能绑上()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        var project = await host.Projects.CreateAsync("甲项目", repoPath: repo);
        var sessions = new AgentSessionService(host.Db, host.Clock);

        var bound = await sessions.BindRepoAsync(repo, "agent-x");

        Assert.True(bound.Ok, bound.Error);
        Assert.Equal(project.Id, bound.Session!.ProjectId);
        Assert.Equal("agent-x", bound.Session.AgentId);
    }

    [Fact]
    public async Task 仓库子目录也算在同一个项目下()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        var sub = Path.Combine(repo, "src", "app");
        Directory.CreateDirectory(sub);

        var project = await host.Projects.CreateAsync("甲项目", repoPath: repo);
        var sessions = new AgentSessionService(host.Db, host.Clock);

        var bound = await sessions.BindRepoAsync(sub, "agent-x");

        Assert.True(bound.Ok, bound.Error);
        Assert.Equal(project.Id, bound.Session!.ProjectId);
    }

    [Fact]
    public async Task 目录不在任何项目范围内绑不上并给出候选()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        var elsewhere = NewTempDir();

        await host.Projects.CreateAsync("甲项目", repoPath: repo);
        var sessions = new AgentSessionService(host.Db, host.Clock);

        var bound = await sessions.BindRepoAsync(elsewhere, "agent-x");

        Assert.False(bound.Ok);
        Assert.Contains("不在任何项目", bound.Error);
        Assert.NotNull(bound.CandidateProjects);
        Assert.Contains("甲项目", bound.CandidateProjects!);
    }

    [Fact]
    public async Task 多个项目绑同一个仓库时要显式指定()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();

        var first = await host.Projects.CreateAsync("甲项目", repoPath: repo);

        // 直接写库造出「两个项目绑同一个仓库」——
        // 现在建项目时会拦（ProjectService.EnsureRepoBindingIsUniqueAsync），
        // 但老库、导入的数据里可能有这种重复绑定，会话层仍要兜住。
        var second = await host.Projects.CreateAsync("乙项目");
        var firstRow = await host.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == first.Id);
        await host.Db.Projects
            .Where(p => p.Id == second.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.RepoRoot, firstRow.RepoRoot)
                .SetProperty(p => p.RepoPath, repo));

        var sessions = new AgentSessionService(host.Db, host.Clock);

        var bound = await sessions.BindRepoAsync(repo, "agent-x");

        Assert.False(bound.Ok);
        Assert.Contains("多个项目", bound.Error);

        // 显式指定就能定下来
        var explicitBind = await sessions.BindRepoAsync(repo, "agent-x", projectId: first.Id);

        Assert.True(explicitBind.Ok, explicitBind.Error);
        Assert.Equal(first.Id, explicitBind.Session!.ProjectId);
        Assert.NotEqual(second.Id, explicitBind.Session.ProjectId);
    }

    [Fact]
    public async Task 显式指定的项目必须真的覆盖这个目录()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        var elsewhere = NewTempDir();

        var project = await host.Projects.CreateAsync("甲项目", repoPath: repo);
        var sessions = new AgentSessionService(host.Db, host.Clock);

        var bound = await sessions.BindRepoAsync(elsewhere, "agent-x", projectId: project.Id);

        Assert.False(bound.Ok);
        Assert.Contains("不在项目", bound.Error);
    }

    [Fact]
    public async Task 同一协议会话复用同一条会话()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        await host.Projects.CreateAsync("甲项目", repoPath: repo);
        var sessions = new AgentSessionService(host.Db, host.Clock);

        var first = await sessions.BindRepoAsync(repo, "agent-x", mcpSessionId: "mcp-1");
        var second = await sessions.BindRepoAsync(repo, "agent-x", mcpSessionId: "mcp-1");

        Assert.True(first.Ok && second.Ok);
        Assert.Equal(first.Session!.Id, second.Session!.Id);
    }

    [Fact]
    public async Task 没有会话也没有项目时报错并给出指引()
    {
        using var host = new GuanfuTestHost();
        var resolver = new ProjectScopeResolver(host.Db);

        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => resolver.ResolveAsync(null));

        Assert.Contains("guanfu_bind_session", ex.Message);
    }

    [Fact]
    public async Task 会话与显式项目不一致时拒绝()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        var otherRepo = NewTempDir();

        var first = await host.Projects.CreateAsync("甲项目", repoPath: repo);
        var second = await host.Projects.CreateAsync("乙项目", repoPath: otherRepo);

        var sessions = new AgentSessionService(host.Db, host.Clock);
        var bound = await sessions.BindRepoAsync(repo, "agent-x");
        Assert.True(bound.Ok, bound.Error);

        var accessor = new FakeSessionAccessor(bound.Session!);
        var resolver = new ProjectScopeResolver(host.Db, accessor);

        // 显式传同一个项目 —— 放行
        var ok = await resolver.ResolveAsync(first.Id);
        Assert.Equal(first.Id, ok.ProjectId);

        // 显式传别的项目 —— 拒
        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => resolver.ResolveAsync(second.Id));
        Assert.Contains("不一致", ex.Message);
    }

    [Fact]
    public async Task 会话自动决定项目归属()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        var project = await host.Projects.CreateAsync("甲项目", repoPath: repo);

        var sessions = new AgentSessionService(host.Db, host.Clock);
        var bound = await sessions.BindRepoAsync(repo, "agent-x");
        Assert.True(bound.Ok, bound.Error);

        var resolver = new ProjectScopeResolver(host.Db, new FakeSessionAccessor(bound.Session!));

        var scope = await resolver.ResolveAsync(null);

        Assert.Equal(project.Id, scope.ProjectId);
        Assert.Equal("agent-x", scope.AgentId);
    }

    [Fact]
    public async Task 认领后任务挂到会话上()
    {
        using var host = new GuanfuTestHost();
        var repo = NewTempDir();
        await host.Projects.CreateAsync("甲项目", repoPath: repo);

        var sessions = new AgentSessionService(host.Db, host.Clock);
        var bound = await sessions.BindRepoAsync(repo, "agent-x");
        Assert.True(bound.Ok, bound.Error);

        var task = await host.Tasks.CreateAsync(bound.Session!.ProjectId, "要认领的任务");
        var attached = await sessions.AttachTaskAsync(bound.Session.Id, task.Id);

        Assert.NotNull(attached);
        Assert.Equal(task.Id, attached!.TaskId);

        var listed = await sessions.ListByTaskAsync(task.Id);
        Assert.Single(listed);
        Assert.Equal(bound.Session.Id, listed[0].Id);
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "wb-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeSessionAccessor(AgentSessionDto session) : IAgentSessionAccessor
    {
        public Task<AgentSessionDto?> CurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSessionDto?>(session);

        public string? CurrentMcpSessionId => null;

        public string? CurrentAgentId => null;
    }
}
