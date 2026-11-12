using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

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
                    dependsOn is not null && dependsOn.Status == TaskStatus.Done,
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
            throw new WorkbenchException("任务不能依赖自己。");
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

        // Blocks 翻成对偶：库里只存「我依赖它」一种方向
        var (blockedTaskId, prerequisiteId) = type == DependencyType.Blocks
            ? (dependsOnTaskId, taskId)
            : (taskId, dependsOnTaskId);

        if (await WouldCreateCycleAsync(blockedTaskId, prerequisiteId, cancellationToken))
        {
            throw new WorkbenchException(
                "这条依赖会形成环（A 依赖 B、B 依赖 A …），已拒绝。依赖必须是有向无环的。");
        }

        var existing = await db.TaskDependencies
            .FirstOrDefaultAsync(
                d => d.TaskId == blockedTaskId && d.DependsOnTaskId == prerequisiteId,
                cancellationToken);

        if (existing is not null)
        {
            throw new WorkbenchException("这两个任务之间已经有依赖了。");
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
            Payload = WorkbenchJson.Serialize(new { dependency = prerequisiteId, note }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

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
            Payload = WorkbenchJson.Serialize(new { dependency = dependency.DependsOnTaskId }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountPendingAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var dependencies = await db.TaskDependencies
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .Select(d => d.DependsOnTaskId)
            .ToListAsync(cancellationToken);

        if (dependencies.Count == 0)
        {
            return 0;
        }

        var prerequisiteIds = dependencies.Distinct().ToList();

        var prerequisites = await db.Tasks
            .AsNoTracking()
            .Where(t => prerequisiteIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Status })
            .ToListAsync(cancellationToken);

        var existing = prerequisites.Select(p => p.Id).ToHashSet();

        // 前置任务被删了也算「已完成」—— 依赖指向空气时不该永远挡着
        return prerequisites.Count(p => p.Status != TaskStatus.Done)
               + prerequisiteIds.Count(id => !existing.Contains(id));
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
}
