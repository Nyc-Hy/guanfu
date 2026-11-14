using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Mcp;

namespace Workbench.Tests;

/// <summary>
/// 审批闸门。
///
/// 核心承诺：**一条审批只能执行一次，而且只能执行当初批的那件事**。
///
/// 之前闸门只认「状态是不是 Approved」，不比对工具、不比对操作内容 ——
/// 于是批一条 shell_run 就能拿去推代码，批一条「提交 A」就能提交 B。
/// 这些测试就是守住这条底线的。
/// </summary>
public class ApprovalGateTests
{
    private sealed class RecordingGitWriter : IGitWriter
    {
        public List<string> Commits { get; } = new();
        public List<(string? Branch, string? Remote)> Pushes { get; } = new();

        public Task<GitCommandResult> CommitAsync(string repoPath, string message, CancellationToken cancellationToken = default)
        {
            Commits.Add(message);
            return Task.FromResult(new GitCommandResult(true, "committed", null));
        }

        public Task<GitCommandResult> PushAsync(string repoPath, string? branch = null, string? remote = null, CancellationToken cancellationToken = default)
        {
            Pushes.Add((branch, remote));
            return Task.FromResult(new GitCommandResult(true, "pushed", null));
        }
    }

    /// <summary>记录跑过哪些命令的假 shell —— 不真的起进程。</summary>
    private sealed class RecordingShellRunner : IShellRunner
    {
        public List<string> Executed { get; } = new();

        public bool IsAllowed(string? allowedCommandsJson, string command) =>
            ShellRunner.ParseAllowed(allowedCommandsJson)
                .Any(p => command.Trim().StartsWith(p + " ", StringComparison.OrdinalIgnoreCase));

        public bool IsAllowed(IReadOnlyList<string> allowedCommands, string command) =>
            allowedCommands.Any(p => command.Trim().StartsWith(p + " ", StringComparison.OrdinalIgnoreCase));

        public Task<ShellResult> RunAsync(string workingDirectory, string command, CancellationToken cancellationToken = default)
        {
            Executed.Add(command);
            return Task.FromResult(new ShellResult(true, 0, "ok", ""));
        }
    }

    private static (GeneralMcpTools Tools, RecordingGitWriter Git, RecordingShellRunner Shell, IProjectService Projects)
        BuildTools(WorkbenchTestHost host)
    {
        var git = new RecordingGitWriter();
        var shell = new RecordingShellRunner();

        var tools = new GeneralMcpTools(
            host.Files,
            host.Projects,
            git,
            shell,
            host.Gate,
            host.Audit,
            host.Clock);

        return (tools, git, shell, host.Projects);
    }

