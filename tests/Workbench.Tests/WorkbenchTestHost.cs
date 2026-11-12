using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>用真实的 SQLite（内存）跑服务层测试，不用 InMemory provider —— 事务与约束语义一致。</summary>
public sealed class WorkbenchTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WorkbenchDbContext _db;

    public WorkbenchTestHost()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new WorkbenchDbContext(options);
        _db.Database.EnsureCreated();

        Clock = new TestClock(DateTimeOffset.UtcNow);
    }

    public TestClock Clock { get; }
    public WorkbenchDbContext Db => _db;

    public ProjectService Projects => new(_db, Clock);
    public TaskService Tasks => new(_db, Clock, new NullGitInspector());
    public ApprovalService Approvals => new(_db, Clock);
    public MilestoneService Milestones => new(_db, Clock);
    public MetricsService Metrics => new(_db, Clock);
    public FileSystemService Files => new(_db);

    public void Dispose()
    {
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
