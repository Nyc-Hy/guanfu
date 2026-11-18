using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;

namespace Guanfu.Application;

/// <summary>
/// 应用层对数据访问的统一抽象。由 Infrastructure 的 EF Core DbContext 实现，
/// 保证状态机校验、审计、审批编排只有一处实现。
/// </summary>
public interface IAppDbContext
{
    DbSet<Workspace> Workspaces { get; }
    DbSet<Project> Projects { get; }
    DbSet<TaskItem> Tasks { get; }
    DbSet<StatusEvent> StatusEvents { get; }
    DbSet<Document> Documents { get; }
    DbSet<DocumentParse> DocumentParses { get; }
    DbSet<Plan> Plans { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<Milestone> Milestones { get; }
    DbSet<TestRun> TestRuns { get; }
    DbSet<Approval> Approvals { get; }
    DbSet<ToolCallAudit> ToolCallAudits { get; }
    DbSet<DbConnectionConfig> DbConnections { get; }
    DbSet<DbTableRule> DbTableRules { get; }
    DbSet<McpToolSetting> McpToolSettings { get; }
    DbSet<ReviewRound> ReviewRounds { get; }
    DbSet<ReviewItem> ReviewItems { get; }
    DbSet<TaskDependency> TaskDependencies { get; }
    DbSet<SchemaVersion> SchemaVersions { get; }
    DbSet<AgentSession> AgentSessions { get; }
    DbSet<DbCallRecord> DbCallRecords { get; }
    DbSet<ProjectSpec> ProjectSpecs { get; }
    DbSet<MilestoneSpec> MilestoneSpecs { get; }
    DbSet<WorkspaceSpec> WorkspaceSpecs { get; }
    DbSet<AccessCredential> AccessCredentials { get; }
    DbSet<SpecVerdict> SpecVerdicts { get; }
    DbSet<ArtifactRevision> ArtifactRevisions { get; }
    DbSet<DeliveryPackage> DeliveryPackages { get; }
    DbSet<Member> Members { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空变更跟踪。批量操作失败要重试时用 ——
    /// 不清掉的话，上一次失败留下的实体还在跟踪里，重试会一起提交。
    /// </summary>
    void ClearTracking();
}