    [Fact]
    public async Task 批过的审批不能拿去执行另一条命令()
    {
        using var host = new WorkbenchTestHost();
        var (tools, _, shell, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo"]);

        var first = await tools.RunShellAsync(project.Id, "echo hello", waitSeconds: 1);
        Assert.False(first.Ok);
        Assert.NotEqual(Guid.Empty, first.ApprovalId);

        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        // 同一条审批，换一条命令 —— 必须被拒
        var second = await tools.RunShellAsync(project.Id, "echo pwned", approvalId: first.ApprovalId);

        Assert.False(second.Ok, "审批被复用到别的命令上了");
        Assert.Empty(shell.Executed);
    }

    [Fact]
    public async Task 批过的审批不能跨工具复用()
    {
        using var host = new WorkbenchTestHost();
        var (tools, git, _, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo", "git"]);

        var first = await tools.RunShellAsync(project.Id, "echo hello", waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        // 批的是 shell_run，拿去调 git_commit —— 审批类型都不一样
        var second = await tools.CommitAsync(project.Id, "偷偷提交", approvalId: first.ApprovalId);

        Assert.False(second.Ok, "shell_run 的审批被 git_commit 用掉了");
        Assert.Empty(git.Commits);
    }

    [Fact]
    public async Task 批过的审批不能换参数复用()
    {
        using var host = new WorkbenchTestHost();
        var (tools, git, _, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo", "git"]);

        var first = await tools.CommitAsync(project.Id, "提交 A", waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        // 审批的是「提交 A」，这里提交「提交 B」
        var second = await tools.CommitAsync(project.Id, "提交 B", approvalId: first.ApprovalId);

        Assert.False(second.Ok, "同一条审批被拿去提交了另一条 message");
        Assert.Empty(git.Commits);
    }

    [Fact]
    public async Task 一条审批只能执行一次()
    {
        using var host = new WorkbenchTestHost();
        var (tools, git, _, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo", "git"]);

        var first = await tools.CommitAsync(project.Id, "提交 A", waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        var ok = await tools.CommitAsync(project.Id, "提交 A", approvalId: first.ApprovalId);
        Assert.True(ok.Ok, "第一次执行应该成功");

        // 同一条审批再跑一遍 —— 必须被拒
        var again = await tools.CommitAsync(project.Id, "提交 A", approvalId: first.ApprovalId);

        Assert.False(again.Ok, "同一条审批被执行了两次");
        Assert.Single(git.Commits);
    }

    [Fact]
    public async Task 并发执行只有一次能过()
    {
        using var host = new WorkbenchTestHost();
        var (tools, git, _, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo", "git"]);

        var first = await tools.CommitAsync(project.Id, "提交 A", waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        var results = await Task.WhenAll(
            tools.CommitAsync(project.Id, "提交 A", approvalId: first.ApprovalId),
            tools.CommitAsync(project.Id, "提交 A", approvalId: first.ApprovalId));

        Assert.Single(results.Where(r => r.Ok));
        Assert.Single(git.Commits);
    }

    [Fact]
    public async Task 过期审批不能执行()
    {
        using var host = new WorkbenchTestHost();
        var (tools, git, _, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo", "git"]);

        var first = await tools.CommitAsync(project.Id, "提交 A", waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        host.Clock.Advance(TimeSpan.FromHours(2));

        var second = await tools.CommitAsync(project.Id, "提交 A", approvalId: first.ApprovalId);

        Assert.False(second.Ok, "过期审批还能执行");
        Assert.Empty(git.Commits);
    }

    [Fact]
    public async Task 过期状态会真的落库()
    {
        using var host = new WorkbenchTestHost();

        var approval = await host.Approvals.RequestAsync(
            "shell_run", "shell.write", "echo x", "摘要", ttlMinutes: 1);

        host.Clock.Advance(TimeSpan.FromMinutes(2));

        var dto = await host.Approvals.GetAsync(approval.Id);
        Assert.Equal("Expired", dto!.Status);

        // 关键：库里也得是 Expired，不能永远停在 Pending
        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Expired, row.Status);
    }

    [Fact]
    public async Task 待审列表里不会留着过期单子()
    {
        using var host = new WorkbenchTestHost();

        await host.Approvals.RequestAsync("shell_run", "shell.write", "echo x", "摘要", ttlMinutes: 1);
        host.Clock.Advance(TimeSpan.FromMinutes(2));

        var pending = await host.Approvals.ListAsync(ApprovalStatus.Pending);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task 两个人同时决定只有一个成功()
    {
        using var host = new WorkbenchTestHost();

        var approval = await host.Approvals.RequestAsync(
            "shell_run", "shell.write", "echo x", "摘要");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => Task.WhenAll(
                host.Approvals.DecideAsync(approval.Id, true, "甲"),
                host.Approvals.DecideAsync(approval.Id, false, "乙", "不行")));

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.NotEqual(ApprovalStatus.Pending, row.Status);
    }

    [Fact]
    public async Task 合法续跑能正常执行()
    {
        using var host = new WorkbenchTestHost();
        var (tools, git, _, projects) = BuildTools(host);

        var project = await projects.CreateAsync("P");
        await projects.UpdateConfigAsync(project.Id, repoPath: "/tmp", allowedCommands: ["echo", "git"]);

        var first = await tools.CommitAsync(project.Id, "提交 A", waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        var second = await tools.CommitAsync(project.Id, "提交 A", approvalId: first.ApprovalId);

        Assert.True(second.Ok, $"合法续跑应该成功，实际：{second.Message}");
        Assert.Single(git.Commits);
    }
}
