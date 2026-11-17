using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 调度视图：**现在什么能干、什么卡着、谁在干**。
///
/// agent-native PM 缺的就是这一块。
/// 以前 agent 要自己 list 一堆任务、自己判断哪个能开工、
/// 自己算「我是不是该等那个任务」—— 全靠它自己拼。
/// 现在这些判断工作台替它做：依赖链、租约占用、工位都在库里，
/// 缺的只是一个把它们组织起来的查询。
///
/// 三桶：
/// - **Ready（现在能干的）**：前置全清、没人占着 —— 可以直接领；
/// - **InProgress（有人在干的）**：租约还活着；
/// - **Blocked（卡着的）**：还有前置没做完，带上卡它的是谁、链有多深。
/// </summary>
public interface IDispatchService
{
    Task<DispatchBoardDto> GetAsync(Guid projectId, int limit = 20, CancellationToken cancellationToken = default);
}

/// <summary>调度板上的一条任务。</summary>
public sealed record DispatchTaskDto(
    Guid Id,
    string? Key,
    string Title,
    TaskStatus Status,
    TaskType Type,
    string? Assignee,
    /// <summary>领着这个活的 agent（租约里的）。</summary>
    string? ClaimedBy,
    /// <summary>它登记的工位（自己的 worktree）。空 = 主工作区。</summary>
    string? WorkPath,
    /// <summary>true = 用的是项目主工作区（共享的，同一时刻只允许一个 agent）。</summary>
    bool IsMainCheckout,
    /// <summary>还差几个前置任务。</summary>
    int PendingDependencies,
    /// <summary>卡住它的前置任务编号（直接前置，未完成的那些）。</summary>
    IReadOnlyList<string> BlockedBy,
    /// <summary>阻塞链多深。0 = 能开工；1 = 等一层；越大等得越久。</summary>
    int BlockDepth,
    /// <summary>在项目进度里的权重。</summary>
    double Weight,
    DateTimeOffset CreatedAt);

/// <summary>调度板。</summary>
public sealed record DispatchBoardDto(
    Guid ProjectId,
    IReadOnlyList<DispatchTaskDto> Ready,
    IReadOnlyList<DispatchTaskDto> InProgress,
    IReadOnlyList<DispatchTaskDto> Blocked,
    int ReadyCount,
    int InProgressCount,
    int BlockedCount);

public sealed class DispatchService(IAppDbContext db, IClock clock) : IDispatchService
{
    public async Task<DispatchBoardDto> GetAsync(
        Guid projectId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        var now = clock.UtcNow;

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId &&
                        !t.IsArchived &&
                        t.Status != TaskStatus.Done &&
                        t.Status != TaskStatus.Cancelled)
            .ToListAsync(cancellationToken);

        if (tasks.Count == 0)
        {
            return new DispatchBoardDto(projectId, [], [], [], 0, 0, 0);
        }

        var ids = tasks.Select(t => t.Id).ToList();
        var byId = tasks.ToDictionary(t => t.Id);

        // 依赖只取「我依赖的那些」—— 库里存的是一种方向，Blocks 会翻成对偶。
        var deps = await db.TaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.TaskId) && d.Type == DependencyType.BlockedBy)
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        var depsByTask = deps
            .GroupBy(d => d.TaskId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.DependsOnTaskId).Distinct().ToList());

        // 每个任务的前置是否已完成
        var doneIds = tasks
            .Where(t => t.Status == TaskStatus.Done)
            .Select(t => t.Id)
            .ToHashSet();

        // 已完成但被归档/取消的前置也算没做完 —— 只有真正 Done 才算数。
        var allTaskStates = await db.Tasks.AsNoTracking()
            .Where(t => deps.Select(d => d.DependsOnTaskId).Contains(t.Id))
            .Select(t => new { t.Id, t.Status })
            .ToListAsync(cancellationToken);

        var doneSet = allTaskStates.Where(s => s.Status == TaskStatus.Done).Select(s => s.Id).ToHashSet();
        doneSet.UnionWith(doneIds);

        var depthCache = new Dictionary<Guid, int>();

        int DepthOf(Guid taskId, HashSet<Guid> visiting)
        {
            if (depthCache.TryGetValue(taskId, out var cached))
            {
                return cached;
            }

            // 环（理论上服务层不允许，但别在这儿炸掉）
            if (!visiting.Add(taskId))
            {
                return 0;
            }

            var blockers = depsByTask.TryGetValue(taskId, out var list) ? list : [];
            var depth = 0;

            foreach (var blockerId in blockers)
            {
                if (doneSet.Contains(blockerId))
                {
                    continue;
                }

                var sub = DepthOf(blockerId, visiting);
                depth = Math.Max(depth, sub + 1);
            }

            visiting.Remove(taskId);
            depthCache[taskId] = depth;
            return depth;
        }

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        var mainCheckout = project?.RepoPath;

        var ready = new List<DispatchTaskDto>();
        var inProgress = new List<DispatchTaskDto>();
        var blocked = new List<DispatchTaskDto>();

        foreach (var task in tasks)
        {
            var blockers = depsByTask.TryGetValue(task.Id, out var list)
                ? list.Where(b => !doneSet.Contains(b)).ToList()
                : [];

            // visiting 传空的：里面装的是「递归路径上的人」，
            // 预置自己会被当成环，深度直接算成 0。
            var depth = blockers.Count == 0
                ? 0
                : DepthOf(task.Id, new HashSet<Guid>());

            var leaseAlive = task.ClaimedByAgentId is not null &&
                            task.LeaseExpiresAt is not null &&
                            task.LeaseExpiresAt.Value > now;

            var dto = new DispatchTaskDto(
                task.Id,
                task.Key,
                task.Title,
                task.Status,
                task.Type,
                task.Assignee,
                // 租约过期的「前主人」不算现在在干 —— 免得看板上显示有人占着，其实没人管
                leaseAlive ? task.ClaimedByAgentId : null,
                task.WorkPath,
                string.IsNullOrWhiteSpace(task.WorkPath) ||
                (mainCheckout is not null &&
                 string.Equals(mainCheckout, task.WorkPath, StringComparison.OrdinalIgnoreCase)),
                blockers.Count,
                blockers
                    .Select(b => byId.TryGetValue(b, out var t) ? (t.Key ?? b.ToString()) : b.ToString())
                    .ToList(),
                depth,
                task.Weight,
                task.CreatedAt);

            if (blockers.Count > 0)
            {
                blocked.Add(dto);
            }
            else if (leaseAlive)
            {
                inProgress.Add(dto);
            }
            else
            {
                ready.Add(dto);
            }
        }

        // 排序：能干的按「优先级 → 权重 → 创建时间」；
        // 卡着的按「链深 → 创建时间」—— 等得越久越靠前。
        ready = ready
            .OrderByDescending(t => t.Weight)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        inProgress = inProgress.OrderBy(t => t.CreatedAt).ToList();

        blocked = blocked
            .OrderByDescending(t => t.BlockDepth)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        return new DispatchBoardDto(
            projectId,
            ready.Take(take).ToList(),
            inProgress.Take(take).ToList(),
            blocked.Take(take).ToList(),
            ready.Count,
            inProgress.Count,
            blocked.Count);
    }
}
