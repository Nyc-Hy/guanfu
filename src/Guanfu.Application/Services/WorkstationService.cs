using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>一个工位的现状。</summary>
public sealed record WorkstationEntryDto(
    /// <summary>登记这个工位的活（没有活认领 = 没人管的工位）。</summary>
    Guid? TaskId,
    /// <summary>那个活的编号（REQ-001 这样的，给人看的）。</summary>
    string? TaskKey,
    /// <summary>那个活的标题。</summary>
    string? TaskTitle,
    /// <summary>现在占着它的 agent。</summary>
    string? AgentId,
    /// <summary>工位目录。为空 = 主工作区（仓库根）。</summary>
    string? WorkPath,
    /// <summary>是不是主工作区（共享的那一个）。</summary>
    bool IsMainWorkspace,
    /// <summary>目录还在不在。</summary>
    bool PathExists,
    /// <summary>还是不是本仓库的 worktree（目录被换掉了就是 false）。</summary>
    bool IsValidWorktree,
    /// <summary>租约还活着吗。</summary>
    bool LeaseAlive,
    /// <summary>租约到期时间。</summary>
    DateTimeOffset? LeaseExpiresAt,
    /// <summary>这个工位是不是**没人管了**（活干完了 / 租约过期了，但目录还在）。</summary>
    bool Orphaned,
    /// <summary>
    /// 清理它的确切命令。**观复不替 agent 删目录** ——
    /// 那是它自己仓库里的事，观复只给命令（还有哪一步要人确认）。
    /// </summary>
    string? CleanupCommand,
    /// <summary>
    /// 活干完了、该收这个工位了（任务已完成 / 已取消，但目录还在）。
    /// 跟「没人管」不同：这是**该收了**，提醒人去清。
    /// </summary>
    bool ReadyToClean = false);

/// <summary>放掉工位的结果。</summary>
public sealed record StationReleaseResult(
    bool Ok,
    string? Error = null,
    /// <summary>放掉的工位目录。</summary>
    string? WorkPath = null,
    /// <summary>清理命令（观复不替 agent 删目录）。</summary>
    string? CleanupCommand = null);

/// <summary>
/// 工位清单与放行。
///
/// **worktree 建了没人收** —— agent 建了工位，干完活就走了，
/// 目录一直躺在磁盘上；下一个 agent 不知道那个目录还在，
/// 人也不知道哪些能删。
///
/// 这里做两件事：
/// ① 把工位列出来 —— 谁在用、目录还在不在、**有没有人管**；
/// ② 放掉工位 —— 把登记清掉，并给出清理的确切命令。
///
/// **观复不替 agent 删目录**（跟不替它建 worktree 一个道理）：
/// 那是它自己仓库里的事，观复只给命令；真要删，git 会问一句，人也看得见。
/// </summary>
public interface IWorkstationService
{
    /// <summary>
    /// 工位现状：主工作区被谁占着、这次该用哪个目录、要并行的话建哪个工位。
    ///
    /// 上下文包与 agent 的工作包都带它 —— 一处算，两处用，
    /// 两边算法不一样的话 agent 会看到互相打架的说法。
    /// </summary>
    Task<WorkStationDto> DescribeAsync(
        Guid projectId,
        Guid? taskId = null,
        string? agentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>列工位：登记着的 + 仓库里没人管的。</summary>
    Task<IReadOnlyList<WorkstationEntryDto>> ListAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>放掉某个活登记的工位（清登记 + 给清理命令）。</summary>
    Task<StationReleaseResult> ReleaseAsync(Guid taskId, CancellationToken cancellationToken = default);
}

public sealed class WorkstationService(
    IAppDbContext db,
    IGitInspector git,
    IClock clock) : IWorkstationService
{
    public async Task<WorkStationDto> DescribeAsync(
        Guid projectId,
        Guid? taskId = null,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        var task = taskId is Guid id
            ? await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            : null;

        var leases = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId &&
                        t.ClaimedByAgentId != null &&
                        t.LeaseExpiresAt != null &&
                        t.LeaseExpiresAt > now)
            .Select(t => new { t.Id, t.Key, t.ClaimedByAgentId, t.WorkPath })
            .ToListAsync(cancellationToken);

        // 主工作区的占用者：登记的工位为空 = 用主工作区
        var mainHolder = leases.FirstOrDefault(l =>
            l.WorkPath is null ||
            (project?.RepoPath is not null &&
             string.Equals(l.WorkPath, project.RepoPath, StringComparison.OrdinalIgnoreCase)));

        // 主工作区被**别人**占着 = 我不能用它（自己占着当然能用）
        var heldByOther = mainHolder is not null &&
                          !string.Equals(mainHolder.ClaimedByAgentId, agentId, StringComparison.Ordinal);

        // 绑定键（比较用，解过软链）与人填的路径（显示与命令用）分开 ——
        // 给 agent 的命令要用**它认得的那个路径**，不然它敲了命令跟人看到的不是一个地方。
        var repoRoot = project?.RepoRoot ??
                       (string.IsNullOrWhiteSpace(project?.RepoPath) ? null : project.RepoPath);
        var displayRoot = project?.RepoPath;

        // 建议的独立工位：主工作区被别人占着时才给（能直接用主工作区就别多此一举）
        var suggestedWorktree = heldByOther
            ? WorkstationAdvice.SuggestWorktree(displayRoot, task?.Key)
            : null;

        var suggestedBranch = suggestedWorktree is null
            ? null
            : WorkstationAdvice.SuggestBranch(task?.Key, task?.Branch);

        // 该用的目录：登记的工位优先，回退主工作区
        var effective = task?.WorkPath;
        var isolated = false;

        if (!string.IsNullOrWhiteSpace(effective))
        {
            var check = WorkPathPolicy.ValidateWorktree(repoRoot ?? effective, effective);
            isolated = check.Ok && check.IsIsolated;

            if (!check.Ok)
            {
                effective = project?.RepoPath;
            }
        }
        else
        {
            effective = project?.RepoPath;
        }

        return new WorkStationDto(
            displayRoot,
            mainHolder?.ClaimedByAgentId,
            mainHolder?.Key,
            project?.MaxConcurrentAgents ?? 0,
            leases.Count,
            task?.WorkPath,
            !heldByOther,
            effective,
            isolated,
            suggestedWorktree,
            suggestedBranch,
            WorkstationAdvice.SetupCommands(displayRoot, suggestedWorktree, suggestedBranch));
    }

