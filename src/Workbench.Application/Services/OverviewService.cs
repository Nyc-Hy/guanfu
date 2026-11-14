using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

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
    string? Branch,
    DateTimeOffset? LastActivityAt);

/// <summary>总览里的最近产出文件。</summary>
public sealed record OverviewFileChangeDto(
    Guid TaskId,
    string? TaskKey,
    string ProjectName,
    string Path,
    string ChangeKind,
    int LinesAdded,
    int LinesDeleted,
    string? CommitSha,
    DateTimeOffset CreatedAt);

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
/// 首页总览。工作台是给人看「现在什么在动」的地方 —— 不是给人看一堆表的。
/// </summary>
public sealed record OverviewDto(
    IReadOnlyList<OverviewApprovalDto> PendingApprovals,
    IReadOnlyList<OverviewTaskDto> ActiveTasks,
    IReadOnlyList<OverviewFileChangeDto> RecentFiles,
    IReadOnlyList<OverviewDbCallDto> RecentDbCalls,
    IReadOnlyList<OverviewSessionDto> ActiveSessions,
    IReadOnlyList<ProjectSummaryDto> Projects);

public interface IOverviewService
{
    Task<OverviewDto> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class OverviewService(IAppDbContext db, IClock clock) : IOverviewService
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

        var activeTasks = await db.Tasks
            .AsNoTracking()
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
                t.Branch,
                t.LastActivityAt))
            .ToList();

        var recentFiles = await db.TaskFileChanges
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(12)
            .ToListAsync(cancellationToken);

        var taskKeys = await db.Tasks
            .AsNoTracking()
            .Where(t => recentFiles.Select(c => c.TaskId).Contains(t.Id))
            .Select(t => new { t.Id, t.Key })
            .ToListAsync(cancellationToken);

        var keyByTask = taskKeys.ToDictionary(x => x.Id, x => x.Key);

        var files = recentFiles
            .Select(c => new OverviewFileChangeDto(
                c.TaskId,
                keyByTask.TryGetValue(c.TaskId, out var key) ? key : null,
                projectNames.TryGetValue(c.ProjectId, out var pname) ? pname : "—",
                c.Path,
                c.ChangeKind,
                c.LinesAdded,
                c.LinesDeleted,
                c.CommitSha,
                c.CreatedAt))
            .ToList();

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
                project.RepoRoot));
        }

        return new OverviewDto(
            approvals,
            tasks,
            files,
            calls,
            activeSessions,
            projectSummaries);
    }
}
