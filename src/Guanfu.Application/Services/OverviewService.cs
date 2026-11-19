using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>总览里的待审条目。</summary>
public sealed record OverviewApprovalDto(
    Guid Id,
    string ToolName,
    string Summary,
    string? ProjectName,
    DateTimeOffset CreatedAt);

/// <summary>总览里的在办任务。</summary>
public sealed record OverviewTaskDto(
    Guid Id,
    string? Key,
    string Title,
    string ProjectName,
    string Status,
    string? AgentId,
    DateTimeOffset? LastActivityAt,
    /// <summary>负责人（人名或 agent id）。</summary>
    string? Assignee = null);

/// <summary>总览里的最近数据库调用。</summary>
public sealed record OverviewDbCallDto(
    Guid Id,
    string ToolName,
    string? ProjectName,
    Guid? TaskId,
    string? TaskKey,
    string? Server,
    string? Database,
    string? Sql,
    string? Kind,
    string? Outcome,
    DateTimeOffset CreatedAt);

/// <summary>总览里的活动会话。</summary>
public sealed record OverviewSessionDto(
    Guid Id,
    string AgentId,
    string ProjectName,
    string RepoPath,
    Guid? TaskId,
    string? TaskKey,
    DateTimeOffset LastSeenAt);

/// <summary>
/// 首页总览。观复是给人看「现在什么在动」的地方 —— 不是给人看一堆表的。
/// </summary>
public sealed record OverviewDto(
    IReadOnlyList<OverviewApprovalDto> PendingApprovals,
    IReadOnlyList<OverviewTaskDto> ActiveTasks,
    /// <summary>「我负责的」—— 分派给我、以及还没人认领的。</summary>
    IReadOnlyList<OverviewTaskDto> MyTasks,
    IReadOnlyList<OverviewDbCallDto> RecentDbCalls,
    IReadOnlyList<OverviewSessionDto> ActiveSessions,
    IReadOnlyList<ProjectSummaryDto> Projects);

public interface IOverviewService
{
    Task<OverviewDto> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class OverviewService(IAppDbContext db, IClock clock,
    ICurrentMemberScope? currentMember = null) : IOverviewService
{
    /// <summary>会话多久没动静就算「不在活跃」。</summary>
    private static readonly TimeSpan SessionIdleAfter = TimeSpan.FromHours(2);


    public async Task<OverviewDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var projectNames = projects.ToDictionary(p => p.Id, p => p.Name);
        var repoPaths = projects.ToDictionary(p => p.Id, p => p.RepoPath);

        var pendingApprovals = await db.Approvals
            .AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending)
            .OrderByDescending(a => a.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        var approvals = pendingApprovals
            .Select(a => new OverviewApprovalDto(
                a.Id,
                a.ToolName,
                a.Summary,
                a.ProjectId is not null && projectNames.TryGetValue(a.ProjectId.Value, out var name)
                    ? name
                    : null,
                a.CreatedAt))
            .ToList();

        // 可见性：普通成员在总览里也只该看见自己的活 ——
        // 隔离不能只在列表页生效（总览也是一条看的路）。
        var scope = currentMember?.Current;

        var activeTasks = await db.Tasks
            .AsNoTracking()
            .VisibleTo(scope)
            .Where(t => !t.IsArchived &&
                        t.Status != TaskStatus.Done &&
                        t.Status != TaskStatus.Blocked &&
                        t.Status != TaskStatus.Cancelled)
            .OrderByDescending(t => t.LastActivityAt ?? t.UpdatedAt)
            .Take(12)
            .ToListAsync(cancellationToken);

        var tasks = activeTasks
            .Select(t => new OverviewTaskDto(
                t.Id,
                t.Key,
                t.Title,
                projectNames.TryGetValue(t.ProjectId, out var pname) ? pname : "—",
                t.Status.ToString(),
                t.ClaimedByAgentId,
                t.LastActivityAt,
                t.Assignee))
            .ToList();

        // 「我负责的」：分派给我的 + 还没人认领的（别人不领，我得能看见它）。
        List<OverviewTaskDto> myTasks = [];

        if (scope is not null)
        {
            var myRows = await db.Tasks
                .AsNoTracking()
                .Where(t => !t.IsArchived &&
                            t.Status != TaskStatus.Done &&
                            t.Status != TaskStatus.Cancelled &&
                            (t.AssigneeMemberId == scope.MemberId ||
                             (t.AssigneeMemberId == null && string.IsNullOrWhiteSpace(t.Assignee))))
                .OrderByDescending(t => t.LastActivityAt ?? t.UpdatedAt)
                .Take(12)
                .ToListAsync(cancellationToken);

            myTasks = myRows
                .Select(t => new OverviewTaskDto(
                    t.Id,
                    t.Key,
                    t.Title,
                    projectNames.TryGetValue(t.ProjectId, out var pname) ? pname : "—",
                    t.Status.ToString(),
                    t.ClaimedByAgentId,
                    t.LastActivityAt,
                    t.Assignee))
                .ToList();
        }

        var recentCalls = await db.DbCallRecords
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(12)
            .ToListAsync(cancellationToken);

        var callTaskIds = recentCalls.Where(c => c.TaskId is not null).Select(c => c.TaskId!.Value).ToList();

        var callTaskKeys = await db.Tasks
            .AsNoTracking()
            .Where(t => callTaskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key })
            .ToListAsync(cancellationToken);

