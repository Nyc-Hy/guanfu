using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;

namespace Guanfu.Application.Services;

/// <summary>
/// 交接包。
///
/// **多 agent 协作里最容易漏的一环**：上一个 agent 把活放下了，接手的人却只看到
/// 「这活在开发中」，不知道做到哪一步、为什么停下、哪些坑别再踩。
///
/// 交接的难点不是把文件传过去，是**让接手的人知道从哪儿下手**。
/// 这些事实本来就散在观复里（计划、文档、产物、测试、流水、租约、规约），
/// 这里把它们打成**一份**。
///
/// 只放结构化事实 —— 观复不生成自然语言总结（它不调模型，也不该假装会）。
/// </summary>
public interface IHandoffService
{
    /// <summary>取这个任务的交接包。</summary>
    Task<HandoffPackDto> GetAsync(Guid taskId, CancellationToken cancellationToken = default);
}

public sealed class HandoffService(
    ITaskService tasks,
    IReviewService reviews,
    IAppDbContext db,
    IClock clock) : IHandoffService
{
    public async Task<HandoffPackDto> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        // 上下文包已经是一次性信息包 —— 在它基础上补上交接特有的东西，
        // 免得两处各算各的（算出来不一样更麻烦）。
        var context = await tasks.GetContextAsync(taskId, eventLimit: 50, cancellationToken);

        var review = await reviews.GetCurrentAsync(taskId, cancellationToken);

        var lastTest = await db.TestRuns.AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .OrderByDescending(t => t.ReportedAt)
            .Select(t => new { t.Total, t.Passed, t.Failed, t.Coverage, t.Failures, t.Author, t.ReportedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var chain = await ReadChainAsync(taskId, cancellationToken);
        var handoff = SummarizeHandoff(chain, context.Task);

        return new HandoffPackDto(
            context.Task,
            context.Project,
            context.Plan,
            context.Documents,
            context.Artifacts,
            lastTest is null
                ? null
                : new TestResultDto(
                    taskId,
                    lastTest.Total,
                    lastTest.Passed,
                    lastTest.Failed,
                    lastTest.Coverage,
                    lastTest.Failures,
                    lastTest.Author,
                    lastTest.ReportedAt),
            review,
            context.Spec,
            context.RecentEvents,
            context.AllowedTransitions,
            handoff,
            chain,
            clock.UtcNow);
    }

    /// <summary>
    /// 交接链：这个活转过几次手。
    ///
    /// 一个活转手多次时，只说「上一个是谁」是不够的 ——
    /// 接手的人要看得见整条链：谁交的、为什么、什么时候。
    ///
    /// 链是从账本里的**认领 / 放手**事件拼出来的，不是另存一列 ——
    /// 那些信息本来就在事件里。
    /// </summary>
    private async Task<IReadOnlyList<HandoffStepDto>> ReadChainAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var events = await db.StatusEvents.AsNoTracking()
            .Where(e => e.TaskId == taskId &&
                        (e.EventType == TaskEventTypes.Claimed || e.EventType == TaskEventTypes.Released))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Actor, e.EventType, e.Payload, e.CreatedAt })
            .ToListAsync(cancellationToken);

        var steps = new List<HandoffStepDto>();

        // 「谁放下了，还没人来接」—— 等有人接了再合成一步
        (string Agent, string? Reason, DateTimeOffset At)? pending = null;

        // 最近一次认领时留的上下文：放手时没写理由就拿它顶上
        string? lastClaimNote = null;

        foreach (var evt in events)
        {
            if (evt.EventType == TaskEventTypes.Released)
            {
                var reason = TaskService.ExtractEventNote(evt.Payload);

                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = lastClaimNote;
                }

                pending = (evt.Actor ?? "?", reason, evt.CreatedAt);
                continue;
            }

            // 认领
            var agent = evt.Actor ?? "?";

            if (pending is { } released && !string.Equals(released.Agent, agent, StringComparison.Ordinal))
            {
                // A 放下了，B 接了 —— 这一步就是「A 交给 B」，理由是 A 写的那句
                steps.Add(new HandoffStepDto(released.At, released.Agent, agent, released.Reason));
            }
            else
            {
                var note = TaskService.ExtractEventNote(evt.Payload);
                lastClaimNote = note;
                steps.Add(new HandoffStepDto(evt.CreatedAt, null, agent, note));
            }

            pending = null;
        }

        // 最后一次放手还没人接 —— 链到这儿就断了，得让人看得见
        if (pending is { } left)
        {
            steps.Add(new HandoffStepDto(left.At, left.Agent, null, left.Reason));
        }

        return steps;
    }

    /// <summary>
    /// 上一个是谁、为什么交出来、现在在谁手上。
    ///
    /// 从**交接链**上摘最后一步 —— 别另算一套：
    /// 两处算法不一致的话，人在「交接」页签上会看到互相打架的说法。
    /// </summary>
    private static HandoffInfoDto? SummarizeHandoff(
        IReadOnlyList<HandoffStepDto> chain,
        TaskSummaryDto task)
    {
        if (chain.Count == 0)
        {
            // 没人碰过这个活 —— 没有「上一个」，但「现在在谁手上」还是有的
            return new HandoffInfoDto(null, null, null, task.ClaimedBy, task.LeaseExpiresAt);
        }

        var last = chain[^1];

        // 最后一步是「A 交给 B」→ 上一个是 A，理由是那一步的
        // 最后一步是「B 放下了」→ 上一个是 B
        // 只有第一步（第一次认领）→ 没有上一个
        var previousHolder = last.From;
        var reason = last.Reason;

        return new HandoffInfoDto(
            previousHolder,
            reason,
            last.At,
            task.ClaimedBy,
            task.LeaseExpiresAt);
    }
}
