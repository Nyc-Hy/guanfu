using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;

namespace Workbench.Application.Services;

/// <summary>
/// 交接包。
///
/// **多 agent 协作里最容易漏的一环**：上一个 agent 把活放下了，接手的人却只看到
/// 「这活在开发中」，不知道做到哪一步、为什么停下、哪些坑别再踩。
///
/// 交接的难点不是把文件传过去，是**让接手的人知道从哪儿下手**。
/// 这些事实本来就散在工作台里（计划、文档、产物、测试、流水、租约、规约），
/// 这里把它们打成**一份**。
///
/// 只放结构化事实 —— 工作台不生成自然语言总结（它不调模型，也不该假装会）。
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

        var handoff = await ReadHandoffAsync(taskId, context.Task, cancellationToken);

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
            context.Station,
            context.Spec,
            context.RecentEvents,
            context.AllowedTransitions,
            handoff,
            clock.UtcNow);
    }

    /// <summary>
    /// 上一个是谁、为什么交出来、现在在谁手上。
    ///
    /// 事实全部来自账本与租约 —— 工作台不猜「他大概是因为……」。
    /// </summary>
    private async Task<HandoffInfoDto?> ReadHandoffAsync(
        Guid taskId,
        TaskSummaryDto task,
        CancellationToken cancellationToken)
    {
        // 「上一个」= 最近一次认领或释放的人。
        var last = await db.StatusEvents.AsNoTracking()
            .Where(e => e.TaskId == taskId &&
                        (e.EventType == TaskEventTypes.Claimed || e.EventType == TaskEventTypes.Released))
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new { e.Actor, e.EventType, e.Payload, e.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (last is null)
        {
            // 没人碰过这个活 —— 没有「上一个」，但「现在在谁手上」还是有的
            return new HandoffInfoDto(null, null, null, task.ClaimedBy, task.LeaseExpiresAt);
        }

        var reason = TaskService.ExtractEventNote(last.Payload);

        if (last.EventType == TaskEventTypes.Released && string.IsNullOrWhiteSpace(reason))
        {
            // 放手时没写理由 —— 退一步看他接活时留了什么上下文
            var claim = await db.StatusEvents.AsNoTracking()
                .Where(e => e.TaskId == taskId && e.EventType == TaskEventTypes.Claimed)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.Payload)
                .FirstOrDefaultAsync(cancellationToken);

            reason = TaskService.ExtractEventNote(claim);
        }

        var previousHolder = last.Actor;

        // 现在在谁手上 —— 上一个就是现在这个人的话，不算「交接」
        var currentHolder = task.ClaimedBy;
        var isHandoff = previousHolder is not null &&
                        !string.Equals(previousHolder, currentHolder, StringComparison.Ordinal);

        return new HandoffInfoDto(
            isHandoff ? previousHolder : null,
            reason,
            last.CreatedAt,
            currentHolder,
            task.LeaseExpiresAt);
    }
}
