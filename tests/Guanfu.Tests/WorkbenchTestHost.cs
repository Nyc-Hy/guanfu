using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Infrastructure;
using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>用真实的 SQLite（内存）跑服务层测试，不用 InMemory provider —— 事务与约束语义一致。</summary>
public sealed class GuanfuTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly GuanfuDbContext _db;
    private readonly DbContextOptions<GuanfuDbContext> _options;
    private readonly List<GuanfuDbContext> _extraContexts = new();
    private readonly ServiceProvider _provider;

    public GuanfuTestHost()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<GuanfuDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new GuanfuDbContext(_options);
        _db.Database.EnsureCreated();

        // McpToolState 是单例，需要一个能开 scope 的容器来拿 IAppDbContext。
        // 每个 scope 给一个**新的** context（共用同一条内存连接）：
        // scope 结束会 dispose 掉它，注册同一个实例会被提前 dispose。
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => new GuanfuDbContext(_options));
        _provider = services.BuildServiceProvider();

        Clock = new TestClock(DateTimeOffset.UtcNow);

        Git = new FakeGitInspector();
        Reviews = new ReviewService(_db, Clock, Git, Specs);
        Tasks = new TaskService(_db, Clock, Git, Reviews);
    }

    public TestClock Clock { get; }
    public GuanfuDbContext Db => _db;
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();
    public McpToolState ToolState => new(ScopeFactory);

    public ProjectService Projects => new(_db, Clock);

    /// <summary>任务服务带评审门禁 —— 评审不过不许结项这条规则要真的生效。</summary>
    public TaskService Tasks { get; }

    /// <summary>
    /// 审批服务每次一个新 DbContext。
    ///
    /// 生产里每个 HTTP 请求一个 scope、一个 DbContext；测试里全共用 _db 的话，
    /// 「等审批」的轮询和「裁决」的写会撞在同一个 context 上 ——
    /// "A second operation was started on this context instance"。
    /// 这里按生产的语义拆开：轮询与裁决各用各的 context。
    /// </summary>
    public ApprovalService Approvals => new(NewContext(), Clock);

    private GuanfuDbContext NewContext()
    {
        var context = new GuanfuDbContext(_options);
        _extraContexts.Add(context);
        return context;
    }

    /// <summary>写操作闸门 —— 四个写工具共用这一套。</summary>
    public ApprovalGate Gate => new(Approvals);

    public ToolAuditService Audit => new(_db, Clock);

    public ReviewService Reviews { get; }

    /// <summary>可改的 git 状态（默认「测试环境无 git」）。</summary>
    public FakeGitInspector Git { get; }
    /// <summary>项目级规约服务。</summary>
    public ProjectSpecService Specs => new(_db, Clock);

    public MilestoneService Milestones => new(_db, Clock, Specs);
    public MetricsService Metrics => new(_db, Clock);
    public FileSystemService Files => new(_db);
    public TaskDependencyService Dependencies => new(_db, Clock);

    /// <summary>会话服务（绑定链路）。</summary>
    public AgentSessionService Sessions => new(_db, Clock);
    public ExportService Exports => new(_db, Reviews);

    /// <summary>数据库连接服务。密码保护器用假的 —— 测试里不需要真的加密。</summary>
    public DbConnectionService DbConnections => new(_db, new FakeSecretProtector(), DbProbe);

    /// <summary>可编程的连接探活器（试连成功/失败都能造）。</summary>
    public FakeDbConnectionProbe DbProbe { get; } = new();

    public void Dispose()
    {
        _provider.Dispose();
        foreach (var context in _extraContexts)
        {
            context.Dispose();
        }

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

        /// <summary>HEAD 的 sha。评审轮次绑 commit 的测试要能改它（默认值保持既有断言不变）。</summary>
        public string? HeadSha { get; set; } = "abc123";

        /// <summary>待提交内容指纹。默认 null = 读不到，调用方必须按拒绝处理。</summary>
        public string? PendingCommitFingerprint { get; set; }

        /// <summary>某个 commit 改动的文件。产出文件采集的测试要能改它。</summary>
        public List<GitFileChange> CommitFileChanges { get; set; } = [];

        /// <summary>工作区里改动的文件。</summary>
        public List<GitFileChange> WorkingTreeFileChanges { get; set; } = [];

        public GitState Inspect(string repoPath) =>
            new(Branch, HeadSha, false, 0, Error);

        public string? GetPendingCommitFingerprint(string repoPath) => PendingCommitFingerprint;

        public IReadOnlyList<GitFileChange> GetCommitFileChanges(string repoPath, string commitSha) =>
            CommitFileChanges;

        public IReadOnlyList<GitFileChange> GetWorkingTreeFileChanges(string repoPath) =>
            WorkingTreeFileChanges;

        /// <summary>读工作区时的错误（模拟「仓库读不到」）。</summary>
        public string? WorkingTreeError { get; set; }

        public GitWorkingTreeResult GetWorkingTreeChanges(string repoPath) =>
            WorkingTreeError is null
                ? new GitWorkingTreeResult(WorkingTreeFileChanges)
                : new GitWorkingTreeResult([], Failed: true, Error: WorkingTreeError);

        public string? GetHeadSha(string repoPath) => HeadSha;

        /// <summary>仓库里的 worktree（默认只有主 checkout）。</summary>
        public List<GitWorktree> Worktrees { get; set; } = [];

        public IReadOnlyList<GitWorktree> ListWorktrees(string repoPath) => Worktrees;
    }

    /// <summary>假连接探活器：默认连通，可以设成抛异常来验证失败路径。</summary>
    public sealed class FakeDbConnectionProbe : IDbConnectionProbe
    {
        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public DbConnectionConfig? LastConfig { get; private set; }

        public Task ProbeAsync(DbConnectionConfig config, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastConfig = config;
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    /// <summary>假密钥保护器。测试里不需要真的加密，只关心「密码存进去了」。</summary>
    public sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;
    }
}