    public async Task<IReadOnlyList<WorkstationEntryDto>> ListAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var projects = await db.Projects.AsNoTracking()
            .Where(p => projectId == null || p.Id == projectId)
            .ToListAsync(cancellationToken);

        var projectIds = projects.Select(p => p.Id).ToList();

        // 登记了工位的活（主工作区的不算 —— 那是仓库根，不用管）
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId) && t.WorkPath != null)
            .Select(t => new
            {
                t.Id,
                t.Key,
                t.Title,
                t.Status,
                t.ClaimedByAgentId,
                t.WorkPath,
                t.LeaseExpiresAt,
                t.ProjectId
            })
            .ToListAsync(cancellationToken);

        var stations = new List<WorkstationEntryDto>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            var project = projects.FirstOrDefault(p => p.Id == task.ProjectId);
            var path = task.WorkPath!;

            // 比对用**解析过的**路径：登记的是人填的那个（/tmp/...），
            // git 报的是解过软链的（/private/tmp/...）—— 不归一化会把同一个工位算成两个。
            claimed.Add(RealPath(path));

            var leaseAlive = task.LeaseExpiresAt is DateTimeOffset expires && expires > now;

            // 活干完了（已完成 / 已取消）→ 该收这个工位了
            var readyToClean = task.Status is TaskStatus.Done or TaskStatus.Cancelled;

            stations.Add(new WorkstationEntryDto(
                task.Id,
                task.Key,
                task.Title,
                task.ClaimedByAgentId,
                path,
                IsMainWorkspace: false,
                PathExists: Directory.Exists(path),
                IsValidWorktree: IsValidWorktree(project?.RepoRoot ?? project?.RepoPath, path),
                LeaseAlive: leaseAlive,
                task.LeaseExpiresAt,
                Orphaned: !leaseAlive,
                ReadyToClean: readyToClean,
                CleanupCommand: CleanupCommand(project?.RepoPath, path)));
        }

        // 仓库里没人管的工位：git 自己知道有哪些，登记里对不上就是没人管
        foreach (var project in projects)
        {
            var repoPath = project.RepoPath;
            if (string.IsNullOrWhiteSpace(repoPath))
            {
                continue;
            }

            foreach (var worktree in git.ListWorktrees(repoPath))
            {
                if (worktree.IsMain || claimed.Contains(RealPath(worktree.Path)))
                {
                    continue;
                }

                stations.Add(new WorkstationEntryDto(
                    TaskId: null,
                    TaskKey: null,
                    TaskTitle: null,
                    AgentId: null,
                    WorkPath: worktree.Path,
                    IsMainWorkspace: false,
                    PathExists: Directory.Exists(worktree.Path),
                    IsValidWorktree: true,
                    LeaseAlive: false,
                    LeaseExpiresAt: null,
                    Orphaned: true,
                    ReadyToClean: true,
                    CleanupCommand: CleanupCommand(repoPath, worktree.Path)));
            }
        }

        return stations;
    }

    public async Task<StationReleaseResult> ReleaseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

        if (string.IsNullOrWhiteSpace(task.WorkPath))
        {
            return new StationReleaseResult(false, "这个活没有登记工位（用的是项目的主工作区，不用收）。");
        }

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        var path = task.WorkPath;

        // 登记清掉 —— 工位回到池子里
        task.WorkPath = null;
        task.UpdatedAt = clock.UtcNow;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = task.ClaimedByAgentId,
            Note = $"放掉工位 {path}",
            EventType = TaskEventTypes.StationReleased,
            Payload = GuanfuJson.Serialize(new { workPath = path }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

        return new StationReleaseResult(true, null, path, CleanupCommand(project?.RepoPath, path));
    }

    /// <summary>
    /// 归一化路径（解软链）—— 比对工位时用。
    /// macOS 上 /tmp 其实是 /private/tmp，登记的与 git 报的不是一个写法。
    /// </summary>
    private static string RealPath(string path)
    {
        try
        {
            return PathBoundary.ResolveRealPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>还是不是本仓库的 worktree（登记那一刻验过，中途被换掉了就是 false）。</summary>
    private static bool IsValidWorktree(string? repoRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return false;
        }

        return WorkPathPolicy.ValidateWorktree(repoRoot, path).Ok;
    }

    /// <summary>
    /// 清理命令。**观复不替 agent 删目录** —— 只给确切命令。
    /// 有未提交改动的话 git 会拦一下（那正是要人看一眼的地方）。
    /// </summary>
    private static string? CleanupCommand(string? repoPath, string path)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }

        return $"git -C {repoPath} worktree remove {path}";
    }
}
