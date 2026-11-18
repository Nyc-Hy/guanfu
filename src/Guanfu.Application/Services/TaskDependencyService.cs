using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>一条任务依赖。</summary>
public sealed record TaskDependencyDto(
    Guid Id,
    Guid TaskId,
    string? TaskKey,
    string TaskTitle,
    Guid DependsOnTaskId,
    string? DependsOnKey,
    string DependsOnTitle,
    /// <summary>前置任务是否已完成。前置没做完，本任务就不该开工。</summary>
    bool DependsOnDone,
    string Type,
    string? Note,
    DateTimeOffset CreatedAt);

/// <summary>
/// 任务依赖。表达「A 做完 B 才能开始」。
///
/// 依赖存在一张单独的表里而不是塞进 TaskItem 的字段 —— 一对多，
/// 而且方向有两种（我依赖它 / 我阻塞它），字段表达不了。
/// </summary>
public interface ITaskDependencyService
{
    /// <summary>列出某个任务的所有依赖（我依赖的 + 我阻塞的）。</summary>
    Task<IReadOnlyList<TaskDependencyDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 加一条依赖。
    /// <paramref name="type"/> 是 Blocks 时会翻成对偶的 BlockedBy 存 ——
    /// 库里只存一种方向，查询时不用两边都判。
    /// </summary>
    Task<TaskDependencyDto> AddAsync(
        Guid taskId,
        Guid dependsOnTaskId,
        DependencyType type = DependencyType.BlockedBy,
        string? note = null,
        CancellationToken cancellationToken = default);

    /// <summary>删掉一条依赖。</summary>
    Task RemoveAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken = default);

    /// <summary>还有几个前置任务没做完。0 表示可以开工。</summary>
    Task<int> CountPendingAsync(Guid taskId, CancellationToken cancellationToken = default);
}

public sealed class TaskDependencyService(IAppDbContext db, IClock clock) : ITaskDependencyService
{
    /// <summary>
    /// 「环检测 → 写入」的串行闸门。
    ///
    /// 检测本身不是原子的：两个并发请求各自遍历时都看不到对方正要写的那条边，
    /// 于是 A→B 和 B→A 双双通过检测，库里就留下一个环，之后两个任务永远开不了工。
    /// SQLite 是单写者、Web 是唯一写进程，所以进程内串行化就够了 ——
    /// 不需要分布式锁，也不要把检测挪进 SQL（EF 翻译不了图遍历）。
    /// </summary>
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public async Task<IReadOnlyList<TaskDependencyDto>> ListAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.TaskDependencies
            .AsNoTracking()
            .Where(d => d.TaskId == taskId || d.DependsOnTaskId == taskId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var taskIds = rows
            .SelectMany(d => new[] { d.TaskId, d.DependsOnTaskId })
            .Distinct()
            .ToList();

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        return rows
            .Select(d =>
            {
                tasks.TryGetValue(d.TaskId, out var task);
                tasks.TryGetValue(d.DependsOnTaskId, out var dependsOn);

                return new TaskDependencyDto(
                    d.Id,
                    d.TaskId,
                    task?.Key,
                    task?.Title ?? "(已删除)",
                    d.DependsOnTaskId,
                    dependsOn?.Key,
                    dependsOn?.Title ?? "(已删除)",
                    // 前置被删了算「已满足」—— 与 CountPending / 开工守卫同一口径，
                    // 不能永远等一个不存在的任务
                    dependsOn is null || dependsOn.Status == TaskStatus.Done,
                    d.Type.ToString(),
                    d.Note,
                    d.CreatedAt);
            })
            .OrderBy(d => d.DependsOnDone)
            .ThenBy(d => d.CreatedAt)
            .ToList();
    }

    public async Task<TaskDependencyDto> AddAsync(
        Guid taskId,
        Guid dependsOnTaskId,
        DependencyType type = DependencyType.BlockedBy,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (taskId == dependsOnTaskId)
        {
            throw new GuanfuException("任务不能依赖自己。");
        }

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId || t.Id == dependsOnTaskId)
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        if (!tasks.ContainsKey(taskId))
        {
            throw new NotFoundException("任务", taskId);
        }

        if (!tasks.ContainsKey(dependsOnTaskId))
        {
            throw new NotFoundException("前置任务", dependsOnTaskId);
        }

