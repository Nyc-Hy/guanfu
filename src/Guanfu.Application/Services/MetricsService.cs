using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

public sealed record StatusCountDto(string Status, int Count);

public sealed record ThroughputPointDto(string Week, int Completed);

public sealed record ProjectMetricsDto(
    Guid ProjectId,
    string Name,
    int TotalTasks,
    int DoneTasks,
    int BlockedTasks,
    int StaleTasks,
    int ActiveTasks,
    double Progress,
    IReadOnlyList<StatusCountDto> StatusCounts,
    IReadOnlyList<ThroughputPointDto> Throughput,
    double? AvgCycleHours,
    double? TestPassRate,
    int OpenApprovals,
    double? AvgTestCoverage);

public interface IMetricsService
{
    Task<ProjectMetricsDto> GetProjectMetricsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectMetricsDto>> GetAllAsync(CancellationToken cancellationToken = default);
}

public sealed class MetricsService(
    IAppDbContext db,
    IClock clock,
    ICurrentMemberScope? currentMember = null) : IMetricsService
{
    /// <summary>当前身份（没人登录 = 单人本机模式 = 不过滤）。</summary>
    private MemberScope? Scope => currentMember?.Current;
    private const int StaleAfterHours = 72;
    private const int ThroughputWeeks = 8;

    public async Task<ProjectMetricsDto> GetProjectMetricsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                      ?? throw new NotFoundException("项目", projectId);

        // 可见性：计数也要按身份收 —— 否则「这个项目有 40 个活」本身就是一种泄露。
        var tasks = await db.Tasks.AsNoTracking()
            .VisibleTo(Scope)
            .Where(t => t.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        // 用子查询而不是 IN 列表 —— 任务多了 IN 列表会撞 SQLite 的参数上限
        var doneEvents = await db.StatusEvents
            .AsNoTracking()
            .Where(e => e.ToStatus == TaskStatus.Done &&
                        db.Tasks.Any(t => t.Id == e.TaskId && t.ProjectId == projectId))
            .ToListAsync(cancellationToken);

        var testRuns = await db.TestRuns
            .AsNoTracking()
            .Where(r => db.Tasks.Any(t => t.Id == r.TaskId && t.ProjectId == projectId))
            .ToListAsync(cancellationToken);

        // 过期的待审不算「待审」—— 不然这个数只会单调上涨
        var now = clock.UtcNow;
        var openApprovals = await db.Approvals
            .AsNoTracking()
            .CountAsync(a => a.ProjectId == projectId &&
                             a.Status == ApprovalStatus.Pending &&
                             a.ExpiresAt > now, cancellationToken);

        return Build(project, tasks, doneEvents, testRuns, openApprovals);
    }

    public async Task<IReadOnlyList<ProjectMetricsDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            return [];
        }

        // 一次把数据全捞出来再按项目分组 —— 逐个项目跑一遍是 1+5P 次查询
        var projectIds = projects.Select(p => p.Id).ToList();

        var allTasks = await db.Tasks.AsNoTracking()
            .VisibleTo(Scope)
            .Where(t => projectIds.Contains(t.ProjectId))
            .ToListAsync(cancellationToken);

        var allDoneEvents = await db.StatusEvents
            .AsNoTracking()
            .Where(e => e.ToStatus == TaskStatus.Done &&
                        db.Tasks.Any(t => t.Id == e.TaskId && projectIds.Contains(t.ProjectId)))
            .ToListAsync(cancellationToken);

        var allTestRuns = await db.TestRuns
            .AsNoTracking()
            .Where(r => db.Tasks.Any(t => t.Id == r.TaskId && projectIds.Contains(t.ProjectId)))
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;
        var allApprovals = await db.Approvals
            .AsNoTracking()
            .Where(a => a.ProjectId != null && projectIds.Contains(a.ProjectId.Value) &&
                        a.Status == ApprovalStatus.Pending && a.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        var tasksByProject = allTasks
            .GroupBy(t => t.ProjectId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TaskItem>)g.ToList());

        // 任务 → 项目的映射先建好。别在 GroupBy 的键选择器里写子查询 ——
        // 那会退化成每个元素查一次库。
        var projectOfTask = allTasks.ToDictionary(t => t.Id, t => t.ProjectId);

        var eventsByProject = allDoneEvents
            .Where(e => projectOfTask.ContainsKey(e.TaskId))
            .GroupBy(e => projectOfTask[e.TaskId])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StatusEvent>)g.ToList());

        var runsByProject = allTestRuns
            .Where(r => projectOfTask.ContainsKey(r.TaskId))
            .GroupBy(r => projectOfTask[r.TaskId])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TestRun>)g.ToList());

        var approvalsByProject = allApprovals
            .GroupBy(a => a.ProjectId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<ProjectMetricsDto>(projects.Count);

        foreach (var project in projects)
        {
            tasksByProject.TryGetValue(project.Id, out var projectTasks);
            eventsByProject.TryGetValue(project.Id, out var projectEvents);
            runsByProject.TryGetValue(project.Id, out var projectRuns);
            approvalsByProject.TryGetValue(project.Id, out var projectApprovals);

            result.Add(Build(
                project,
                projectTasks ?? [],
                projectEvents ?? [],
                projectRuns ?? [],
                projectApprovals));
        }

        return result;
    }

    private ProjectMetricsDto Build(
        Project project,
        IReadOnlyList<TaskItem> tasks,
        IReadOnlyList<StatusEvent> doneEvents,
        IReadOnlyList<TestRun> testRuns,
        int openApprovals)
    {
        // 与项目进度、里程碑同一口径：排除归档与取消，子任务归并到父任务
        var counted = TaskProgress.Countable(tasks);
        var progress = TaskProgress.Weighted(counted);

        var now = clock.UtcNow;

        // ---- 停滞：最近一次动静超过阈值 ----
        var stale = counted.Count(t => now - (t.LastActivityAt ?? t.UpdatedAt) > TimeSpan.FromHours(StaleAfterHours));

        var taskById = tasks.ToDictionary(t => t.Id);

        // 状态机允许 Done → Blocked → … → Done，同一个任务会有多条 Done 事件。
        // 流速与周期都只认每个任务的**最后一次** Done：不去重的话一次返工
        // 就被算成两次交付，流速虚高、平均周期偏小。
        // 归档任务的历史交付也不再计入 —— 与进度口径保持一致。
        var lastDoneByTask = doneEvents
            .Where(e => taskById.TryGetValue(e.TaskId, out var t) && !t.IsArchived)
            .GroupBy(e => e.TaskId)
            .Select(g => g.OrderByDescending(e => e.CreatedAt).First())
            .ToList();

        // ---- 流速：最近 N 周每周完成数 ----
        var throughput = new List<ThroughputPointDto>();
        var currentWeekStart = StartOfWeek(now);
        for (var i = ThroughputWeeks - 1; i >= 0; i--)
        {
            var weekStart = currentWeekStart.AddDays(-7 * i);
            var weekEnd = weekStart.AddDays(7);
            var count = lastDoneByTask.Count(e => e.CreatedAt >= weekStart && e.CreatedAt < weekEnd);
            throughput.Add(new ThroughputPointDto($"{weekStart:yyyy-MM-dd}", count));
        }

        // ---- 周期：从建任务到完成 ----
        var cycles = new List<double>();
        foreach (var evt in lastDoneByTask)
        {
            if (!taskById.TryGetValue(evt.TaskId, out var task))
            {
                continue;
            }

            var hours = (evt.CreatedAt - task.CreatedAt).TotalHours;
            if (hours >= 0)
            {
                cycles.Add(hours);
            }
        }

        // ---- 测试通过率与覆盖率 ----
        // 通过率是**项目聚合**：sum(passed)/sum(total)。
        // 以前取的是最近一次 run —— 九个任务全红、最后一个任务通过，看板就显示 100%。
        var totalTests = testRuns.Sum(r => r.Total);
        var passedTests = testRuns.Sum(r => r.Passed);

        var passRate = totalTests > 0
            ? (double)passedTests / totalTests
            : (double?)null;

        var coveredRuns = testRuns.Where(r => r.Coverage is not null).Select(r => r.Coverage!.Value).ToList();
        var avgCoverage = coveredRuns.Count > 0 ? coveredRuns.Average() : (double?)null;

        var statusCounts = Enum.GetValues<TaskStatus>()
            .Select(status => new StatusCountDto(status.ToString(), counted.Count(t => t.Status == status)))
            .Where(c => c.Count > 0)
            .ToList();

        return new ProjectMetricsDto(
            project.Id,
            project.Name,
            counted.Count,
            counted.Count(t => t.Status == TaskStatus.Done),
            counted.Count(t => t.Status == TaskStatus.Blocked),
            stale,
            counted.Count(t => !TaskStatusMachine.IsTerminal(t.Status) && t.Status != TaskStatus.Blocked),
            Math.Round(progress, 4),
            statusCounts,
            throughput,
            cycles.Count > 0 ? Math.Round(cycles.Average(), 2) : null,
            passRate is null ? null : Math.Round(passRate.Value, 4),
            openApprovals,
            // 覆盖率 0 是**测出来的 0**，不是「没数据」。
            // 以前把 0 也映射成 null，看板上「一行没覆盖」和「压根没跑过覆盖率」长得一样。
            avgCoverage is null ? null : Math.Round(avgCoverage.Value, 4));
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        // 周一为一周之始
        var offset = ((int)value.DayOfWeek + 6) % 7;
        return value.AddDays(-offset).Date;
    }
}
