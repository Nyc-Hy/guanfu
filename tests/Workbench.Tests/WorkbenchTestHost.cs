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

        Git = new FakeGitInspector();
        Reviews = new ReviewService(_db, Clock, Git);
        BranchGuard = new BranchGuard(_db, Git);
        Tasks = new TaskService(_db, Clock, Git, Reviews, BranchGuard);
    }

    public TestClock Clock { get; }
    public WorkbenchDbContext Db => _db;
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();
    public McpToolState ToolState => new(ScopeFactory);

    public ProjectService Projects => new(_db, Clock);

    /// <summary>任务服务带评审门禁 —— 评审不过不许结项这条规则要真的生效。</summary>
    public TaskService Tasks { get; }

    public ApprovalService Approvals => new(_db, Clock);

    /// <summary>写操作闸门 —— 四个写工具共用这一套。</summary>
    public ApprovalGate Gate => new(Approvals);

    public ToolAuditService Audit => new(_db, Clock);

    public ReviewService Reviews { get; }

    /// <summary>分支守卫。测试里可以换成假的 git 状态。</summary>
    public BranchGuard BranchGuard { get; }

    /// <summary>可改的 git 状态（默认「测试环境无 git」）。</summary>
    public FakeGitInspector Git { get; }
    public MilestoneService Milestones => new(_db, Clock);
    public MetricsService Metrics => new(_db, Clock);
    public FileSystemService Files => new(_db);

    /// <summary>数据库连接服务。密码保护器用假的 —— 测试里不需要真的加密。</summary>
    public DbConnectionService DbConnections => new(_db, new FakeSecretProtector());

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

    /// <summary>可编程的 git 状态：分支、脏标记都能随便设。</summary>
    public sealed class FakeGitInspector : IGitInspector
    {
        public string? Branch { get; set; }

        public string? Error { get; set; }

        public GitState Inspect(string repoPath) =>
            new(Branch, "abc123", false, 0, Error);
    }

    /// <summary>假密钥保护器。测试里不需要真的加密，只关心「密码存进去了」。</summary>
    public sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;
    }
}
