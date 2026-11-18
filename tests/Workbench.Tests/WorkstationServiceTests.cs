using System.Diagnostics;
using Workbench.Application.Services;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 工位的清理。
///
/// **worktree 建了没人收** —— agent 建了工位，干完活就走了，目录一直躺在磁盘上：
/// 下一个 agent 不知道那个目录还在，人也不知道哪些能删。
///
/// 所以工位要能列出来（谁在用、有没有人管），也要能放掉。
/// **工作台不替 agent 删目录** —— 只给确切命令（跟不替它建 worktree 一个道理）。
/// </summary>
public sealed class WorkstationServiceTests : IDisposable
{
    // 真仓库 + 真 worktree：登记工位要过校验，假的目录过不了
    private readonly string _root = Directory.CreateTempSubdirectory("wb-station-svc-").FullName;
    private readonly string _repo = Path.Combine(Directory.CreateTempSubdirectory("wb-station-svc-").FullName, "repo");
    private readonly string _worktree = Path.Combine(Directory.CreateTempSubdirectory("wb-station-svc-").FullName, "wt");

    public WorkstationServiceTests()
    {
        Directory.CreateDirectory(_repo);
        Run(_repo, "init", "-q", ".");
        Run(_repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
        Run(_repo, "worktree", "add", "-q", _worktree, "-b", "wb/req-001");
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _repo, _worktree })
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    private static void Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} 失败: {process.StandardError.ReadToEnd()}");
        }
    }

    [Fact]
    public async Task 列得出登记着的工位()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);
        var task = await host.Tasks.CreateAsync(project.Id, "有工位的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: _worktree);

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        var stations = await service.ListAsync(project.Id);

        var station = Assert.Single(stations);
        Assert.Equal(task.Id, station.TaskId);
        Assert.Equal("agent-a", station.AgentId);
        Assert.Equal(_worktree, station.WorkPath);
        Assert.True(station.LeaseAlive);
        Assert.False(station.Orphaned);
    }

    [Fact]
    public async Task 租约过期了就是没人管的工位()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);
        var task = await host.Tasks.CreateAsync(project.Id, "过期工位的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: _worktree);

        // 租约过期 = 活干完了 = 工位该收了
        host.Clock.Advance(TimeSpan.FromMinutes(60));

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        var stations = await service.ListAsync(project.Id);

        var station = Assert.Single(stations);
        Assert.False(station.LeaseAlive);
        Assert.True(station.Orphaned);
        Assert.Contains($"git -C {_repo} worktree remove", station.CleanupCommand!);
    }

    [Fact]
    public async Task 仓库里没人登记的worktree也算没人管()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);

        // git 说仓库里有这个 worktree，但工作台里没人登记它
        host.Git.Worktrees =
        [
            new GitWorktree(_repo, "main", IsMain: true),
            new GitWorktree(_worktree, "wb/req-001", IsMain: false)
        ];

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        var stations = await service.ListAsync(project.Id);

        var station = Assert.Single(stations);
        Assert.Null(station.TaskId);
        Assert.True(station.Orphaned);
        Assert.Equal(_worktree, station.WorkPath);
        Assert.Contains("worktree remove", station.CleanupCommand!);
    }

    [Fact]
    public async Task 活干完了的工位标成该收()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);
        var task = await host.Tasks.CreateAsync(project.Id, "干完的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: _worktree);

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        Assert.False((await service.ListAsync(project.Id)).Single().ReadyToClean);

        // 推到已完成 —— 活干完了，那个工位该拆
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);

        var station = (await service.ListAsync(project.Id)).Single();
        Assert.True(station.ReadyToClean);
        Assert.Contains("worktree remove", station.CleanupCommand!);

        // 账上也要留一笔 —— 事后翻得到「这个工位该收」
        var events = await host.Tasks.ListEventsAsync(task.Id);
        Assert.Contains(events, e => e.EventType == TaskEventTypes.StationReadyToClean);
    }

    [Fact]
    public async Task 放掉工位会清登记并给出清理命令()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);
        var task = await host.Tasks.CreateAsync(project.Id, "要收工位的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: _worktree);

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        var result = await service.ReleaseAsync(task.Id);

        Assert.True(result.Ok);
        Assert.Equal(_worktree, result.WorkPath);
        Assert.Contains($"git -C {_repo} worktree remove", result.CleanupCommand!);

        // 登记真的清掉了
        var stations = await service.ListAsync(project.Id);
        Assert.Empty(stations);

        // 而且留了痕 —— 工位放掉是件该记的事
        var events = await host.Tasks.ListEventsAsync(task.Id);
        Assert.Contains(events, e => e.EventType == TaskEventTypes.StationReleased);
    }

    [Fact]
    public async Task 没有工位的活放不掉()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);
        var task = await host.Tasks.CreateAsync(project.Id, "用主工作区的活");

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        var result = await service.ReleaseAsync(task.Id);

        Assert.False(result.Ok);
        Assert.Contains("没有登记工位", result.Error!);
    }

    [Fact]
    public async Task 同一个工位不会因为路径写法不同被算成两个()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目", repoPath: _repo);
        var task = await host.Tasks.CreateAsync(project.Id, "软链工位的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: _worktree);

        // git 报的是解过软链的路径（macOS 上 /tmp → /private/tmp），
        // 登记的是人填的那个 —— 不归一化就会把同一个工位列成两条
        host.Git.Worktrees =
        [
            new GitWorktree(_repo, "main", IsMain: true),
            new GitWorktree(_worktree, "wb/req-001", IsMain: false)
        ];

        var service = new WorkstationService(host.Db, host.Git, host.Clock);
        var stations = await service.ListAsync(project.Id);

        Assert.Single(stations);
    }

    [Fact]
    public void 解析git的worktree清单()
    {
        var output = string.Join("\n",
            "worktree /repo",
            "HEAD abc123",
            "branch refs/heads/main",
            "",
            "worktree /repo-wt",
            "HEAD def456",
            "branch refs/heads/feature/x",
            "",
            "worktree /repo-gone",
            "HEAD 000000",
            "detached",
            "prunable gitdir file points to non-existent location",
            "");

        var worktrees = GitInspector.ParseWorktrees(output);

        Assert.Equal(3, worktrees.Count);

        Assert.True(worktrees[0].IsMain);
        Assert.Equal("main", worktrees[0].Branch);

        Assert.False(worktrees[1].IsMain);
        Assert.Equal("feature/x", worktrees[1].Branch);

        // 失效的那个要标出来 —— 它才是真正该清的
        Assert.True(worktrees[2].Prunable);
        Assert.Null(worktrees[2].Branch);
    }
}
