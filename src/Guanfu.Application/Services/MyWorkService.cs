using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>我卡着谁：一个活在等我的某个活。</summary>
public sealed record BlockingTaskDto(
    /// <summary>在等我的那个活的 id。</summary>
    Guid TaskId,
    /// <summary>在等我的那个活的编号。</summary>
    string? TaskKey,
    /// <summary>那个活的标题。</summary>
    string Title,
    /// <summary>它在等我的哪个活（编号）。</summary>
    string? WaitingOnKey,
    /// <summary>谁持有那个在等的活。</summary>
    string? WaitingOnHolder,
    /// <summary>那个活的状态。</summary>
    string WaitingOnStatus);

/// <summary>我放掉过的一个活。</summary>
public sealed record HandedOverDto(
    /// <summary>那个活的 id。</summary>
    Guid TaskId,
    /// <summary>那个活的编号。</summary>
    string? TaskKey,
    /// <summary>那个活的标题。</summary>
    string Title,
    /// <summary>为什么交出去。</summary>
    string? Reason,
    /// <summary>谁接了（还没人接就是 null）。</summary>
    string? TakenOverBy,
    /// <summary>什么时候交的。</summary>
    DateTimeOffset At);

/// <summary>
/// 「我现在该干什么」—— 一次调用给全。
///
/// 这是**给 agent 的**工作包，不是给人看的看板：
/// agent 问的不是「这个项目里有什么」，而是「**跟我有关的部分**是什么」：
/// 我手上正做谁的活、我卡着谁、我放掉过什么、接下来该领哪个、
/// 干活的规矩是什么、我认领了哪个维度还没交。
///
/// 这些事实散在任务、依赖、流水、规约、评审、工位六处 ——
/// 让 agent 挨个问一遍，就是十几次调用；而且它最容易漏掉「我卡着谁」那一项。
/// </summary>
public sealed record MyWorkDto(
    /// <summary>我是谁（agent id）。</summary>
    string AgentId,
    /// <summary>我正做的那个活（租约还活着的）。没有就是 null。</summary>
    TaskSummaryDto? CurrentTask,
    /// <summary>我手上所有的活（租约还活着的）。</summary>
    IReadOnlyList<TaskSummaryDto> MyTasks,
    /// <summary>我卡着谁（别人在等我的活）。</summary>
    IReadOnlyList<BlockingTaskDto> Blocking,
    /// <summary>我放掉过的活（最近若干条）。</summary>
    IReadOnlyList<HandedOverDto> HandedOver,
    /// <summary>接下来建议我做什么（带理由）。</summary>
    IReadOnlyList<DispatchSuggestionDto> Suggestions,
    /// <summary>生效的规约（目标 / 完成定义 / 提交约定 / 测试命令）。</summary>
    ProjectSpecDto? Spec,
    /// <summary>当前评审轮。</summary>
    ReviewRoundDto? Review,
    /// <summary>我认领了但还没交结论的评审维度。</summary>
    IReadOnlyList<string> PendingReviewDimensions,
    /// <summary>我的工位（在哪干活、主工作区被谁占着）。</summary>
    /// <summary>当前任务能去哪（状态机允许的迁移）。</summary>
    IReadOnlyList<string> AllowedTransitions,
    DateTimeOffset GeneratedAt);

public interface IMyWorkService
{
    /// <summary>取这个 agent 的工作包。</summary>
    Task<MyWorkDto> GetAsync(
        Guid projectId,
        string agentId,
        CancellationToken cancellationToken = default);
}

