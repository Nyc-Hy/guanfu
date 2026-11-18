using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 任务的产出文件采集。
///
/// 核心立场：**观复自己去 git 里数出来**，不是 agent 说它改了什么。
/// agent 自觉调 <c>guanfu_add_artifact</c> 登记是另一回事（那是自述），
/// 这里是事实 —— 采集点在 agent 绕不过去的地方（提交之后、进评审之前）。
/// </summary>
public interface IFileChangeService
{
    /// <summary>把某个 commit 改动的文件落库。同一 commit 的同一文件只落一条（幂等）。</summary>
    Task<int> CaptureCommitAsync(
        Guid taskId,
        Guid projectId,
        string repoPath,
        string commitSha,
        Guid? sessionId = null,
        string? author = null,
        CancellationToken cancellationToken = default);

    /// <summary>把工作区里改了但还没提交的文件落库。</summary>
    Task<int> CaptureWorkingTreeAsync(
        Guid taskId,
        Guid projectId,
        string repoPath,
        Guid? sessionId = null,
        string? author = null,
        CancellationToken cancellationToken = default);

    /// <summary>列某个任务的产出文件（新到旧）。</summary>
    Task<IReadOnlyList<TaskFileChangeDto>> ListAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按路径搜产出文件（新到旧）。
    ///
    /// 全局搜索要能答「哪个任务动过这个文件」，所以结果里带着任务与项目。
    /// </summary>
    Task<IReadOnlyList<FileChangeHitDto>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 产出文件采集的默认实现。
///
/// 两条规矩：
/// - **采集失败不抛异常**。git 读不到（仓库没了 / 权限不对 / 超时）就返回 0 ——
///   采集是「锦上添花」，不能把 agent 的主流程带崩。
/// - **幂等**。同一个 commit 采两次不会多出重复行：人可能反复点「提交」，
///   采集点也会被反复触发。
/// </summary>
public sealed class FileChangeService(
    IAppDbContext db,
    IGitInspector git,
    IClock clock) : IFileChangeService
{
    public async Task<int> CaptureCommitAsync(
        Guid taskId,
        Guid projectId,
        string repoPath,
        string commitSha,
        Guid? sessionId = null,
        string? author = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(commitSha))
        {
            return 0;
        }

        IReadOnlyList<GitFileChange> changes;

        try
        {
            changes = git.GetCommitFileChanges(repoPath, commitSha);
        }
        catch (Exception)
        {
            // 读不到仓库 —— 返回 0，不打断 agent 的活
            return 0;
        }

        if (changes.Count == 0)
        {
            return 0;
        }

        // 幂等：同一个 (TaskId, CommitSha, Path) 只落一条。
        // 一次查回来，别一条一条问库。
        var existing = await db.TaskFileChanges
            .AsNoTracking()
            .Where(c => c.TaskId == taskId && c.CommitSha == commitSha)
            .Select(c => c.Path)
            .ToListAsync(cancellationToken);

        var known = new HashSet<string>(existing, StringComparer.Ordinal);
        var added = 0;

        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Path) || !known.Add(change.Path))
            {
                continue;
            }

            db.TaskFileChanges.Add(new TaskFileChange
            {
                TaskId = taskId,
                ProjectId = projectId,
                SessionId = sessionId,
                Path = change.Path,
                ChangeKind = change.ChangeKind,
                OldPath = change.OldPath,
                LinesAdded = change.LinesAdded,
                LinesDeleted = change.LinesDeleted,
                CommitSha = commitSha,
                Author = author,
                CreatedAt = clock.UtcNow
            });

            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    public async Task<int> CaptureWorkingTreeAsync(
        Guid taskId,
        Guid projectId,
        string repoPath,
        Guid? sessionId = null,
        string? author = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return 0;
        }

        IReadOnlyList<GitFileChange> changes;

        try
        {
            changes = git.GetWorkingTreeFileChanges(repoPath);
        }
        catch (Exception)
        {
            return 0;
        }

        if (changes.Count == 0)
        {
            return 0;
        }

        // 工作区的记录没有 commit，所以按 (TaskId, Path) 认。
        // 已经在库里了就更新（改了多少行变了要更新），别每次都插新行 ——
        // 否则任务进一次评审就多一份「已产出但没提交」的清单。
        var existing = await db.TaskFileChanges
            .Where(c => c.TaskId == taskId && c.CommitSha == null)
            .ToListAsync(cancellationToken);

        var byPath = new Dictionary<string, TaskFileChange>(StringComparer.Ordinal);
        foreach (var row in existing)
        {
            byPath[row.Path] = row;
        }

        var added = 0;

        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Path))
            {
                continue;
            }

            if (byPath.TryGetValue(change.Path, out var row))
            {
                row.ChangeKind = change.ChangeKind;
                row.OldPath = change.OldPath;
                row.LinesAdded = change.LinesAdded;
                row.LinesDeleted = change.LinesDeleted;
                row.Author = author ?? row.Author;
                row.SessionId = sessionId ?? row.SessionId;
                continue;
            }

            var entity = new TaskFileChange
            {
                TaskId = taskId,
                ProjectId = projectId,
                SessionId = sessionId,
                Path = change.Path,
                ChangeKind = change.ChangeKind,
                OldPath = change.OldPath,
                LinesAdded = change.LinesAdded,
                LinesDeleted = change.LinesDeleted,
                CommitSha = null,
                Author = author,
                CreatedAt = clock.UtcNow
            };

            db.TaskFileChanges.Add(entity);
            byPath[change.Path] = entity;
            added++;
        }

        if (added > 0 || existing.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    public async Task<IReadOnlyList<TaskFileChangeDto>> ListAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.TaskFileChanges
            .AsNoTracking()
            .Where(c => c.TaskId == taskId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<FileChangeHitDto>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var value = query.Trim();

        if (value.Length == 0)
        {
            return [];
        }

        // 大小写不敏感：路径在 macOS / Windows 上本来就不分大小写，
        // 人打「orders」要能搜到「Orders.cs」。
        // lower() 是 SQL 函数，翻译得过去；value 的 lower() 在客户端算好当参数传。
        var lowered = value.ToLowerInvariant();

        var rows = await db.TaskFileChanges
            .AsNoTracking()
            .Where(c => c.Path.ToLower().Contains(lowered))
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var taskIds = rows.Select(r => r.TaskId).Distinct().ToList();

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key, t.Title, t.ProjectId })
            .ToListAsync(cancellationToken);

        var projectIds = tasks.Select(t => t.ProjectId).Distinct().ToList();

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        var taskById = tasks.ToDictionary(t => t.Id);
        var projectById = projects.ToDictionary(p => p.Id);

        return rows.Select(r =>
        {
            taskById.TryGetValue(r.TaskId, out var task);
            var projectName = task is not null && projectById.TryGetValue(task.ProjectId, out var p)
                ? p.Name
                : "—";

            return new FileChangeHitDto(
                r.TaskId,
                task?.Key,
                task?.Title,
                projectName,
                r.Path,
                r.ChangeKind,
                r.LinesAdded,
                r.LinesDeleted,
                r.CreatedAt);
        }).ToList();
    }

    private static TaskFileChangeDto ToDto(TaskFileChange change) => new(
        change.Id,
        change.Path,
        change.ChangeKind,
        change.OldPath,
        change.LinesAdded,
        change.LinesDeleted,
        change.CommitSha,
        change.Branch,
        change.Author,
        change.CreatedAt);
}
