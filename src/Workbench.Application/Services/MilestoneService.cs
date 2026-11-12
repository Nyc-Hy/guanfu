using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

public sealed record MilestoneDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    DateTimeOffset? DueAt,
    int Order,
    int TotalTasks,
    int DoneTasks,
    double Progress);

public interface IMilestoneService
{
    Task<IReadOnlyList<MilestoneDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<MilestoneDto> CreateAsync(
        Guid projectId,
        string name,
        string? description = null,
        DateTimeOffset? dueAt = null,
        CancellationToken cancellationToken = default);

    Task<MilestoneDto> UpdateAsync(
        Guid id,
        string? name = null,
        string? description = null,
        DateTimeOffset? dueAt = null,
        int? order = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>把任务挂到里程碑下（传 null 表示摘出来）。</summary>
    Task AssignTaskAsync(Guid taskId, Guid? milestoneId, CancellationToken cancellationToken = default);
}

public sealed class MilestoneService(IAppDbContext db, IClock clock) : IMilestoneService
{
    public async Task<IReadOnlyList<MilestoneDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var milestones = await db.Milestones
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.Order)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        if (milestones.Count == 0)
        {
            return [];
        }

        var ids = milestones.Select(m => (Guid?)m.Id).ToList();
        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.MilestoneId != null && ids.Contains(t.MilestoneId))
            .ToListAsync(cancellationToken);

        return milestones.Select(m => Build(m, tasks)).ToList();
    }

    public async Task<MilestoneDto> CreateAsync(
        Guid projectId,
        string name,
        string? description = null,
        DateTimeOffset? dueAt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WorkbenchException("里程碑名称不能为空。");
        }

        var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
        {
            throw new NotFoundException("项目", projectId);
        }

        var order = await db.Milestones
            .Where(m => m.ProjectId == projectId)
            .Select(m => (int?)m.Order)
            .MaxAsync(cancellationToken) ?? 0;

        var milestone = new Milestone
        {
            ProjectId = projectId,
            Name = name.Trim(),
            Description = description,
            DueAt = dueAt,
            Order = order + 1,
            CreatedAt = clock.UtcNow
        };

        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(cancellationToken);

        return Build(milestone, []);
    }

    public async Task<MilestoneDto> UpdateAsync(
        Guid id,
        string? name = null,
        string? description = null,
        DateTimeOffset? dueAt = null,
        int? order = null,
        CancellationToken cancellationToken = default)
    {
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                        ?? throw new NotFoundException("里程碑", id);

        if (name is not null)
        {
            milestone.Name = name.Trim();
        }

        if (description is not null)
        {
            milestone.Description = description;
        }

        if (dueAt is not null)
        {
            milestone.DueAt = dueAt;
        }

        if (order is not null)
        {
            milestone.Order = order.Value;
        }

        await db.SaveChangesAsync(cancellationToken);

        var tasks = await db.Tasks.AsNoTracking().Where(t => t.MilestoneId == id).ToListAsync(cancellationToken);
        return Build(milestone, tasks);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                        ?? throw new NotFoundException("里程碑", id);

        // 任务不跟着删，只是摘出来
        var tasks = await db.Tasks.Where(t => t.MilestoneId == id).ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            task.MilestoneId = null;
        }

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AssignTaskAsync(Guid taskId, Guid? milestoneId, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

        if (milestoneId is not null)
        {
            var milestone = await db.Milestones
                .FirstOrDefaultAsync(m => m.Id == milestoneId.Value, cancellationToken)
                ?? throw new NotFoundException("里程碑", milestoneId.Value);

            if (milestone.ProjectId != task.ProjectId)
            {
                throw new WorkbenchException("里程碑与任务不属于同一个项目。");
            }
        }

        task.MilestoneId = milestoneId;
        task.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static MilestoneDto Build(Milestone milestone, IReadOnlyList<TaskItem> tasks)
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

        return new MilestoneDto(
            milestone.Id,
            milestone.ProjectId,
            milestone.Name,
            milestone.Description,
            milestone.DueAt,
            milestone.Order,
            counted.Count,
            counted.Count(t => t.Status == TaskStatus.Done),
            Math.Round(progress, 4));
    }
}
