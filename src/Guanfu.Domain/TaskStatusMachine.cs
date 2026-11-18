namespace Guanfu.Domain;

/// <summary>
/// 任务状态机。所有状态迁移都必须经过这里，保证非法迁移被拦下、
/// 且每次迁移都留下可追溯的流水。
/// </summary>
public static class TaskStatusMachine
{
    private static readonly IReadOnlyDictionary<TaskStatus, TaskStatus[]> AllowedTransitions =
        new Dictionary<TaskStatus, TaskStatus[]>
        {
            [TaskStatus.Draft] = [TaskStatus.DocsReady, TaskStatus.Cancelled],
            [TaskStatus.DocsReady] = [TaskStatus.Planned, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Planned] = [TaskStatus.InDev, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.InDev] = [TaskStatus.Testing, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Testing] = [TaskStatus.Review, TaskStatus.InDev, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Review] = [TaskStatus.Done, TaskStatus.InDev, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Done] = [TaskStatus.Blocked],
            [TaskStatus.Blocked] =
            [
                TaskStatus.Draft, TaskStatus.DocsReady, TaskStatus.Planned,
                TaskStatus.InDev, TaskStatus.Testing, TaskStatus.Review, TaskStatus.Cancelled
            ],
            [TaskStatus.Cancelled] = [TaskStatus.Draft]
        };

    private static readonly IReadOnlyDictionary<TaskStatus, double> ProgressWeights =
        new Dictionary<TaskStatus, double>
        {
            [TaskStatus.Draft] = 0.00,
            [TaskStatus.DocsReady] = 0.10,
            [TaskStatus.Planned] = 0.20,
            [TaskStatus.InDev] = 0.50,
            [TaskStatus.Testing] = 0.70,
            [TaskStatus.Review] = 0.85,
            [TaskStatus.Done] = 1.00,
            [TaskStatus.Blocked] = 0.00,
            [TaskStatus.Cancelled] = 0.00
        };

    /// <summary>从指定状态允许迁移到的状态列表。</summary>
    public static IReadOnlyList<TaskStatus> AllowedFrom(TaskStatus status) =>
        AllowedTransitions.TryGetValue(status, out var next) ? next : [];

    public static bool CanTransition(TaskStatus from, TaskStatus to) =>
        AllowedTransitions.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    /// <summary>校验迁移合法性，非法时抛出带「当前允许列表」的异常。</summary>
    public static void EnsureTransition(TaskStatus from, TaskStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStatusTransitionException(from, to, AllowedFrom(from));
        }
    }

    /// <summary>状态对应的进度权重（0~1）。</summary>
    public static double ProgressWeight(TaskStatus status) =>
        ProgressWeights.TryGetValue(status, out var w) ? w : 0d;

    public static bool IsTerminal(TaskStatus status) =>
        status is TaskStatus.Done or TaskStatus.Cancelled;

    /// <summary>是否允许直接阻塞/取消的通用出口状态。</summary>
    public static bool IsExceptionState(TaskStatus status) =>
        status is TaskStatus.Blocked or TaskStatus.Cancelled;
}