        // 跨项目依赖一律拒绝：两个项目各有自己的里程碑、进度与看板，
        // 串起来之后「等前置完成」可能永远等不到（那边根本不在这个项目的流程里），
        // 还会把本项目的任务无声卡死。
        if (tasks[taskId].ProjectId != tasks[dependsOnTaskId].ProjectId)
        {
            throw new GuanfuException(
                $"依赖的两个任务不属于同一个项目（{tasks[taskId].ProjectId} / {tasks[dependsOnTaskId].ProjectId}），已拒绝。");
        }

        // Blocks 翻成对偶：库里只存「我依赖它」一种方向
        var (blockedTaskId, prerequisiteId) = type == DependencyType.Blocks
            ? (dependsOnTaskId, taskId)
            : (taskId, dependsOnTaskId);

        // 检测与写入必须在同一个闸门里 —— 见 WriteGate 的说明。
        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            if (await WouldCreateCycleAsync(blockedTaskId, prerequisiteId, cancellationToken))
            {
                throw new GuanfuException(
                    "这条依赖会形成环（A 依赖 B、B 依赖 A …），已拒绝。依赖必须是有向无环的。");
            }

            var existing = await db.TaskDependencies
                .FirstOrDefaultAsync(
                    d => d.TaskId == blockedTaskId && d.DependsOnTaskId == prerequisiteId,
                    cancellationToken);

            if (existing is not null)
            {
                throw new GuanfuException("这两个任务之间已经有依赖了。");
            }

            var dependency = new TaskDependency
            {
                TaskId = blockedTaskId,
                DependsOnTaskId = prerequisiteId,
                Type = DependencyType.BlockedBy,
                Note = note,
                CreatedAt = clock.UtcNow
            };

            db.TaskDependencies.Add(dependency);

            // 依赖也是账本上的事 —— 事后要能追溯「当时是谁把它挡住的」
            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = blockedTaskId,
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Human,
                EventType = TaskEventTypes.DependencyAdded,
                Note = $"新增依赖: 依赖 {prerequisiteId} 先完成",
                Payload = GuanfuJson.Serialize(new { dependency = prerequisiteId, note }),
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            WriteGate.Release();
        }

        var list = await ListAsync(blockedTaskId, cancellationToken);
        return list.First(d => d.DependsOnTaskId == prerequisiteId);
    }

    public async Task RemoveAsync(
        Guid taskId,
        Guid dependsOnTaskId,
        CancellationToken cancellationToken = default)
    {
        var dependency = await db.TaskDependencies
            .FirstOrDefaultAsync(
                d => (d.TaskId == taskId && d.DependsOnTaskId == dependsOnTaskId) ||
                     (d.TaskId == dependsOnTaskId && d.DependsOnTaskId == taskId),
                cancellationToken);

        if (dependency is null)
        {
            throw new NotFoundException("任务依赖", taskId);
        }

        db.TaskDependencies.Remove(dependency);

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = dependency.TaskId,
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Human,
            EventType = TaskEventTypes.DependencyRemoved,
            Note = $"移除依赖: {dependency.DependsOnTaskId}",
            Payload = GuanfuJson.Serialize(new { dependency = dependency.DependsOnTaskId }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountPendingAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var counts = await PendingDependencyCounts.CountAsync(db, [taskId], cancellationToken);
        return counts.GetValueOrDefault(taskId);
    }

    /// <summary>
    /// 加这条依赖会不会形成环。
    ///
    /// 依赖图的边是「前置 → 依赖它的那个」。新加的边是
    /// prerequisite → blocked，所以只要 blocked 能顺着已有边走到 prerequisite，
    /// 加上这条就把圈闭上了。
    ///
    /// 起点必须是 blocked（不是 prerequisite）—— 走反了就检测不出来。
    /// </summary>
    private async Task<bool> WouldCreateCycleAsync(
        Guid blockedTaskId,
        Guid prerequisiteId,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var frontier = new Stack<Guid>();
        frontier.Push(blockedTaskId);

        while (frontier.Count > 0)
        {
            var current = frontier.Pop();

            if (current == prerequisiteId)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            // 谁依赖 current —— current 是它们的前置
            var dependents = await db.TaskDependencies
                .AsNoTracking()
                .Where(d => d.DependsOnTaskId == current)
                .Select(d => d.TaskId)
                .ToListAsync(cancellationToken);

            foreach (var dependent in dependents)
            {
                frontier.Push(dependent);
            }
        }

        return false;
    }
}

