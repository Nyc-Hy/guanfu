using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Domain;

namespace Workbench.Infrastructure;

public sealed class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options) : DbContext(options), IAppDbContext
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
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.RepoPath).HasMaxLength(1000);
            e.Property(x => x.RepoRemote).HasMaxLength(500);
            e.Property(x => x.DefaultBranch).HasMaxLength(200);

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

            e.HasIndex(t => t.ProjectId);
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.ParentTaskId);
        });

        modelBuilder.Entity<StatusEvent>(e =>
        {
            e.ToTable("status_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(4000);

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

            e.HasIndex(x => new { x.TaskId, x.Version });
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

            e.HasOne(x => x.Task)
                .WithMany(t => t.Artifacts)
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Milestone>(e =>
        {
            e.ToTable("milestones");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);

            e.HasOne(x => x.Project)
                .WithMany(p => p.Milestones)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ProjectId, x.Order });
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
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RequestedBy).HasMaxLength(200);
            e.Property(x => x.DecidedBy).HasMaxLength(200);
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
    }
}
