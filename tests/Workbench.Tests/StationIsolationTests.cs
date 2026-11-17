using System.Diagnostics;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 工位隔离与并发闸门。
///
/// agent 时代的新问题：**干活的是 agent，不是人** —— 两个人共用一个工作区会互相踩文件。
/// 这里守住三条：
/// ① 主工作区同一时刻只允许一个 agent 占着；
/// ② 要并行，各开各的 git worktree —— 而且必须确实是这个仓库的 worktree；
/// ③ 项目级并行上限：并发过头要拦得住。
/// </summary>
public class StationIsolationTests
{
    // ---- 工位校验 ----

    [Fact]
    public void 本仓库的worktree算数()
    {
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var check = WorkPathPolicy.ValidateWorktree(repo, worktree);

            Assert.True(check.Ok, check.Error);
            Assert.True(check.IsIsolated);
            Assert.Equal(Path.GetFullPath(worktree), check.ResolvedPath);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public void 普通目录不是worktree()
    {
        var root = Directory.CreateTempSubdirectory("wb-station-plain-").FullName;
        try
        {
            var check = WorkPathPolicy.ValidateWorktree(root, root);

            Assert.False(check.Ok);
            Assert.Contains("不是 worktree", check.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 嵌套仓库不是worktree()
    {
        var (repo, _) = CreateRepoWithWorktree();
        try
        {
            // 主 checkout 的 .git 是**目录** —— 那不是 worktree
            var check = WorkPathPolicy.ValidateWorktree(repo, repo);

            Assert.False(check.Ok);
            Assert.Contains(".git 是目录", check.Error);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public void 别的仓库的worktree不算数()
    {
        var (repo, _) = CreateRepoWithWorktree();
        var (otherRepo, otherWorktree) = CreateRepoWithWorktree();
        try
        {
            // 声明别人仓库的 worktree = 换了个战场，不认
            var check = WorkPathPolicy.ValidateWorktree(repo, otherWorktree);

            Assert.False(check.Ok);
            Assert.Contains("不是本仓库的 worktree", check.Error);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(otherRepo);
        }
    }

    [Fact]
    public void 相对路径与不存在的目录都要拒()
    {
        var (repo, _) = CreateRepoWithWorktree();
        try
        {
            Assert.Contains("绝对路径", WorkPathPolicy.ValidateWorktree(repo, "wt").Error);
            Assert.Contains("不存在", WorkPathPolicy.ValidateWorktree(repo, "/tmp/不存在的目录-xyz").Error);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    // ---- 领取时的工位独占 ----

    [Fact]
    public async Task 主工作区同时只能一个agent占()
    {
        using var host = new WorkbenchTestHost();
        var (repo, _) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var first = await host.Tasks.CreateAsync(project.Id, "一");
            var second = await host.Tasks.CreateAsync(project.Id, "二");
            var third = await host.Tasks.CreateAsync(project.Id, "三");

            await host.Tasks.ClaimAsync(first.Id, "agent-a");
            await host.Tasks.ClaimAsync(second.Id, "agent-a");   // 同一个 agent 换活儿：不算冲突

            // 另一个 agent 想在同一个工作区干活 → 拦
            var ex = await Assert.ThrowsAsync<WorkbenchException>(
                async () => await host.Tasks.ClaimAsync(third.Id, "agent-b"));

            Assert.Contains("正被 agent-a 占着", ex.Message);
            Assert.Contains("worktree", ex.Message);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 声明自己的worktree就能并行()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var first = await host.Tasks.CreateAsync(project.Id, "一");
            var second = await host.Tasks.CreateAsync(project.Id, "二");

            await host.Tasks.ClaimAsync(first.Id, "agent-a");

            // 第二个 agent 带着自己的 worktree 来 → 允许并行
            var claimed = await host.Tasks.ClaimAsync(second.Id, "agent-b", workPath: worktree);

            Assert.Equal("agent-b", claimed.ClaimedBy);
            Assert.Equal(Path.GetFullPath(worktree), claimed.WorkPath);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 冒充worktree要被拒()
    {
        using var host = new WorkbenchTestHost();
        var (repo, _) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");

            var ex = await Assert.ThrowsAsync<WorkbenchException>(
                async () => await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: "/etc"));

            Assert.Contains("不是 worktree", ex.Message);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 租约过期后工位就放出来了()
    {
        using var host = new WorkbenchTestHost();
        var (repo, _) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var first = await host.Tasks.CreateAsync(project.Id, "一");
            var second = await host.Tasks.CreateAsync(project.Id, "二");

            await host.Tasks.ClaimAsync(first.Id, "agent-a", leaseMinutes: 10);

            await Assert.ThrowsAsync<WorkbenchException>(
                async () => await host.Tasks.ClaimAsync(second.Id, "agent-b"));

            // 租约到期 —— 工位不再被占
            host.Clock.Advance(TimeSpan.FromMinutes(11));

            var claimed = await host.Tasks.ClaimAsync(second.Id, "agent-b");
            Assert.Equal("agent-b", claimed.ClaimedBy);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    // ---- 工具认工位 ----

    [Fact]
    public async Task 文件工具按工位走()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");
            await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: worktree);

            // worktree 里有个文件，主工作区里没有
            File.WriteAllText(Path.Combine(worktree, "wt-only.txt"), "只在 worktree 里的东西");

            var workstations = new WorkstationResolver(host.Db, host.Clock);
            var files = new FileSystemService(host.Db, workstations);

            var resolved = await workstations.ResolveAsync(project.Id, task.Id);
            Assert.Equal(Path.GetFullPath(worktree), resolved);
            Assert.True(File.Exists(Path.Combine(worktree, "wt-only.txt")), "测试自己写的文件不在");

            var inWorktree = await files.ReadAsync(project.Id, "wt-only.txt", taskId: task.Id);
            Assert.False(string.IsNullOrEmpty(inWorktree.Content), "工位里的文件应该读得到");
            Assert.Contains("worktree", inWorktree.Content);

            // 不给任务 = 主工作区 —— 那边没有这个文件
            await Assert.ThrowsAsync<WorkbenchException>(
                async () => await files.ReadAsync(project.Id, "wt-only.txt"));
            Assert.Equal(
                Path.GetFullPath(repo),
                await workstations.ResolveAsync(project.Id, null));
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 租约过期后工具回到主工作区()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");
            await host.Tasks.ClaimAsync(task.Id, "agent-a", leaseMinutes: 10, workPath: worktree);

            var workstations = new WorkstationResolver(host.Db, host.Clock);

            Assert.Equal(Path.GetFullPath(worktree),
                await workstations.ResolveAsync(project.Id, task.Id));

            // 活干完了 → 工位还回去
            host.Clock.Advance(TimeSpan.FromMinutes(11));

            Assert.Equal(Path.GetFullPath(repo),
                await workstations.ResolveAsync(project.Id, task.Id));
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 会话没挂任务时按agent找回工位()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");
            await host.Tasks.ClaimAsync(task.Id, "agent-x", workPath: worktree);

            var workstations = new WorkstationResolver(host.Db, host.Clock);

            // 会话没挂任务（活是走 REST 领的）→ 按 agent 找回来
            var byAgent = await workstations.ResolveAsync(project.Id, null, "agent-x");
            Assert.Equal(Path.GetFullPath(worktree), byAgent);

            // 别的 agent 不该拿到这个工位
            Assert.Equal(Path.GetFullPath(repo), await workstations.ResolveAsync(project.Id, null, "agent-y"));
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 工位没了就退回主工作区()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");
            await host.Tasks.ClaimAsync(task.Id, "agent-a", workPath: worktree);

            // 工位目录被删了 —— 不能拿着失效目录继续读写
            Directory.Delete(worktree, recursive: true);

            var workstations = new WorkstationResolver(host.Db, host.Clock);
            Assert.Equal(Path.GetFullPath(repo),
                await workstations.ResolveAsync(project.Id, task.Id));
        }
        finally
        {
            Cleanup(repo);
        }
    }

    // ---- 会话认 worktree ----

    [Fact]
    public async Task 会话能绑到worktree()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);

            // agent 的工作目录就是 worktree —— 它是仓库的兄弟目录，
            // 但属于同一个仓库，不该被当成「不在任何项目范围内」。
            var bound = await host.Sessions.BindRepoAsync(worktree, "agent-a");

            Assert.True(bound.Ok, bound.Error);
            Assert.Equal(project.Id, bound.Session!.ProjectId);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 会话绑的是别的仓库时要拒()
    {
        using var host = new WorkbenchTestHost();
        var (repo, _) = CreateRepoWithWorktree();
        var (otherRepo, otherWorktree) = CreateRepoWithWorktree();
        try
        {
            await host.Projects.CreateAsync("P", repoPath: repo);

            // 别人仓库的 worktree —— 绑上来就等于换了个战场
            var bound = await host.Sessions.BindRepoAsync(otherWorktree, "agent-a");

            Assert.False(bound.Ok);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(otherRepo);
        }
    }

    // ---- 并行上限 ----

    [Fact]
    public async Task 并行上限拦得住()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            await host.Projects.UpdateConfigAsync(project.Id, maxConcurrentAgents: 1);

            var first = await host.Tasks.CreateAsync(project.Id, "一");
            var second = await host.Tasks.CreateAsync(project.Id, "二");

            await host.Tasks.ClaimAsync(first.Id, "agent-a");

            // 上限 1 → 第二个 agent 领不到（哪怕它有自己的 worktree）
            var ex = await Assert.ThrowsAsync<WorkbenchException>(
                async () => await host.Tasks.ClaimAsync(second.Id, "agent-b", workPath: worktree));

            Assert.Contains("并行上限", ex.Message);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 不设上限就不拦()
    {
        using var host = new WorkbenchTestHost();
        var (repo, worktree) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var first = await host.Tasks.CreateAsync(project.Id, "一");
            var second = await host.Tasks.CreateAsync(project.Id, "二");

            await host.Tasks.ClaimAsync(first.Id, "agent-a");
            var claimed = await host.Tasks.ClaimAsync(second.Id, "agent-b", workPath: worktree);

            Assert.Equal("agent-b", claimed.ClaimedBy);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 上下文里带着工位现状()
    {
        using var host = new WorkbenchTestHost();
        var (repo, _) = CreateRepoWithWorktree();
        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var first = await host.Tasks.CreateAsync(project.Id, "一");
            var second = await host.Tasks.CreateAsync(project.Id, "二");

            await host.Tasks.ClaimAsync(first.Id, "agent-a");

            var context = await host.Tasks.GetContextAsync(second.Id);

            Assert.NotNull(context.Station);
            Assert.Equal("agent-a", context.Station!.MainCheckoutHolder);
            Assert.Equal(first.Key, context.Station.MainCheckoutTaskKey);
            Assert.Equal(1, context.Station.ActiveLeases);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    // ---- 夹具 ----

    /// <summary>造一个真仓库 + 一个真 worktree（校验逻辑读的是 .git 文件，得有真的）。</summary>
    private static (string Repo, string Worktree) CreateRepoWithWorktree()
    {
        var root = Directory.CreateTempSubdirectory("wb-station-").FullName;
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);

        Run(repo, "init", "-q", ".");
        Run(repo, "-c", "user.email=t@t", "-c", "user.name=t",
            "commit", "-q", "--allow-empty", "-m", "init");

        var worktree = Path.Combine(root, "wt");
        Run(repo, "worktree", "add", "-q", worktree, "-b", "feature/x");

        return (repo, worktree);
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

    private static void Cleanup(string repo)
    {
        var root = Path.GetDirectoryName(repo);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
