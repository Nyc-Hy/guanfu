using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application;

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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
