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

    /// <summary>
    /// 「先干哪个」—— 给候选活排个序，并说清**为什么**是它。
    ///
    /// 调度板回答「什么能干」，这个回答「先干哪个」：
    /// 同样能干的活有五个，agent 挑哪个、为什么挑，是两回事。
    /// 排序要**可解释** —— agent 看得懂理由，人也看得懂凭什么。
    /// </summary>
    Task<IReadOnlyList<DispatchSuggestionDto>> SuggestAsync(
        Guid projectId,
        int limit = 10,
        string? excludeAgentId = null,
        CancellationToken cancellationToken = default,
        /// <summary>谁在问。指派给他的活排前面（人工指派也是干预）。</summary>
        string? forAgentId = null);
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

/// <summary>一条调度建议：这个活为什么排在这儿。</summary>
public sealed record DispatchSuggestionDto(
    Guid TaskId,
    string? Key,
    string Title,
    TaskType Type,
    TaskPriority Priority,
    double Weight,
    string Status,
    /// <summary>排序分（越高越该先做）。</summary>
    double Score,
    /// <summary>为什么推荐它 —— agent 要知道，人也要看得懂。</summary>
    IReadOnlyList<string> Reasons,
    /// <summary>它做完能解锁几个任务（别人在等它）。</summary>
    int Unblocks,
    /// <summary>工位提示（主工作区被占着 → 要自己开 worktree）。空 = 现在就能开工。</summary>
    string? StationHint,
    DateTimeOffset CreatedAt,
    /// <summary>人工排的位次（为空 = 没排）。</summary>
    int? PinnedOrder = null,
    /// <summary>指派给了谁。</summary>
    string? Assignee = null);

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
    public async Task<IReadOnlyList<DispatchSuggestionDto>> SuggestAsync(
        Guid projectId,
        int limit = 10,
        string? excludeAgentId = null,
        CancellationToken cancellationToken = default,
        string? forAgentId = null)
    {
        var take = Math.Clamp(limit, 1, 50);
        var now = clock.UtcNow;

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId &&
                        !t.IsArchived &&
                        t.Status != TaskStatus.Done &&
                        t.Status != TaskStatus.Cancelled)
            .Select(t => new
            {
                t.Id, t.Key, t.Title, t.Type, t.Priority, t.Weight, t.Status,
                t.CreatedAt, t.ClaimedByAgentId, t.LeaseExpiresAt, t.WorkPath,
                t.Assignee, t.PinnedOrder
            })
            .ToListAsync(cancellationToken);

        if (tasks.Count == 0)
        {
            return [];
        }

        var ids = tasks.Select(t => t.Id).ToList();
        var byId = tasks.ToDictionary(t => t.Id);

        // 依赖的两个方向都要：
        // 正向（我依赖它）用来判断能不能开工，反向（谁依赖我）用来算「做完能解锁几个」。
        var deps = await db.TaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.TaskId) && d.Type == DependencyType.BlockedBy)
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        var forward = deps
            .GroupBy(d => d.TaskId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.DependsOnTaskId).Distinct().ToList());

        var reverse = deps
            .GroupBy(d => d.DependsOnTaskId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.TaskId).Distinct().ToList());

        // 已完成的前置（可能不在这个项目里，也要算数）
        var doneIds = await db.Tasks.AsNoTracking()
            .Where(t => deps.Select(d => d.DependsOnTaskId).Contains(t.Id) && t.Status == TaskStatus.Done)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var done = doneIds.ToHashSet();

        // 每个活的上一个持有者（最近的认领 / 释放事件）——
        // 用来判断「这活是不是某个 agent 刚放下的」。
        var holderEvents = await db.StatusEvents.AsNoTracking()
            .Where(e => ids.Contains(e.TaskId) &&
                        (e.EventType == TaskEventTypes.Claimed || e.EventType == TaskEventTypes.Released))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.TaskId, e.Actor })
            .ToListAsync(cancellationToken);

        var lastHolders = holderEvents
            .GroupBy(e => e.TaskId)
            .ToDictionary(g => g.Key, g => g.Last().Actor);

        // 主工作区现在被谁占着
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        var mainCheckout = project?.RepoPath;

        var mainHolder = tasks.FirstOrDefault(t =>
            t.LeaseExpiresAt is not null &&
            t.LeaseExpiresAt.Value > now &&
            t.ClaimedByAgentId is not null &&
            (t.WorkPath is null ||
             (mainCheckout is not null &&
              string.Equals(mainCheckout, t.WorkPath, StringComparison.OrdinalIgnoreCase))));

        var suggestions = new List<DispatchSuggestionDto>();

        foreach (var task in tasks)
        {
            // 前置没做完 → 现在不能开工
            var blockers = forward.TryGetValue(task.Id, out var list)
                ? list.Where(b => !done.Contains(b)).ToList()
                : [];

            if (blockers.Count > 0)
            {
                continue;
            }

            // 有人在干 → 不是候选
            var held = task.LeaseExpiresAt is not null && task.LeaseExpiresAt.Value > now;
            if (held)
            {
                continue;
            }

            // 排除某个 agent 的活：**手上占着的**（本来就不算候选）
            // 与**刚放下的**（上一个持有者是他）——
            // agent 常问「还有什么该我干的」，别把刚放下的活又推回给它。
            if (!string.IsNullOrWhiteSpace(excludeAgentId))
            {
                var heldBy = task.ClaimedByAgentId;
                var lastHolder = lastHolders.TryGetValue(task.Id, out var h) ? h : null;

                if (string.Equals(heldBy, excludeAgentId, StringComparison.Ordinal) ||
                    string.Equals(lastHolder, excludeAgentId, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            // 解锁数：还有几个任务在等它
            var unblocks = reverse.TryGetValue(task.Id, out var waiting)
                ? waiting.Count(w => !byId.TryGetValue(w, out var t) || t.Status != TaskStatus.Done)
                : 0;

            var reasons = new List<string>();
            var score = 0d;

            // 人工排的位次：最硬的信号 —— 人说先干它，那就先干它
            if (task.PinnedOrder is int pinnedOrder)
            {
                score += 1000 - Math.Min(pinnedOrder, 999);
                reasons.Add($"人工排在第 {pinnedOrder} 位");
            }

            // 指派给谁了：被问的那个人，这活就是他的
            if (!string.IsNullOrWhiteSpace(forAgentId) &&
                string.Equals(task.Assignee, forAgentId, StringComparison.Ordinal))
            {
                score += 200;
                reasons.Add("指派给你");
            }

            var priorityPoints = PriorityPoints(task.Priority);
            score += priorityPoints;
            reasons.Add(task.Priority switch
            {
                TaskPriority.Critical => "优先级：紧急",
                TaskPriority.High => "优先级：高",
                TaskPriority.Low => "优先级：低",
                _ => "优先级：普通"
            });

            if (task.Weight > 1)
            {
                score += task.Weight * 5;
                reasons.Add($"权重 {task.Weight:0.##}");
            }

            // 等得越久越该做 —— 防饥饿
            var waitedDays = Math.Max(0, (now - task.CreatedAt).TotalDays);
            if (waitedDays >= 1)
            {
                score += Math.Min(waitedDays * 2, 30);
                reasons.Add($"已等 {(int)waitedDays} 天");
            }

            if (unblocks > 0)
            {
                score += Math.Min(unblocks * 8, 40);
                reasons.Add($"做完能解锁 {unblocks} 个任务");
            }

            // 工位：主工作区被占着 → 要并行就得开自己的 worktree
            string? stationHint = null;
            if (mainHolder is not null &&
                !string.Equals(mainHolder.ClaimedByAgentId, task.ClaimedByAgentId, StringComparison.Ordinal))
            {
                stationHint = $"主工作区被 {mainHolder.ClaimedByAgentId} 占着，要并行就得开自己的 worktree";
            }
            else
            {
                reasons.Add("现在没人占主工作区");
            }

            suggestions.Add(new DispatchSuggestionDto(
                task.Id,
                task.Key,
                task.Title,
                task.Type,
                task.Priority,
                task.Weight,
                task.Status.ToString(),
                Math.Round(score, 1),
                reasons,
                unblocks,
                stationHint,
                task.CreatedAt,
                task.PinnedOrder,
                task.Assignee));
        }

        // 人工排期的在前（分数里已经加了 1000+），其余按分；同分按老的先做
        return suggestions
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.CreatedAt)
            .Take(take)
            .ToList();
    }

    private static double PriorityPoints(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => 40,
        TaskPriority.High => 30,
        TaskPriority.Normal => 20,
        _ => 10
    };

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