public sealed class MyWorkService(
    IAppDbContext db,
    IClock clock,
    IDispatchService dispatch,
    IReviewService reviews,
    IProjectSpecService specs) : IMyWorkService
{
    public async Task<MyWorkDto> GetAsync(
        Guid projectId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new GuanfuException("要看谁的工作包，得先说是谁。");
        }

        var now = clock.UtcNow;

        // 我在这个项目里手上还活着的活
        var mine = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId &&
                        t.ClaimedByAgentId == agentId &&
                        t.LeaseExpiresAt != null &&
                        t.LeaseExpiresAt > now &&
                        !t.IsArchived)
            .OrderBy(t => t.UpdatedAt)
            .ToListAsync(cancellationToken);

        var myIds = mine.Select(t => t.Id).ToList();

        var myTasks = mine.Select(ToSummary).ToList();
        var current = myTasks.FirstOrDefault();

        // 我卡着谁：别人在等我的活
        var blocking = await LoadBlockingAsync(projectId, myIds, cancellationToken);

        // 我放掉过什么：从账本里取（认领 / 放手事件）
        var handedOver = await LoadHandedOverAsync(projectId, agentId, cancellationToken);

        // 接下来建议我做什么（别把我自己手上的算进来）
        var suggestions = await dispatch.SuggestAsync(
            projectId, 10, excludeAgentId: agentId, cancellationToken, forAgentId: agentId);

        // 生效的规约
        var spec = await specs.GetEffectiveAsync(current?.Id ?? (await FirstTaskIdAsync(projectId, cancellationToken)), cancellationToken);

        // 当前评审轮 + 我认领了但还没交的维度
        ReviewRoundDto? review = null;
        var pendingDimensions = new List<string>();

        if (current is not null)
        {
            review = await reviews.GetCurrentAsync(current.Id, cancellationToken);

            if (review is not null)
            {
                pendingDimensions = review.Items
                    .Where(i => string.Equals(i.ClaimedBy, agentId, StringComparison.Ordinal) &&
                                i.Status == "Pending")
                    .Select(i => i.DimensionName)
                    .ToList();
            }
        }

        // 当前任务能去哪
        var transitions = current is null
            ? []
            : await LoadAllowedTransitionsAsync(current.Id, cancellationToken);

        return new MyWorkDto(
            agentId,
            current,
            myTasks,
            blocking,
            handedOver,
            suggestions,
            spec,
            review,
            pendingDimensions,
            transitions,
            now);
    }

    /// <summary>
    /// 我卡着谁：谁在等我的活。
    ///
    /// 依赖在库里只存一种方向（BlockedBy），所以「谁在等我」要反过来查：
    /// 找那些 dependsOn 指向我的活。
    /// </summary>
    private async Task<IReadOnlyList<BlockingTaskDto>> LoadBlockingAsync(
        Guid projectId,
        IReadOnlyList<Guid> myTaskIds,
        CancellationToken cancellationToken)
    {
        if (myTaskIds.Count == 0)
        {
            return [];
        }

        var deps = await db.TaskDependencies.AsNoTracking()
            .Where(d => myTaskIds.Contains(d.DependsOnTaskId))
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        if (deps.Count == 0)
        {
            return [];
        }

        var waitingTaskIds = deps.Select(d => d.TaskId).Distinct().ToList();

        var waiting = await db.Tasks.AsNoTracking()
            .Where(t => waitingTaskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key, t.Title, t.Status, t.ClaimedByAgentId })
            .ToListAsync(cancellationToken);

        var holders = await db.Tasks.AsNoTracking()
            .Where(t => myTaskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key, t.Status, t.ClaimedByAgentId })
            .ToListAsync(cancellationToken);

        var result = new List<BlockingTaskDto>();

        foreach (var w in waiting)
        {
            var waitingOn = deps.FirstOrDefault(d => d.TaskId == w.Id);
            if (waitingOn is null)
            {
                continue;
            }

            var held = holders.FirstOrDefault(h => h.Id == waitingOn.DependsOnTaskId);

            result.Add(new BlockingTaskDto(
                w.Id,
                w.Key,
                w.Title,
                held?.Key,
                held?.ClaimedByAgentId,
                held?.Status.ToString() ?? "Unknown"));
        }

        return result
            .OrderBy(b => b.TaskKey)
            .ToList();
    }

    /// <summary>我放掉过的活：放的理由、谁接了。</summary>
    private async Task<IReadOnlyList<HandedOverDto>> LoadHandedOverAsync(
        Guid projectId,
        string agentId,
        CancellationToken cancellationToken)
    {
        var taskIds = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (taskIds.Count == 0)
        {
            return [];
        }

        var events = await db.StatusEvents.AsNoTracking()
            .Where(e => taskIds.Contains(e.TaskId) &&
                        e.Actor == agentId &&
                        (e.EventType == TaskEventTypes.Released || e.EventType == TaskEventTypes.Claimed))
            .OrderByDescending(e => e.CreatedAt)
            .Take(50)
            .Select(e => new { e.TaskId, e.EventType, e.Payload, e.CreatedAt })
            .ToListAsync(cancellationToken);

        if (events.Count == 0)
        {
            return [];
        }

        var titles = await db.Tasks.AsNoTracking()
            .Where(t => events.Select(e => e.TaskId).Contains(t.Id))
            .Select(t => new { t.Id, t.Key, t.Title, t.ClaimedByAgentId })
            .ToListAsync(cancellationToken);

        var result = new List<HandedOverDto>();

        foreach (var evt in events)
        {
            if (evt.EventType != TaskEventTypes.Released)
            {
                continue;
            }

            var task = titles.FirstOrDefault(t => t.Id == evt.TaskId);
            if (task is null)
            {
                continue;
            }

            result.Add(new HandedOverDto(
                task.Id,
                task.Key,
                task.Title,
                TaskService.ExtractEventNote(evt.Payload),
                // 现在在谁手上（不是我就说明有人接了）
                string.Equals(task.ClaimedByAgentId, agentId, StringComparison.Ordinal) ? null : task.ClaimedByAgentId,
                evt.CreatedAt));
        }

        return result.Take(10).ToList();
    }

    private async Task<Guid> FirstTaskIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        return await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadAllowedTransitionsAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return task == default
            ? []
            : TaskStatusMachine.AllowedFrom(task)
                .Select(s => s.ToString())
                .ToList();
    }

    /// <summary>任务 → 摘要（与列表页同一份映射）。</summary>
    private static TaskSummaryDto ToSummary(TaskItem task) => new(
        task.Id,
        task.ProjectId,
        "",
        task.Title,
        task.Status,
        task.ClaimedByAgentId,
        task.LeaseExpiresAt,
        task.UpdatedAt,
        task.LastActivityAt,
        false,
        task.MilestoneId,
        task.Key,
        task.Type,
        task.IsArchived,
        task.Assignee,
        task.AssigneeMemberId,
        task.Priority,
        task.DueAt,
        task.Weight,
        0);
}
