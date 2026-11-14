namespace Workbench.Domain;

/// <summary>
/// 进度口径。**项目 / 里程碑 / 度量看板共用这一份** ——
/// 三处各写一遍的话，口径一定会漂移（曾经就是：归档的任务只在列表里被排除，
/// 统计里照样算进分母，于是项目进度永远到不了 100%）。
///
/// 口径：
/// 1. **归档的任务不参与统计** —— 归档是「这条不算了」，不是「这条还没做」；
/// 2. **取消的任务排除出分母** —— 取消不是完成，留着它进度永远差一截；
/// 3. 子任务归并到父任务，避免同一件事被计两次；一个顶层任务都没有时退回全量。
///
/// 于是 进度 = 已完成权重 / (总数 - 归档 - 取消) 的权重和。
/// </summary>
public static class TaskProgress
{
    /// <summary>挑出参与统计的任务（排除归档与取消，并把子任务归并到父任务）。</summary>
    public static List<TaskItem> Countable(IEnumerable<TaskItem> tasks)
    {
        var live = tasks.Where(t => !t.IsArchived && t.Status != TaskStatus.Cancelled).ToList();

        var topLevel = live.Where(t => t.ParentTaskId == null).ToList();
        return topLevel.Count > 0 ? topLevel : live;
    }

    /// <summary>
    /// 按状态权重算进度（0~1）。空集合返回 0 ——
    /// 除零会得到 NaN，而 NaN 不是合法 JSON，会让整个接口 500。
    /// </summary>
    public static double Weighted(IReadOnlyList<TaskItem> counted)
    {
        var totalWeight = counted.Sum(t => Math.Max(t.Weight, 0.0001));

        var progress = totalWeight > 0d
            ? counted.Sum(t => TaskStatusMachine.ProgressWeight(t.Status) * Math.Max(t.Weight, 0.0001)) / totalWeight
            : 0d;

        return double.IsFinite(progress) ? progress : 0d;
    }
}