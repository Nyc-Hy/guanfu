using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 外部 agent 拉起：日志单 writer + 机器可读事件。
///
/// 回归点：以前 stdout/stderr 两路各开一个 StreamWriter 写同一文件，
/// Windows 上第二个必抛 IOException 被吞 —— 该路输出全丢，管道写满后
/// 子进程阻塞在 write 上，整个 agent 死锁。现在两路泵入 Channel，
/// 单消费循环写盘，进程退出后 drain 完再关。
/// </summary>
public class AgentLauncherTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "wb-launch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 双路输出都落盘_事件带类型与结构化载荷()
    {
        if (OperatingSystem.IsWindows())
        {
            return;  // 用 /bin/sh 造双路输出，CI 只跑 ubuntu/macos
        }

        var (launcher, db, task, scopeFactory) = await SetupAsync(
            "/bin/sh -c \"echo stdout-line; echo stderr-line 1>&2\"");

        var result = await launcher.LaunchAsync(task.Id);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.ProcessId);

        var exited = await WaitForEventAsync(db, TaskEventTypes.AgentExited);

        // 两路输出都在日志里，各带 tag —— 丢任何一路都是回归
        var log = await ReadRunLogAsync(task.Id);
        Assert.Contains("[stdout] stdout-line", log);
        Assert.Contains("[stderr] stderr-line", log);

        // 启动事件：EventType + {pid,command}（规矩 20：机器按 EventType 判定，不猜 Note）
        var launched = await db.StatusEvents.AsNoTracking()
            .FirstAsync(e => e.EventType == TaskEventTypes.AgentLaunched);
        Assert.NotNull(launched.Payload);
        using (var payload = JsonDocument.Parse(launched.Payload!))
        {
            Assert.Equal(result.ProcessId, payload.RootElement.GetProperty("pid").GetInt32());
            Assert.Contains("/bin/sh", payload.RootElement.GetProperty("command").GetString());
        }

        // 退出事件：{exitCode}
        Assert.NotNull(exited.Payload);
        using (var payload = JsonDocument.Parse(exited.Payload!))
        {
            Assert.Equal(0, payload.RootElement.GetProperty("exitCode").GetInt32());
        }

        // 运行日志产物也还在
        Assert.True(await db.Artifacts.AsNoTracking().AnyAsync(a => a.TaskId == task.Id));
    }

    [Fact]
    public async Task 非零退出码记进事件载荷()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (launcher, db, task, _) = await SetupAsync("/bin/sh -c \"exit 3\"");

        var result = await launcher.LaunchAsync(task.Id);
        Assert.True(result.Ok, result.Error);

        var exited = await WaitForEventAsync(db, TaskEventTypes.AgentExited);
        using var payload = JsonDocument.Parse(exited.Payload!);
        Assert.Equal(3, payload.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task 大量双路输出不死锁且尾部完整()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 两路各 5000 行，远超管道缓冲 —— 没人读就会写满死锁。
        // 退出事件出现时日志必须已 drain 完（最后一行在）。
        var (launcher, db, task, _) = await SetupAsync(
            "/bin/sh -c \"i=0; while [ $i -lt 5000 ]; do echo out-$i; echo err-$i 1>&2; i=$((i+1)); done\"");

        var result = await launcher.LaunchAsync(task.Id);
        Assert.True(result.Ok, result.Error);

        await WaitForEventAsync(db, TaskEventTypes.AgentExited);

        var log = await ReadRunLogAsync(task.Id);
        Assert.Contains("[stdout] out-4999", log);
        Assert.Contains("[stderr] err-4999", log);
    }

    // ---------- 辅助 ----------

    private async Task<(AgentLauncher Launcher, WorkbenchDbContext Db, TaskItem Task, IServiceScopeFactory ScopeFactory)>
        SetupAsync(string agentCommand)
    {
        Directory.CreateDirectory(_home);

        // 文件库：后台 WatchAsync 用独立 scope 开自己的连接，避免与测试查询挤同一条连接
        var dbPath = Path.Combine(_home, "workbench.db");
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        var db = new WorkbenchDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var workspace = new Workspace { Name = "w" };
        var project = new Project
        {
            Workspace = workspace,
            Name = "p",
            AgentCommand = agentCommand
            // RepoPath 留空 → 工作目录落在 WORKBENCH_HOME
        };
        var task = new TaskItem { Project = project, Title = "t" };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new WorkbenchDbContext(options));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var launcher = new AgentLauncher(
            db,
            new FixedClock(DateTimeOffset.UtcNow),
            scopeFactory,
            Options.Create(new WorkbenchOptions { HomeDirectory = _home }),
            new BranchGuard(db, new NoGit()));

        return (launcher, db, task, scopeFactory);
    }

    private static async Task<StatusEvent> WaitForEventAsync(WorkbenchDbContext db, string eventType)
    {
        for (var i = 0; i < 200; i++)
        {
            var found = await db.StatusEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.EventType == eventType);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"等不到事件 {eventType}（20 秒）。");
    }

    private async Task<string> ReadRunLogAsync(Guid taskId)
    {
        var runDir = Path.Combine(_home, "runs", taskId.ToString("N"));
        var files = Directory.GetFiles(runDir, "*.log");
        Assert.Single(files);
        return await File.ReadAllTextAsync(files[0]);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>任务不绑分支，分支守卫直接放行，用不到 git。</summary>
    private sealed class NoGit : IGitInspector
    {
        public GitState Inspect(string repoPath) => new(null, null, false, 0, "测试环境无 git");

        public string? GetPendingCommitFingerprint(string repoPath) => null;

        public IReadOnlyList<GitFileChange> GetCommitFileChanges(string repoPath, string commitSha) => [];

        public IReadOnlyList<GitFileChange> GetWorkingTreeFileChanges(string repoPath) => [];

        public string? GetHeadSha(string repoPath) => null;

        public IReadOnlyList<GitWorktree> ListWorktrees(string repoPath) => [];
    }
}