        var callKeyByTask = callTaskKeys.ToDictionary(x => x.Id, x => x.Key);

        var calls = recentCalls
            .Select(c => new OverviewDbCallDto(
                c.Id,
                c.ToolName,
                projectNames.TryGetValue(c.ProjectId, out var pname) ? pname : "—",
                c.TaskId,
                c.TaskId is not null && callKeyByTask.TryGetValue(c.TaskId.Value, out var key) ? key : null,
                c.Server,
                c.Database,
                c.Sql,
                c.Kind,
                c.Outcome,
                c.CreatedAt))
            .ToList();

        var since = clock.UtcNow - SessionIdleAfter;

        var sessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.EndedAt == null && s.LastSeenAt >= since)
            .OrderByDescending(s => s.LastSeenAt)
            .Take(12)
            .ToListAsync(cancellationToken);

        var sessionTaskIds = sessions.Where(s => s.TaskId is not null).Select(s => s.TaskId!.Value).ToList();

        var sessionTaskKeys = await db.Tasks
            .AsNoTracking()
            .Where(t => sessionTaskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key })
            .ToListAsync(cancellationToken);

        var sessionKeyByTask = sessionTaskKeys.ToDictionary(x => x.Id, x => x.Key);

        var activeSessions = sessions
            .Select(s => new OverviewSessionDto(
                s.Id,
                s.AgentId,
                projectNames.TryGetValue(s.ProjectId, out var pname) ? pname : "—",
                s.RepoPath,
                s.TaskId,
                s.TaskId is not null && sessionKeyByTask.TryGetValue(s.TaskId.Value, out var key)
                    ? key
                    : null,
                s.LastSeenAt))
            .ToList();

        var projectSummaries = new List<ProjectSummaryDto>(projects.Count);
        foreach (var project in projects)
        {
            var count = await db.Tasks.CountAsync(t => t.ProjectId == project.Id, cancellationToken);
            projectSummaries.Add(new ProjectSummaryDto(
                project.Id,
                project.Name,
                project.Description,
                0,
                count,
                0,
                0,
                0,
                null,
                false,
                project.RepoPath,
                project.DefaultBranch,
                project.RepoRemote,
                project.WorkspaceId,
                project.RepoRoot));
        }

        return new OverviewDto(
            approvals,
            tasks,
            myTasks,
            calls,
            activeSessions,
            projectSummaries);
    }
}
