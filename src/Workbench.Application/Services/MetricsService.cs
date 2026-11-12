using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

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

public sealed class MetricsService(IAppDbContext db, IClock clock) : IMetricsService
{
    private const int StaleAfterHours = 72;
    private const int ThroughputWeeks = 8;

    public async Task<ProjectMetricsDto> GetProjectMetricsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                      ?? throw new NotFoundException("项目", projectId);

        var tasks = await db.Tasks.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(cancellationToken);
        var taskIds = tasks.Select(t => t.Id).ToList();

        var doneEvents = await db.StatusEvents
            .AsNoTracking()
            .Where(e => taskIds.Contains(e.TaskId) && e.ToStatus == TaskStatus.Done)
            .ToListAsync(cancellationToken);

        var testRuns = await db.TestRuns
            .AsNoTracking()
            .Where(r => taskIds.Contains(r.TaskId))
            .ToListAsync(cancellationToken);

        var openApprovals = await db.Approvals
            .AsNoTracking()
            .CountAsync(a => a.ProjectId == projectId && a.Status == ApprovalStatus.Pending, cancellationToken);

        return Build(project, tasks, doneEvents, testRuns, openApprovals);
    }

    public async Task<IReadOnlyList<ProjectMetricsDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var result = new List<ProjectMetricsDto>(projects.Count);

        foreach (var project in projects)
        {
            result.Add(await GetProjectMetricsAsync(project.Id, cancellationToken));
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
        var counted = tasks.Where(t => t.ParentTaskId == null).ToList();
        if (counted.Count == 0)
        {
            counted = tasks.ToList();
        }

        var totalWeight = counted.Sum(t => Math.Max(t.Weight, 0.0001));
        var progress = totalWeight > 0d
            ? counted.Sum(t => TaskStatusMachine.ProgressWeight(t.Status) * Math.Max(t.Weight, 0.0001)) / totalWeight
            : 0d;

        if (!double.IsFinite(progress))
        {
            progress = 0d;
        }

        var now = clock.UtcNow;

        // ---- 停滞：最近一次动静超过阈值 ----
        var stale = counted.Count(t => now - (t.LastActivityAt ?? t.UpdatedAt) > TimeSpan.FromHours(StaleAfterHours));

        // ---- 流速：最近 N 周每周完成数 ----
        var throughput = new List<ThroughputPointDto>();
        var currentWeekStart = StartOfWeek(now);
        for (var i = ThroughputWeeks - 1; i >= 0; i--)
        {
            var weekStart = currentWeekStart.AddDays(-7 * i);
            var weekEnd = weekStart.AddDays(7);
            var count = doneEvents.Count(e => e.CreatedAt >= weekStart && e.CreatedAt < weekEnd);
            throughput.Add(new ThroughputPointDto($"{weekStart:yyyy-MM-dd}", count));
        }

        // ---- 周期：从建任务到完成 ----
        var cycles = new List<double>();
        foreach (var evt in doneEvents)
        {
            var task = tasks.FirstOrDefault(t => t.Id == evt.TaskId);
            if (task is null)
            {
                continue;
            }

            var hours = (evt.CreatedAt - task.CreatedAt).TotalHours;
            if (hours >= 0)
            {
                cycles.Add(hours);
            }
        }

        // ---- 测试通过率与覆盖率：取最近一次 ----
        var latestRun = testRuns
            .OrderByDescending(r => r.ReportedAt)
            .FirstOrDefault();

        var passRate = latestRun is not null && latestRun.Total > 0
            ? (double)latestRun.Passed / latestRun.Total
            : (double?)null;

        var avgCoverage = testRuns.Count > 0
            ? testRuns.Where(r => r.Coverage is not null).Select(r => r.Coverage!.Value).DefaultIfEmpty(0).Average()
            : (double?)null;

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
            avgCoverage is null || avgCoverage.Value == 0 ? null : Math.Round(avgCoverage.Value, 4));
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        // 周一为一周之始
        var offset = ((int)value.DayOfWeek + 6) % 7;
        return value.AddDays(-offset).Date;
    }
}
