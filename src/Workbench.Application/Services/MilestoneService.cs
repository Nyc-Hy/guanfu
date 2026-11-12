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
    double Progress,
    /// <summary>父里程碑。为空表示顶层（迭代版本）。</summary>
    Guid? ParentId = null,
    /// <summary>种类：迭代版本还是需求组。</summary>
    MilestoneKind Kind = MilestoneKind.Release,
    /// <summary>直接子里程碑的 id。</summary>
    IReadOnlyList<Guid>? Children = null);

public interface IMilestoneService
{
    Task<IReadOnlyList<MilestoneDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<MilestoneDto> CreateAsync(
        Guid projectId,
        string name,
        string? description = null,
        DateTimeOffset? dueAt = null,
        CancellationToken cancellationToken = default,
        Guid? parentId = null,
        MilestoneKind kind = MilestoneKind.Release);

    Task<MilestoneDto> UpdateAsync(
        Guid id,
        string? name = null,
        string? description = null,
        DateTimeOffset? dueAt = null,
        int? order = null,
        CancellationToken cancellationToken = default,
        Guid? parentId = null,
        MilestoneKind? kind = null);

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

        // 子里程碑的 id 一并带上 —— 前端要能自己把树搭出来
        var childrenByParent = milestones
            .Where(m => m.ParentId is not null)
            .GroupBy(m => m.ParentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(c => c.Id).ToList());

        return milestones
            .Select(m =>
            {
                var dto = Build(m, tasks);
                return dto with { Children = childrenByParent.GetValueOrDefault(m.Id, []) };
            })
            .ToList();
    }

    public async Task<MilestoneDto> CreateAsync(
        Guid projectId,
        string name,
        string? description = null,
        DateTimeOffset? dueAt = null,
        CancellationToken cancellationToken = default,
        Guid? parentId = null,
        MilestoneKind kind = MilestoneKind.Release)
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

        // 父里程碑必须同项目 —— 否则「12月版本」和「1月版本」会串到一个项目里，
        // 层级关系就成了假的。
        if (parentId is not null)
        {
            var parent = await db.Milestones.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == parentId.Value, cancellationToken)
                ?? throw new NotFoundException("父里程碑", parentId.Value);

            if (parent.ProjectId != projectId)
            {
                throw new WorkbenchException("父里程碑与子里程碑不属于同一个项目。");
            }
        }

        var order = await db.Milestones
            .Where(m => m.ProjectId == projectId && m.ParentId == parentId)
            .Select(m => (int?)m.Order)
            .MaxAsync(cancellationToken) ?? 0;

        var milestone = new Milestone
        {
            ProjectId = projectId,
            ParentId = parentId,
            Kind = kind,
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
        CancellationToken cancellationToken = default,
        Guid? parentId = null,
        MilestoneKind? kind = null)
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

        if (kind is not null)
        {
            milestone.Kind = kind.Value;
        }

        if (parentId is not null)
        {
            if (parentId.Value == id)
            {
                throw new WorkbenchException("里程碑不能是自己的父级。");
            }

            var parent = await db.Milestones.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == parentId.Value, cancellationToken)
                ?? throw new NotFoundException("父里程碑", parentId.Value);

            if (parent.ProjectId != milestone.ProjectId)
            {
                throw new WorkbenchException("父里程碑与子里程碑不属于同一个项目。");
            }

            // 挂到自己的后代底下会形成环
            if (await IsDescendantAsync(parentId.Value, id, cancellationToken))
            {
                throw new WorkbenchException("这样挂会形成环（父级在自己的后代下面），已拒绝。");
            }

            milestone.ParentId = parentId.Value;
        }

        await db.SaveChangesAsync(cancellationToken);

        var tasks = await db.Tasks.AsNoTracking().Where(t => t.MilestoneId == id).ToListAsync(cancellationToken);
        return Build(milestone, tasks);
    }

    /// <summary>candidate 是不是 ancestor 的后代（把 candidate 挂到 ancestor 下面会不会成环）。</summary>
    private async Task<bool> IsDescendantAsync(
        Guid candidate,
        Guid ancestor,
        CancellationToken cancellationToken)
    {
        var current = candidate;
        var visited = new HashSet<Guid>();

        while (visited.Add(current))
        {
            var parentId = await db.Milestones.AsNoTracking()
                .Where(m => m.Id == current)
                .Select(m => m.ParentId)
                .FirstOrDefaultAsync(cancellationToken);

            if (parentId is null)
            {
                return false;
            }

            if (parentId.Value == ancestor)
            {
                return true;
            }

            current = parentId.Value;
        }

        return false;
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

        // 子里程碑也不跟着删 —— 孤儿节点比没了强。删掉父级，
        // 下面的需求组就变成孤儿了（外键是 Restrict，本来也删不掉）。
        var children = await db.Milestones.Where(m => m.ParentId == id).ToListAsync(cancellationToken);
        foreach (var child in children)
        {
            child.ParentId = null;
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
            Math.Round(progress, 4),
            milestone.ParentId,
            milestone.Kind,
            []);
    }
}
