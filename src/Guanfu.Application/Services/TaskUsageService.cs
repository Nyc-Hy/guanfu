using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 用量。
///
/// **只记观复自己数得出来的**：工具调用次数、用到的工具种类、事件条数、租约时长。
///
/// **刻意不记 token 与费用** —— 观复不调模型，不知道 agent 用的是哪个模型、
/// 什么价格、什么套餐，那些数字只能靠 agent 自报。自报的数字在审计上站不住
/// （查证不了的东西不要摆成看起来可查证的），所以干脆不做：
/// agent 花的「资源」用**观复能数的工具调用次数**来代理。
///
/// 「数得出来」的那部分还能**当闸门用**：项目级「每个任务最多 N 次工具调用」，
/// 超了就拒 —— agent 看见明确的上限会自己去收敛。
/// </summary>
public interface ITaskUsageService
{
    /// <summary>某个任务的用量。</summary>
    Task<TaskUsageDto> GetTaskUsageAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>整个项目的用量：按任务、按 agent 汇总。</summary>
    Task<ProjectUsageDto> GetProjectUsageAsync(
        Guid projectId,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 这个任务的「每任务工具调用上限」用得怎么样。
    ///
    /// 超了就别再调了 —— 上限是**节流阀**：拦的是「一个 agent 跑飞」，
    /// 不是安全边界，所以判定可以粗一点（数一下审计表就行）。
    /// </summary>
    Task<CallBudgetStatus> GetCallBudgetAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default);

}

/// <summary>每任务工具调用上限的用度。</summary>
public sealed record CallBudgetStatus(
    /// <summary>超了没有。</summary>
    bool Over,
    /// <summary>已经用了几次。</summary>
    int Used,
    /// <summary>上限（0 = 不限）。</summary>
    int Budget);

/// <summary>一个任务的用量。</summary>
public sealed record TaskUsageDto(
    Guid TaskId,
    string? Key,
    string Title,
    /// <summary>工具调用次数（观复自己数的）。</summary>
    int ToolCalls,
    /// <summary>用到了几种工具。</summary>
    int UniqueTools,
    /// <summary>事件条数。</summary>
    int Events,
    /// <summary>租约时长（分钟）。</summary>
    double LeaseMinutes,
    /// <summary>现在在谁手上。</summary>
    string? ClaimedBy,
    /// <summary>这个项目的每任务调用上限（0 = 不限）。</summary>
    int CallBudget);

/// <summary>一个 agent 的用量汇总。</summary>
public sealed record AgentUsageDto(
    string AgentId,
    int Tasks,
    int ToolCalls);

/// <summary>项目的用量汇总。</summary>
public sealed record ProjectUsageDto(
    Guid ProjectId,
    int TotalTasks,
    int TotalToolCalls,
    int CallBudgetPerTask,
    IReadOnlyList<TaskUsageDto> TopTasks,
    IReadOnlyList<AgentUsageDto> Agents);

public sealed class TaskUsageService(IAppDbContext db, IClock clock) : ITaskUsageService
{
    public async Task<TaskUsageDto> GetTaskUsageAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundException("任务", taskId);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        var calls = await db.ToolCallAudits.AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .Select(a => a.ToolName)
            .ToListAsync(cancellationToken);

        var events = await db.StatusEvents.AsNoTracking()
            .CountAsync(e => e.TaskId == taskId, cancellationToken);

        // 租约时长（近似）：到期时间 - 最近一次活动时间。
        // 认领之后任务还会被更新，所以这是个量级而不是精确值。
        var leaseMinutes = task.LeaseExpiresAt is DateTimeOffset expiry
            ? Math.Max(0, (expiry - task.UpdatedAt).TotalMinutes)
            : 0d;

        return new TaskUsageDto(
            task.Id,
            task.Key,
            task.Title,
            calls.Count,
            calls.Distinct(StringComparer.Ordinal).Count(),
            events,
            Math.Round(leaseMinutes, 1),
            task.ClaimedByAgentId,
            project?.MaxToolCallsPerTask ?? 0);
    }

    public async Task<ProjectUsageDto> GetProjectUsageAsync(
        Guid projectId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 200);

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId && !t.IsArchived)
            .Select(t => new { t.Id, t.Key, t.Title, t.ClaimedByAgentId })
            .ToListAsync(cancellationToken);

        var calls = await db.ToolCallAudits.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .Select(a => new { a.TaskId, a.ToolName, a.CallerId })
            .ToListAsync(cancellationToken);

        // 事件数按任务分组。用 id 列表做 IN —— 在内存里拿 tasks 去过滤翻译不成 SQL。
        var taskIds = tasks.Select(t => t.Id).ToList();
        var eventsByTask = taskIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.StatusEvents.AsNoTracking()
                .Where(e => taskIds.Contains(e.TaskId))
                .GroupBy(e => e.TaskId)
                .Select(g => new { TaskId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TaskId, x => x.Count, cancellationToken);

        var perTask = tasks.Select(t =>
        {
            var taskCalls = calls.Where(c => c.TaskId == t.Id).ToList();

            return new TaskUsageDto(
                t.Id,
                t.Key,
                t.Title,
                taskCalls.Count,
                taskCalls.Select(c => c.ToolName).Distinct(StringComparer.Ordinal).Count(),
                eventsByTask.GetValueOrDefault(t.Id),
                0,
                t.ClaimedByAgentId,
                project?.MaxToolCallsPerTask ?? 0);
        }).ToList();

        var byAgent = perTask
            .Where(t => !string.IsNullOrWhiteSpace(t.ClaimedBy))
            .GroupBy(t => t.ClaimedBy!, StringComparer.Ordinal)
            .Select(g => new AgentUsageDto(g.Key, g.Count(), g.Sum(t => t.ToolCalls)))
            .OrderByDescending(a => a.ToolCalls)
            .ToList();

        return new ProjectUsageDto(
            projectId,
            tasks.Count,
            calls.Count,
            project?.MaxToolCallsPerTask ?? 0,
            perTask.OrderByDescending(t => t.ToolCalls).ThenBy(t => t.Key).Take(take).ToList(),
            byAgent);
    }

    public async Task<CallBudgetStatus> GetCallBudgetAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var budget = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.MaxToolCallsPerTask)
            .FirstOrDefaultAsync(cancellationToken);

        if (budget <= 0)
        {
            return new CallBudgetStatus(false, 0, 0);
        }

        var used = await db.ToolCallAudits.AsNoTracking()
            .CountAsync(a => a.TaskId == taskId, cancellationToken);

        return new CallBudgetStatus(used >= budget, used, budget);
    }

}
