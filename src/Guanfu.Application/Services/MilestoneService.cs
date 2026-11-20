using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

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
    IReadOnlyList<Guid>? Children = null,
    /// <summary>验收时间。非空 = 已签字验收。</summary>
    DateTimeOffset? AcceptedAt = null,
    /// <summary>验收人。</summary>
    string? AcceptedBy = null);

public interface IMilestoneService
{
    Task<IReadOnlyList<MilestoneDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>取单个里程碑（含进度）。</summary>
    Task<MilestoneDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

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
        MilestoneKind? kind = null,
        bool clearParent = false,
        bool clearDue = false);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>把任务挂到里程碑下（传 null 表示摘出来）。</summary>
    Task AssignTaskAsync(Guid taskId, Guid? milestoneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验收一个里程碑（人签字：这个版本做完了）。
    ///
    /// 条件：它下面（含子里程碑）的活**全都做完了**。
    /// 验收之后，里面的活不能悄悄回退 —— 要改先撤销验收。
    /// </summary>
    Task<MilestoneDto> AcceptAsync(Guid id, string? acceptedBy = null, CancellationToken cancellationToken = default);

    /// <summary>撤销验收（改动了就得先撤销 —— 签字不是永久的）。</summary>
    Task<MilestoneDto> UnacceptAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class MilestoneService(
    IAppDbContext db,
    IClock clock,
    IProjectSpecService? specs = null,
    ICurrentMemberScope? currentMember = null) : IMilestoneService
{
    private MemberScope? Scope => currentMember?.Current;

    public async Task<MilestoneDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var milestone = await db.Milestones.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException("里程碑", id);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == id)
            .ToListAsync(cancellationToken);

        return Build(milestone, tasks);
    }

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

        // 每个里程碑只算挂在自己名下的任务 —— 拿全项目的任务算，
        // 列表里每一行都会显示同一个总数与进度。
        var tasksByMilestone = tasks
            .GroupBy(t => t.MilestoneId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TaskItem>)g.ToList());

        // 子里程碑的 id 一并带上 —— 前端要能自己把树搭出来
        var childrenByParent = milestones
            .Where(m => m.ParentId is not null)
            .GroupBy(m => m.ParentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(c => c.Id).ToList());

        return milestones
            .Select(m =>
            {
                var dto = Build(m, tasksByMilestone.GetValueOrDefault(m.Id, []));
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
            throw new GuanfuException("里程碑名称不能为空。");
        }

        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
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
                throw new GuanfuException("父里程碑与子里程碑不属于同一个项目。");
            }
            await MilestoneAcceptanceLock.EnsureEditableAsync(db, projectId, parent.Id, cancellationToken);
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
        await transaction.CommitAsync(cancellationToken);

        return Build(milestone, []);
    }

    /// <summary>
    /// 改里程碑。null 表示「不动这个字段」——
    /// 所以「解除父级」「清空截止日」必须走显式的 <paramref name="clearParent"/> /
    /// <paramref name="clearDue"/>，否则这两件事永远做不到。
    /// </summary>
    public async Task<MilestoneDto> UpdateAsync(
        Guid id,
        string? name = null,
        string? description = null,
        DateTimeOffset? dueAt = null,
        int? order = null,
        CancellationToken cancellationToken = default,
        Guid? parentId = null,
        MilestoneKind? kind = null,
        bool clearParent = false,
        bool clearDue = false)
    {
        if (clearParent && parentId is not null)
        {
            throw new GuanfuException("clearParent 与 parentId 不能同时给：一个要清空，一个要设值。");
        }

        if (clearDue && dueAt is not null)
        {
            throw new GuanfuException("clearDue 与 dueAt 不能同时给：一个要清空，一个要设值。");
        }

        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                        ?? throw new NotFoundException("里程碑", id);

        var targetParent = clearParent ? null : parentId ?? milestone.ParentId;
        if (targetParent != milestone.ParentId)
        {
            await MilestoneAcceptanceLock.EnsureEditableAsync(db, milestone.ProjectId, id, cancellationToken);
            await MilestoneAcceptanceLock.EnsureEditableAsync(db, milestone.ProjectId, targetParent, cancellationToken);
        }

        if (name is not null)
        {
            milestone.Name = name.Trim();
        }

        if (description is not null)
        {
            milestone.Description = description;
        }

        if (clearDue)
        {
            milestone.DueAt = null;
        }
        else if (dueAt is not null)
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

        if (clearParent)
        {
            // 解除父级 = 变成顶层（迭代版本）
            milestone.ParentId = null;
        }
        else if (parentId is not null)
        {
            if (parentId.Value == id)
            {
                throw new GuanfuException("里程碑不能是自己的父级。");
            }

            var parent = await db.Milestones.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == parentId.Value, cancellationToken)
                ?? throw new NotFoundException("父里程碑", parentId.Value);

            if (parent.ProjectId != milestone.ProjectId)
            {
                throw new GuanfuException("父里程碑与子里程碑不属于同一个项目。");
            }

            // 挂到自己的后代底下会形成环
            if (await IsDescendantAsync(parentId.Value, id, cancellationToken))
            {
                throw new GuanfuException("这样挂会形成环（父级在自己的后代下面），已拒绝。");
            }

            milestone.ParentId = parentId.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

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
        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                        ?? throw new NotFoundException("里程碑", id);

        await MilestoneAcceptanceLock.EnsureEditableAsync(db, milestone.ProjectId, milestone.Id, cancellationToken);

        var children = await db.Milestones.Where(m => m.ParentId == id).ToListAsync(cancellationToken);
        if (children.Any(c => c.AcceptedAt is not null))
        {
            throw new GuanfuException("有子里程碑已经验收。请撤销验收后再调整版本范围。");
        }

        // 任务不跟着删，只是摘出来
        var tasks = await db.Tasks.Where(t => t.MilestoneId == id).ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            task.MilestoneId = null;
        }

        // 子里程碑也不跟着删 —— 孤儿节点比没了强。删掉父级，
        // 下面的需求组就变成孤儿了（外键是 Restrict，本来也删不掉）。
        foreach (var child in children)
        {
            child.ParentId = null;
        }

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AssignTaskAsync(Guid taskId, Guid? milestoneId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

        var before = task.MilestoneId;
        if (before == milestoneId)
        {
            return;
        }

        await MilestoneAcceptanceLock.EnsureEditableAsync(db, task.ProjectId, before, cancellationToken);

        string? milestoneName = null;

        if (milestoneId is not null)
        {
            var milestone = await db.Milestones
                .FirstOrDefaultAsync(m => m.Id == milestoneId.Value, cancellationToken)
                ?? throw new NotFoundException("里程碑", milestoneId.Value);

            if (milestone.ProjectId != task.ProjectId)
            {
                throw new GuanfuException("里程碑与任务不属于同一个项目。");
            }

            await MilestoneAcceptanceLock.EnsureEditableAsync(db, task.ProjectId, milestone.Id, cancellationToken);
            milestoneName = milestone.Name;
        }

        task.MilestoneId = milestoneId;
        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;

        // 挂载/摘出也是账本上的事：事后要能回答「这个任务什么时候进的哪个版本」
        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            // 这不是状态迁移 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Human,
            Note = milestoneId is null
                ? "摘出里程碑"
                : $"挂到里程碑「{milestoneName}」",
            EventType = TaskEventTypes.MilestoneAssigned,
            Payload = GuanfuJson.Serialize(new
            {
                from = before,
                to = milestoneId,
                milestoneName
            }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static MilestoneDto Build(Milestone milestone, IReadOnlyList<TaskItem> tasks)
    {
        // 与项目进度、度量看板同一口径：排除归档与取消，子任务归并到父任务。
        var counted = TaskProgress.Countable(tasks);
        var progress = TaskProgress.Weighted(counted);

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
            [],
            milestone.AcceptedAt,
            milestone.AcceptedBy);
    }

    public async Task<MilestoneDto> AcceptAsync(
        Guid id,
        string? acceptedBy = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCanAccept();
        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                        ?? throw new NotFoundException("里程碑", id);

        // 验收看的是**整棵子树**：版本 → 需求组 → 需求。
        // 只看直接挂着的活的话，需求组里没做完的活会被漏掉。
        var scope = await CollectScopeAsync(milestone.Id, cancellationToken);

        var pending = await db.Tasks.AsNoTracking()
            .Where(t => scope.Contains(t.MilestoneId ?? Guid.Empty) && !t.IsArchived)
            .Where(t => t.Status != TaskStatus.Done && t.Status != TaskStatus.Cancelled)
            .Select(t => new { t.Id, t.Key, t.Title })
            .ToListAsync(cancellationToken);

        if (pending.Count > 0)
        {
            var names = pending.Take(5).Select(t => t.Key ?? t.Title).ToList();
            throw new GuanfuException(
                $"还不能验收「{milestone.Name}」：下面还有 {pending.Count} 个活没做完" +
                $"（{string.Join("、", names)}{(pending.Count > 5 ? " 等" : "")}）。");
        }

        // 完成定义也要判过 —— 活做完不等于「这个版本该满足的都满足了」。
        // 判定的证据在评审轮次里（每个活评的是**生效**的那份完成定义，
        // 项目级 + 这个里程碑自己那层），这里把它们汇总起来看：
        // 有条目一条都没判过 → 没人确认过；被判成不满足 → 明确没过。
        var dodProblem = await CheckDefinitionOfDoneAsync(milestone, scope, cancellationToken);
        if (dodProblem is not null)
        {
            throw new GuanfuException(
                $"还不能验收「{milestone.Name}」：{dodProblem}");
        }

        milestone.AcceptedAt = clock.UtcNow;
        milestone.AcceptedBy = Scope?.DisplayName ??
            (string.IsNullOrWhiteSpace(acceptedBy) ? "human" : acceptedBy.Trim());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone.Id)
            .ToListAsync(cancellationToken);

        return Build(milestone, tasks);
    }

    public async Task<MilestoneDto> UnacceptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureCanAccept();
        await using var transaction = await db.BeginWriteTransactionAsync(cancellationToken);
        var milestone = await db.Milestones.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                        ?? throw new NotFoundException("里程碑", id);

        milestone.AcceptedAt = null;
        milestone.AcceptedBy = null;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone.Id)
            .ToListAsync(cancellationToken);

        return Build(milestone, tasks);
    }

    private void EnsureCanAccept()
    {
        if (Scope is { IsAdmin: false })
        {
            throw new GuanfuException("需要管理员权限才能验收或撤销验收。");
        }
    }

    /// <summary>
    /// 这个版本的完成定义判过没有。
    ///
    /// 返回 null = 没问题（没填完成定义，或都判过了且没有不满足的）；
    /// 返回一段话 = 差在哪（能照着改）。
    /// </summary>
    private async Task<string?> CheckDefinitionOfDoneAsync(
        Milestone milestone,
        IReadOnlyCollection<Guid> scope,
        CancellationToken cancellationToken)
    {
        if (specs is null)
        {
            return null;
        }

        // 生效的完成定义：项目级 + 这个里程碑自己那层
        var dod = new List<string>();
        dod.AddRange(SplitLines(await ReadProjectDoDAsync(milestone.ProjectId, cancellationToken)));
        dod.AddRange(SplitLines(await ReadMilestoneDoDAsync(milestone.Id, cancellationToken)));
        dod = dod.Distinct(StringComparer.Ordinal).ToList();

        if (dod.Count == 0)
        {
            return null;
        }

        // 这些活开过的评审轮次 → 里面的逐条判定
        var taskIds = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId.HasValue && scope.Contains(t.MilestoneId!.Value))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var roundIds = taskIds.Count == 0
            ? []
            : await db.ReviewRounds.AsNoTracking()
                .Where(r => taskIds.Contains(r.TaskId))
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);

        var verdicts = roundIds.Count == 0
            ? []
            : await db.SpecVerdicts.AsNoTracking()
                .Where(v => roundIds.Contains(v.ReviewRoundId))
                .Select(v => new { v.ItemKey, v.Status })
                .ToListAsync(cancellationToken);

        var judged = verdicts
            .GroupBy(v => v.ItemKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var unjudged = new List<string>();
        var failed = new List<string>();

        foreach (var item in dod)
        {
            var key = TaskKeys.Normalize(item);

            if (!judged.TryGetValue(key, out var itemVerdicts) || itemVerdicts.Count == 0)
            {
                unjudged.Add(item);
                continue;
            }

            if (itemVerdicts.Any(v => v.Status == ReviewStatus.Failed))
            {
                failed.Add(item);
            }
        }

        var parts = new List<string>();

        if (unjudged.Count > 0)
        {
            parts.Add($"完成定义还有 {unjudged.Count} 条没判过（{string.Join("、", unjudged.Take(3))}）");
        }

        if (failed.Count > 0)
        {
            parts.Add($"完成定义有 {failed.Count} 条被判成不满足（{string.Join("、", failed.Take(3))}）");
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private async Task<string?> ReadProjectDoDAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var spec = await db.ProjectSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);

        return spec?.DefinitionOfDone;
    }

    private async Task<string?> ReadMilestoneDoDAsync(Guid milestoneId, CancellationToken cancellationToken)
    {
        var spec = await db.MilestoneSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.MilestoneId == milestoneId, cancellationToken);

        return spec?.DefinitionOfDone;
    }

    private static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>这个里程碑下面（含所有子里程碑）的里程碑 id。</summary>
    private async Task<HashSet<Guid>> CollectScopeAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var all = await db.Milestones.AsNoTracking()
            .Select(m => new { m.Id, m.ParentId })
            .ToListAsync(cancellationToken);

        var scope = new HashSet<Guid> { rootId };
        var grew = true;

        while (grew)
        {
            grew = false;
            foreach (var milestone in all)
            {
                if (milestone.ParentId is Guid parent &&
                    scope.Contains(parent) &&
                    scope.Add(milestone.Id))
                {
                    grew = true;
                }
            }
        }

        return scope;
    }
}
