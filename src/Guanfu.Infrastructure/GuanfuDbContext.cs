using Microsoft.EntityFrameworkCore;
using Guanfu.Application;
using Guanfu.Domain;

namespace Guanfu.Infrastructure;

public sealed class GuanfuDbContext(DbContextOptions<GuanfuDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<StatusEvent> StatusEvents => Set<StatusEvent>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentParse> DocumentParses => Set<DocumentParse>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ToolCallAudit> ToolCallAudits => Set<ToolCallAudit>();
    public DbSet<DbConnectionConfig> DbConnections => Set<DbConnectionConfig>();
    public DbSet<DbTableRule> DbTableRules => Set<DbTableRule>();
    public DbSet<McpToolSetting> McpToolSettings => Set<McpToolSetting>();
    public DbSet<ReviewRound> ReviewRounds => Set<ReviewRound>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<TaskFileChange> TaskFileChanges => Set<TaskFileChange>();
    public DbSet<DbCallRecord> DbCallRecords => Set<DbCallRecord>();
    public DbSet<ProjectSpec> ProjectSpecs => Set<ProjectSpec>();
    public DbSet<MilestoneSpec> MilestoneSpecs => Set<MilestoneSpec>();
    public DbSet<WorkspaceSpec> WorkspaceSpecs => Set<WorkspaceSpec>();

    public DbSet<AccessCredential> AccessCredentials => Set<AccessCredential>();
    public DbSet<SpecVerdict> SpecVerdicts => Set<SpecVerdict>();
    public DbSet<ArtifactRevision> ArtifactRevisions => Set<ArtifactRevision>();
    public DbSet<DeliveryPackage> DeliveryPackages => Set<DeliveryPackage>();
    public DbSet<Member> Members => Set<Member>();

    /// <inheritdoc />
    public void ClearTracking() => ChangeTracker.Clear();

    /// <summary>
    /// 保存前把编号的归一化列补上。
    ///
    /// 这一列是「同一个项目里不能有两个归一化后相同的编号」的唯一索引的载体，
    /// 补不上就等于那条索引形同虚设。
    /// 在这里统一补，而不是指望每条写入路径都记得填 ——
    /// 少填一处就是一条能悄悄插进来的重号。
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        FillKeyNormalized();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        FillKeyNormalized();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void FillKeyNormalized()
    {
        foreach (var entry in ChangeTracker.Entries<TaskItem>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.KeyNormalized = entry.Entity.Key is null
                    ? null
                    : TaskKeys.Normalize(entry.Entity.Key);
            }
        }
    }

    /// <summary>
    /// 全局约定：DateTimeOffset 一律以 UTC DateTime 落库。
    /// SQLite 不能对 DateTimeOffset 列排序，不转换的话所有按时间排序的查询都会炸。
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();

        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<NullableUtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {        modelBuilder.Entity<Workspace>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);

            // 列名沿用改名前的 —— 改名不该让老库多出一列、数据留在旧列里
            e.Property(x => x.ManagedByGuanfu).HasColumnName("ManagedByWorkbench");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.RepoPath).HasMaxLength(1000);
            e.Property(x => x.RepoRoot).HasMaxLength(1000);
            e.Property(x => x.RepoRemote).HasMaxLength(500);
            e.Property(x => x.DefaultBranch).HasMaxLength(200);

            // 仓库绑定键。索引是为了反查「这个目录归哪个项目」时不用全表扫 ——
            // agent 每次开工都要查一次。
            e.HasIndex(x => x.RepoRoot);

            e.HasOne(x => x.Workspace)
                .WithMany(w => w.Projects)
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(x => x.Tasks)
                .WithOne(t => t.Project!)
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ClaimedByAgentId).HasMaxLength(200);
            e.Property(x => x.Assignee).HasMaxLength(200);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(32);

            e.Property(x => x.Key).HasMaxLength(50);
            e.Property(x => x.KeyNormalized).HasMaxLength(50);
            e.Property(x => x.IsArchived).HasDefaultValue(false);

            e.HasIndex(t => t.ProjectId);
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.ParentTaskId);
            e.HasIndex(t => t.MilestoneId);
            e.HasIndex(t => t.Branch);
            e.HasIndex(t => t.Assignee);

            // 编号在项目内唯一。这条索引是编号并发分配的兜底 ——
            // 内存里的 check-then-act 挡不住两个并发请求算出同一个号。
            // Key 可空，SQLite 的唯一索引不约束多个 NULL，老数据不受影响。
            e.HasIndex(t => new { t.ProjectId, t.Key }).IsUnique();

            // 归一化编号的唯一索引：把「req-001」和「REQ-001」也当成同一个号。
            // 上一条索引按原字符串比，拦不住大小写不同的重号；
            // 这一条按业务口径比，重号在数据库这一层就被挡住。
            // KeyNormalized 可空 → 唯一索引不约束多个 NULL，老数据补列后不受影响。
            e.HasIndex(t => new { t.ProjectId, t.KeyNormalized }).IsUnique();
        });

        modelBuilder.Entity<StatusEvent>(e =>
        {
            e.ToTable("status_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(4000);
            e.Property(x => x.EventType).HasMaxLength(100);
            e.Property(x => x.Payload).HasMaxLength(16000);

            e.HasOne(x => x.Task)
                .WithMany(t => t.StatusEvents)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.StoredPath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.Format).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(50);
            e.Property(x => x.ParseError).HasMaxLength(2000);

            e.HasOne(x => x.Task)
                .WithMany(t => t.Documents)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentParse>(e =>
        {
            e.ToTable("document_parses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Format).HasConversion<string>().HasMaxLength(32);

            e.HasOne(x => x.Document)
                .WithMany()
                .HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.DocumentId);
        });

        modelBuilder.Entity<Plan>(e =>
        {
            e.ToTable("plans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Background).HasMaxLength(16000);
            e.Property(x => x.Understanding).HasMaxLength(16000);
            e.Property(x => x.Breakdown).HasMaxLength(16000);
            e.Property(x => x.AcceptanceCriteria).HasMaxLength(16000);
            e.Property(x => x.Risks).HasMaxLength(16000);
            e.Property(x => x.Author).HasMaxLength(200);

            e.HasOne(x => x.Task)
                .WithMany(t => t.Plans)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // 同一任务的计划版本号唯一 —— 版本重试（并发保存）会算出同一个 Version，
            // 这条索引是兜底。老库已有重复数据时 SchemaUpgrader 会跳过并记警告。
            e.HasIndex(x => new { x.TaskId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<Artifact>(e =>
        {
            e.ToTable("artifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Content).HasMaxLength(32000);
            e.Property(x => x.ExternalPath).HasMaxLength(1000);
            e.Property(x => x.CommitSha).HasMaxLength(64);
            e.Property(x => x.Branch).HasMaxLength(200);
            e.Property(x => x.Author).HasMaxLength(200);
            e.Property(x => x.EditedBy).HasMaxLength(200);

            e.HasOne(x => x.Task)
                .WithMany(t => t.Artifacts)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 产物的版本历史：改了也不许抹掉原来那版。
        modelBuilder.Entity<ArtifactRevision>(e =>
        {
            e.ToTable("artifact_revisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Content).HasMaxLength(32000);
            e.Property(x => x.Editor).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(1000);

            e.HasOne(x => x.Artifact)
                .WithMany()
                .HasForeignKey(x => x.ArtifactId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ArtifactId, x.Version });
        });

        // 交付包：一次打包的记录（包 + 清单）。
        modelBuilder.Entity<DeliveryPackage>(e =>
        {
            e.ToTable("delivery_packages");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.StoredPath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Items).HasMaxLength(32000);
            e.Property(x => x.Manifest).HasMaxLength(32000);
            e.Property(x => x.CreatedBy).HasMaxLength(200);

            e.HasIndex(x => new { x.MilestoneId, x.CreatedAt });
        });

        // 团队成员：一个人用的时候这张表是空的，团队用的时候「谁在操作」靠它。
        modelBuilder.Entity<Member>(e =>
        {
            e.ToTable("members");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserName).HasMaxLength(100).IsRequired();
            e.Property(x => x.UserNameNormalized).HasMaxLength(100).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.PasswordHash).HasMaxLength(500);

            // 登录名唯一 —— 建在**归一化**的那一列上：
            // 「ZhangSan」与「zhangsan」是同一个登录名。
            e.HasIndex(x => x.UserNameNormalized).IsUnique();
        });

        // 凭证归属成员：成员被停用，名下的凭证跟着作废。
        // 表名沿用约定（AccessCredentials）—— 改名会让老库多出一张空表。
        modelBuilder.Entity<AccessCredential>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Member)
                .WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.MemberId);
        });

        // 项目级规约：项目与规约是一对一。规约是「人定的规矩」，
        // 跟项目一起走（项目没了，规约也没了）。
        modelBuilder.Entity<ProjectSpec>(e =>
        {
            e.ToTable("project_specs");
            e.HasKey(x => x.ProjectId);
            e.HasOne<Project>()
                .WithOne(p => p.Spec)
                .HasForeignKey<ProjectSpec>(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.Purpose).HasMaxLength(4000);
            e.Property(x => x.DefinitionOfDone).HasMaxLength(8000);
            e.Property(x => x.CommitConvention).HasMaxLength(1000);
            e.Property(x => x.TestCommand).HasMaxLength(1000);
            e.Property(x => x.BranchConvention).HasMaxLength(500);
            e.Property(x => x.Guardrails).HasMaxLength(4000);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
        });

        // 工作区级规约：所有项目通用的底线
        modelBuilder.Entity<WorkspaceSpec>(e =>
        {
            e.ToTable("workspace_specs");
            e.HasKey(x => x.WorkspaceId);
            e.HasOne<Workspace>()
                .WithOne(w => w.Spec)
                .HasForeignKey<WorkspaceSpec>(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.Purpose).HasMaxLength(4000);
            e.Property(x => x.DefinitionOfDone).HasMaxLength(8000);
            e.Property(x => x.CommitConvention).HasMaxLength(1000);
            e.Property(x => x.TestCommand).HasMaxLength(1000);
            e.Property(x => x.BranchConvention).HasMaxLength(500);
            e.Property(x => x.Guardrails).HasMaxLength(4000);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
        });

        // 里程碑级规约：与项目级规约叠加，但各是各的一张表
        // （混在一张表里，「项目级只有一份」在 NULL 语义下就说不清了）。
        modelBuilder.Entity<MilestoneSpec>(e =>
        {
            e.ToTable("milestone_specs");
            e.HasKey(x => x.MilestoneId);
            e.HasOne<Milestone>()
                .WithOne(m => m.Spec)
                .HasForeignKey<MilestoneSpec>(x => x.MilestoneId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.Purpose).HasMaxLength(4000);
            e.Property(x => x.DefinitionOfDone).HasMaxLength(8000);
            e.Property(x => x.CommitConvention).HasMaxLength(1000);
            e.Property(x => x.TestCommand).HasMaxLength(1000);
            e.Property(x => x.Guardrails).HasMaxLength(4000);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
        });

        modelBuilder.Entity<Milestone>(e =>
        {
            e.ToTable("milestones");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);

            e.HasOne(x => x.Project)
                .WithMany(p => p.Milestones)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // 父里程碑。用 Restrict 而不是 Cascade：删掉一个版本不该把
            // 底下的需求组连带删掉 —— 那些组里还有任务。
            e.HasOne(x => x.Parent)
                .WithMany(m => m.Children)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.ProjectId, x.Order });
            e.HasIndex(x => x.ParentId);
        });

        modelBuilder.Entity<TaskDependency>(e =>
        {
            e.ToTable("task_dependencies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(1000);

            e.HasOne(x => x.Task)
                .WithMany(t => t.Dependencies)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // 前置任务被删了，依赖关系必须跟着消失 —— 留着就是指向空气的依赖
            e.HasOne(x => x.DependsOn)
                .WithMany()
                .HasForeignKey(x => x.DependsOnTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // 同一对任务只能有一条依赖
            e.HasIndex(x => new { x.TaskId, x.DependsOnTaskId }).IsUnique();
            e.HasIndex(x => x.DependsOnTaskId);
        });

        modelBuilder.Entity<SchemaVersion>(e =>
        {
            e.ToTable("schema_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Note).HasMaxLength(4000);

            e.HasIndex(x => x.Fingerprint);
        });

        modelBuilder.Entity<TestRun>(e =>
        {
            e.ToTable("test_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Failures).HasMaxLength(32000);
            e.Property(x => x.Author).HasMaxLength(200);

            e.HasOne(x => x.Task)
                .WithMany(t => t.TestRuns)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.TaskId, x.ReportedAt });
        });

        modelBuilder.Entity<Approval>(e =>
        {
            e.ToTable("approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).HasMaxLength(16000);
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RequestedBy).HasMaxLength(200);
            e.Property(x => x.DecidedBy).HasMaxLength(200);
            e.Property(x => x.DecidedByCredential).HasMaxLength(64);
            e.Property(x => x.DecisionReason).HasMaxLength(2000);
            e.Property(x => x.ResultSummary).HasMaxLength(4000);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<ToolCallAudit>(e =>
        {
            e.ToTable("tool_call_audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(200).IsRequired();
            e.Property(x => x.CallerId).HasMaxLength(200);
            e.Property(x => x.Arguments).HasMaxLength(16000);
            e.Property(x => x.Outcome).HasMaxLength(50);
            e.Property(x => x.ResultSummary).HasMaxLength(4000);
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);

            // 审计表原来一个索引都没有 —— 查一次调用记录就是全表扫
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ToolName);
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.Hash);
            e.HasIndex(x => x.Sequence);
        });

        modelBuilder.Entity<DbConnectionConfig>(e =>
        {
            e.ToTable("db_connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Server).HasMaxLength(500).IsRequired();
            e.Property(x => x.Database).HasMaxLength(200).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.EncryptedPassword).HasMaxLength(2000);

            e.HasOne(x => x.Project)
                .WithMany(p => p.DbConnections)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReviewRound>(e =>
        {
            e.ToTable("review_rounds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Verdict).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Summary).HasMaxLength(8000);
            e.Property(x => x.Reviewer).HasMaxLength(200);

            e.HasOne(x => x.Task)
                .WithMany(t => t.ReviewRounds)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // 同一任务的轮次号不能重复 —— 并发开轮时两个请求会算出同一个号，
            // 门禁取轮顺序就会不稳，可能判错轮
            e.HasIndex(x => new { x.TaskId, x.Round }).IsUnique();
        });

        modelBuilder.Entity<ReviewItem>(e =>
        {
            e.ToTable("review_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Dimension).HasMaxLength(100).IsRequired();
            e.Property(x => x.DimensionName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Findings).HasMaxLength(8000);
            e.Property(x => x.Evidence).HasMaxLength(8000);
            e.Property(x => x.Recommendation).HasMaxLength(8000);
            e.Property(x => x.Reviewer).HasMaxLength(200);

            e.HasOne(x => x.Round)
                .WithMany(r => r.Items)
                .HasForeignKey(x => x.ReviewRoundId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ReviewRoundId, x.Order });
            e.HasIndex(x => x.TaskId);
        });

        modelBuilder.Entity<McpToolSetting>(e =>
        {
            e.ToTable("mcp_tool_settings");
            e.HasKey(x => x.ToolName);
            e.Property(x => x.ToolName).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<DbTableRule>(e =>
        {
            e.ToTable("db_table_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.SchemaName).HasMaxLength(128).IsRequired();
            e.Property(x => x.TableName).HasMaxLength(256).IsRequired();

            e.HasOne(x => x.Connection)
                .WithMany(c => c.Rules)
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSession>(e =>
        {
            e.ToTable("agent_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentId).HasMaxLength(200).IsRequired();
            e.Property(x => x.RepoPath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.McpSessionId).HasMaxLength(200);

            e.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Task)
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.LastSeenAt);

            // 同一个 MCP 协议会话只对应一条 agent 会话。
            // 协议会话 id 为空的行不参与唯一性判断（SQLite 的唯一索引不约束多个 NULL）。
            e.HasIndex(x => x.McpSessionId).IsUnique();
        });

        modelBuilder.Entity<TaskFileChange>(e =>
        {
            e.ToTable("task_file_changes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ChangeKind).HasMaxLength(32);
            e.Property(x => x.OldPath).HasMaxLength(1000);
            e.Property(x => x.CommitSha).HasMaxLength(64);
            e.Property(x => x.Branch).HasMaxLength(200);
            e.Property(x => x.Author).HasMaxLength(200);

            e.HasOne(x => x.Task)
                .WithMany(t => t.FileChanges)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Session)
                .WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.CommitSha);
        });

        modelBuilder.Entity<DbCallRecord>(e =>
        {
            e.ToTable("db_call_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Server).HasMaxLength(500);
            e.Property(x => x.Database).HasMaxLength(200);
            e.Property(x => x.Sql).HasMaxLength(16000);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.TargetTables).HasMaxLength(2000);
            e.Property(x => x.Outcome).HasMaxLength(50);
            e.Property(x => x.CallerId).HasMaxLength(200);

            e.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Task)
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Task)
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.SetNull);

            // 查库调用记录最常见的三个问法：某个项目干了什么、某个库被碰过没有、
            // 某张表被哪些任务改过。索引就照这三个问法建。
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => x.ConnectionId);
            e.HasIndex(x => x.TargetTables);
            e.HasIndex(x => x.TaskId);
        });
    }
}
