using System.Diagnostics;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>
/// 工位自助化：agent 得能一眼知道「我该去哪儿干活」。
///
/// 以前这个答案是没有的 —— agent 只能靠踩文件发现有人在同一个目录里干活，
/// 或者自己猜 worktree 该建在哪（worktree 的路径规则不直观，猜错就白折腾）。
///
/// 现在观复直接给：**该用哪个目录、主工作区被谁占着、要并行的话建哪个工位、
/// 建它的确切命令**。观复仍然不替 agent 建 worktree（规矩 35）——
/// 那是它自己仓库里的事，观复只给路径与命令，外加事后校验。
/// </summary>
public sealed class WorkstationAdviceTests
{
    [Fact]
    public async Task 主工作区空着时不给多余的建议()
    {
        using var host = new GuanfuTestHost();
        var (repo, _) = CreateRepoWithWorktree();

        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");

            var station = await host.Tasks.GetStationAsync(task.Id);

            Assert.True(station.MainCheckoutAvailable);
            Assert.Null(station.MainCheckoutHolder);
            // 能直接用主工作区就别多此一举 —— 建议是给「要并行」时用的
            Assert.Null(station.SuggestedWorktree);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 主工作区被别人占着时给出工位路径与命令()
    {
        using var host = new GuanfuTestHost();
        var (repo, _) = CreateRepoWithWorktree();

        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var occupied = await host.Tasks.CreateAsync(project.Id, "占着的活");
            var waiting = await host.Tasks.CreateAsync(project.Id, "想干的活");

            await host.Tasks.ClaimAsync(occupied.Id, "agent-a");

            var station = await host.Tasks.GetStationAsync(waiting.Id, "agent-b");

            Assert.False(station.MainCheckoutAvailable);
            Assert.Equal("agent-a", station.MainCheckoutHolder);

            // 该去哪儿、怎么建，都得说清楚
            Assert.NotNull(station.SuggestedWorktree);
            Assert.NotNull(station.SuggestedBranch);
            Assert.Contains(
                station.SetupCommands!,
                c => c.Contains("worktree add") && c.Contains(station.SuggestedWorktree!));
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 建议的工位在仓库旁边而不是仓库里面()
    {
        using var host = new GuanfuTestHost();
        var (repo, _) = CreateRepoWithWorktree();

        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var occupied = await host.Tasks.CreateAsync(project.Id, "占着的活");
            var waiting = await host.Tasks.CreateAsync(project.Id, "想干的活");

            await host.Tasks.ClaimAsync(occupied.Id, "agent-a");
            var station = await host.Tasks.GetStationAsync(waiting.Id, "agent-b");

            var suggestion = station.SuggestedWorktree!;

            // 仓库里的目录会变成主工作区的未跟踪文件 —— 提交时容易误带进去
            Assert.False(
                suggestion.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "建议的工位不能落在仓库里面。");

            // 而且得是这个仓库的兄弟目录
            Assert.Equal(
                Path.GetDirectoryName(repo),
                Path.GetDirectoryName(Path.GetDirectoryName(suggestion)));
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 自己占着主工作区对自己是可用的()
    {
        using var host = new GuanfuTestHost();
        var (repo, _) = CreateRepoWithWorktree();

        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "我的活");

            await host.Tasks.ClaimAsync(task.Id, "agent-a");

            // 问的是同一个 agent —— 主工作区当然能用
            var station = await host.Tasks.GetStationAsync(task.Id, "agent-a");

            Assert.True(station.MainCheckoutAvailable);
            Assert.Null(station.SuggestedWorktree);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task 任务绑了分支就用那个分支()
    {
        using var host = new GuanfuTestHost();
        var (repo, _) = CreateRepoWithWorktree();

        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var occupied = await host.Tasks.CreateAsync(project.Id, "占着的活");
            var waiting = await host.Tasks.CreateAsync(project.Id, "想干的活", branch: "feature/login");

            await host.Tasks.ClaimAsync(occupied.Id, "agent-a");
            var station = await host.Tasks.GetStationAsync(waiting.Id, "agent-b");

            Assert.Equal("feature/login", station.SuggestedBranch);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    [Fact]
    public async Task MCP工具能问到工位()
    {
        using var host = new GuanfuTestHost();
        var (repo, _) = CreateRepoWithWorktree();

        try
        {
            var project = await host.Projects.CreateAsync("P", repoPath: repo);
            var task = await host.Tasks.CreateAsync(project.Id, "一");

            var tools = new GuanfuMcpTools(
                host.Projects, host.Tasks, host.Approvals, host.Audit, host.Dependencies, host.Exports);

            var result = await tools.GetWorkstationAsync(task.Id);

            Assert.True(result.Ok);
            Assert.NotNull(result.Station);
            Assert.Equal(repo, result.Station!.MainCheckout);
        }
        finally
        {
            Cleanup(repo);
        }
    }

    // ---- 夹具 ----

    private static (string Repo, string Worktree) CreateRepoWithWorktree()
    {
        var root = Directory.CreateTempSubdirectory("wb-station-advice-").FullName;
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