/// <summary>
/// 「还有几个前置没做完」的**唯一口径**。
///
/// 任务列表、任务上下文、评审包、依赖服务、开工守卫全都走这里 ——
/// 各算各的话口径一定会漂移（曾经就是：一处把「前置已删」算成还欠着，
/// 另一处算成已满足，于是列表显示动不了、真去开工却放行了）。
///
/// 语义：**前置已删 = 已满足**。依赖指向空气时不该永远挡着，
/// 否则删掉一个任务就把下游整条链永久锁死。
/// </summary>
internal static class PendingDependencyCounts
{
    internal static async Task<Dictionary<Guid, int>> CountAsync(
        IAppDbContext db,
        IReadOnlyList<Guid> taskIds,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return [];
        }

        var dependencies = await db.TaskDependencies
            .AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskId))
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        if (dependencies.Count == 0)
        {
            return [];
        }

        // 前置任务的状态一次查完 —— 别在内存里一个个问。
        var prerequisiteIds = dependencies.Select(d => d.DependsOnTaskId).Distinct().ToList();

        var prerequisites = await db.Tasks
            .AsNoTracking()
            .Where(t => prerequisiteIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Status })
            .ToListAsync(cancellationToken);

        var satisfied = prerequisites
            .Where(p => p.Status == TaskStatus.Done)
            .Select(p => p.Id)
            .ToHashSet();

        // 查不到的前置 = 已被删除 = 已满足
        var existing = prerequisites.Select(p => p.Id).ToHashSet();
        foreach (var id in prerequisiteIds.Where(id => !existing.Contains(id)))
        {
            satisfied.Add(id);
        }

        return dependencies
            .Where(d => !satisfied.Contains(d.DependsOnTaskId))
            .GroupBy(d => d.TaskId)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}

/// <summary>
/// 事件类型常量。机器消费事件账本时按这个判定，不去猜 Note 的文本。
/// </summary>
public static class TaskEventTypes
{
    public const string StatusChanged = "status.changed";
    public const string PlanSaved = "plan.saved";
    public const string ArtifactAdded = "artifact.added";
    public const string TestReported = "test.reported";
    public const string DependencyAdded = "dependency.added";
    public const string DependencyRemoved = "dependency.removed";
    public const string Assigned = "task.assigned";
    public const string Archived = "task.archived";
    public const string Note = "note";
    public const string Claimed = "task.claimed";
    public const string Released = "task.released";

    /// <summary>人把活排进 / 移出人工计划（排期干预）。</summary>
    public const string SchedulePinned = "task.schedule_pinned";

    /// <summary>任务从一个 agent 转手到另一个 agent（多 agent 协作的交接点）。</summary>
    public const string Handoff = "task.handoff";
    public const string ReviewRoundOpened = "review.round_opened";
    public const string ReviewDimensionResult = "review.dimension_result";
    public const string ReviewRoundClosed = "review.round_closed";

    /// <summary>项目规约（DoD）某一条的判定结果。</summary>
    public const string SpecVerdictResult = "review.spec_verdict";

    /// <summary>放掉工位（worktree 登记被清掉，等着清理）。</summary>
    public const string StationReleased = "task.station_released";

    /// <summary>活干完了 —— 那个工位可以收了。</summary>
    public const string StationReadyToClean = "task.station_ready_to_clean";

    /// <summary>产出采集失败（工作区读不到，产出可能不完整）。</summary>
    public const string ArtifactCaptureFailed = "artifact.capture_failed";

    /// <summary>某个评审维度被认领（多 agent 分工）。</summary>
    public const string ReviewDimensionClaimed = "review.dimension_claimed";

    /// <summary>某条完成定义被认领（多 agent 分工）。</summary>
    public const string SpecVerdictClaimed = "review.spec_claimed";
    public const string AgentLaunched = "agent.launched";
    public const string AgentExited = "agent.exited";
    public const string MilestoneAssigned = "milestone.assigned";
    public const string TypeChanged = "task.type_changed";
    public const string BranchBound = "task.branch_bound";

    /// <summary>产物被改了一版（人改 AI 写出来的东西）。</summary>
    public const string ArtifactUpdated = "artifact.updated";

    /// <summary>打了一个交付包（这个迭代版本要发出去的东西）。</summary>
    public const string DeliveryPacked = "delivery.packed";
}
