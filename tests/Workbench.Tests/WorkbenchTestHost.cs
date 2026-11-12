using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Infrastructure;
using Workbench.Mcp;

namespace Workbench.Tests;

/// <summary>用真实的 SQLite（内存）跑服务层测试，不用 InMemory provider —— 事务与约束语义一致。</summary>
public sealed class WorkbenchTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WorkbenchDbContext _db;
    private readonly ServiceProvider _provider;

    public WorkbenchTestHost()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new WorkbenchDbContext(options);
        _db.Database.EnsureCreated();

        // McpToolState 是单例，需要一个能开 scope 的容器来拿 IAppDbContext。
        // 每个 scope 给一个**新的** context（共用同一条内存连接）：
        // scope 结束会 dispose 掉它，注册同一个实例会被提前 dispose。
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => new WorkbenchDbContext(options));
        _provider = services.BuildServiceProvider();

        Clock = new TestClock(DateTimeOffset.UtcNow);
    }

    public TestClock Clock { get; }
    public WorkbenchDbContext Db => _db;
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();
    public McpToolState ToolState => new(ScopeFactory);

    public ProjectService Projects => new(_db, Clock);
    public TaskService Tasks => new(_db, Clock, new NullGitInspector());
    public ApprovalService Approvals => new(_db, Clock);
    public MilestoneService Milestones => new(_db, Clock);
    public MetricsService Metrics => new(_db, Clock);
    public FileSystemService Files => new(_db);

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    public sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class NullGitInspector : IGitInspector
    {
        public GitState Inspect(string repoPath) => new(null, null, false, 0, "测试环境无 git");
    }
}
