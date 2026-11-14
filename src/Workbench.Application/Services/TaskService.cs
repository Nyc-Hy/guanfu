using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

public interface ITaskService
{
    Task<TaskSummaryDto> CreateAsync(
        Guid projectId,
        string title,
        string? description = null,
        Guid? parentTaskId = null,
        Guid? milestoneId = null,
        TaskType type = TaskType.Requirement,
        string? branch = null,
        string? key = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列任务。
    ///
    /// 末尾几个是可选筛选/分页参数（放在 CancellationToken 之后是为了不破坏
    /// 现有位置参数调用）。limit/offset 不给就是全量 ——
    /// 但列表端点应该都带上 limit，免得任务多了把 agent 的上下文撑爆。
    /// </summary>
    Task<IReadOnlyList<TaskSummaryDto>> ListAsync(
        Guid? projectId = null,
        TaskStatus? status = null,
        string? keyword = null,
        Guid? milestoneId = null,
        string? key = null,
        TaskType? type = null,
        CancellationToken cancellationToken = default,
        string? branch = null,
        int? limit = null,
        int? offset = null,
        bool includeArchived = false);

    /// <summary>列任务的事件流水（新到旧）。账本得能单独查，不然只是写进去没人看。</summary>
    Task<IReadOnlyList<StatusEventDto>> ListEventsAsync(
        Guid id,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>归档 / 取消归档。归档不是删除，流水和产物都留着。</summary>
    Task<TaskSummaryDto> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);

    Task<TaskSummaryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>改任务类型（需求/缺陷/优化）。编号保持不变 —— 编号已经被引用了，不能说变就变。</summary>
    Task<TaskSummaryDto> SetTypeAsync(Guid id, TaskType type, CancellationToken cancellationToken = default);

    /// <summary>绑定 / 解绑开发分支。传 null 或空串表示解绑。</summary>
    Task<TaskSummaryDto> BindBranchAsync(Guid id, string? branch, CancellationToken cancellationToken = default);

    Task<TaskSummaryDto> ChangeStatusAsync(
        Guid id,
        TaskStatus to,
        string? note = null,
        TaskEventSource source = TaskEventSource.System,
        string? actor = null,
        CancellationToken cancellationToken = default);

    Task<TaskSummaryDto> ClaimAsync(
        Guid id,
        string agentId,
        int leaseMinutes = 30,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid id, string agentId, CancellationToken cancellationToken = default);

    Task<PlanDto> SetPlanAsync(
        Guid id,
        string? background,
        string? understanding,
        string? breakdown,
        string? acceptanceCriteria,
        string? risks,
        string? author,
        CancellationToken cancellationToken = default);

    Task<ArtifactDto> AddArtifactAsync(
        Guid id,
        ArtifactType type,
        string title,
        string? content = null,
        string? externalPath = null,
        string? commitSha = null,
        string? branch = null,
        string? author = null,
        CancellationToken cancellationToken = default);

    Task<TestResultDto> ReportTestResultAsync(
        Guid id,
        int total,
        int passed,
        string? failures = null,
        double? coverage = null,
        string? author = null,
        CancellationToken cancellationToken = default);

    Task<StatusEventDto> PostEventAsync(
        Guid id,
        string message,
        TaskEventSource source = TaskEventSource.System,
        string? actor = null,
        CancellationToken cancellationToken = default);

    Task<TaskContextDto> GetContextAsync(Guid id, int eventLimit = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置分派信息：负责人、优先级、截止日、权重。
    /// 传 null 的字段表示不动（不是清空 —— 清空要走显式的 null 值）。
    /// </summary>
    Task<TaskSummaryDto> SetAssignmentAsync(
        Guid id,
        string? assignee = null,
        TaskPriority? priority = null,
        DateTimeOffset? dueAt = null,
        double? weight = null,
        CancellationToken cancellationToken = default);
}

public sealed class TaskService(
    IAppDbContext db,
    IClock clock,
    IGitInspector git,
    IReviewGate? reviewGate = null,
    IBranchGuard? branchGuard = null) : ITaskService
{
    public const int StaleAfterHours = 72;

    public async Task<TaskSummaryDto> CreateAsync(
        Guid projectId,
        string title,
        string? description = null,
        Guid? parentTaskId = null,
        Guid? milestoneId = null,
        TaskType type = TaskType.Requirement,
        string? branch = null,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new WorkbenchException("任务标题不能为空。");
        }

        var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
        {
            throw new NotFoundException("项目", projectId);
        }

        // 里程碑要属于本项目，否则「12月版本」这种归属就是假的
        if (milestoneId is not null)
        {
            var milestoneBelongs = await db.Milestones
                .AnyAsync(m => m.Id == milestoneId.Value && m.ProjectId == projectId, cancellationToken);

            if (!milestoneBelongs)
            {
                throw new WorkbenchException($"里程碑 {milestoneId} 不属于项目 {projectId}。");
            }
        }

        // 父任务同理。不校验的话，父任务在别的项目（或压根不存在），
        // 子任务会被当成「子任务」从项目统计里剔掉 —— 项目进度永远到不了 100%。
        if (parentTaskId is not null)
        {
            var parent = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == parentTaskId.Value, cancellationToken);

            if (parent is null)
            {
                throw new NotFoundException("父任务", parentTaskId.Value);
            }

            if (parent.ProjectId != projectId)
            {
                throw new WorkbenchException($"父任务 {parentTaskId} 不属于项目 {projectId}。");
            }
        }

        var explicitKey = !string.IsNullOrWhiteSpace(key);
        TaskItem task;

        // 编号可能撞车：并发建任务时两个请求会算出同一个号。
        // 唯一索引挡得住，撞了就重算一个再来。
        for (var attempt = 0; ; attempt++)
        {
            var resolvedKey = await ResolveKeyAsync(projectId, type, key, cancellationToken);

            task = new TaskItem
            {
                ProjectId = projectId,
                ParentTaskId = parentTaskId,
                MilestoneId = milestoneId,
                Type = type,
                Branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
                Key = resolvedKey,
                Title = title.Trim(),
                Description = description,
                Status = TaskStatus.Draft,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
                LastActivityAt = clock.UtcNow
            };

            db.Tasks.Add(task);

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = task.Id,
                FromStatus = null,
                ToStatus = TaskStatus.Draft,
                Source = TaskEventSource.System,
                Note = "任务创建",
                EventType = TaskEventTypes.StatusChanged,
                Payload = WorkbenchJson.Serialize(new
                {
                    from = (string?)null,
                    to = TaskStatus.Draft.ToString(),
                    key = task.Key
                }),
                CreatedAt = clock.UtcNow
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (explicitKey)
            {
                db.ClearTracking();
                throw new WorkbenchException($"编号 {key} 在该项目里已经有了。");
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // 并发算出了同一个号 —— 清掉跟踪状态，重算一个号再来
                db.ClearTracking();
            }
        }

        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, cancellationToken);
        return ToSummary(task, project);
    }

    public async Task<IReadOnlyList<TaskSummaryDto>> ListAsync(
        Guid? projectId = null,
        TaskStatus? status = null,
        string? keyword = null,
        Guid? milestoneId = null,
        string? key = null,
        TaskType? type = null,
        CancellationToken cancellationToken = default,
        string? branch = null,
        int? limit = null,
        int? offset = null,
        bool includeArchived = false)
    {
        var query = db.Tasks.AsNoTracking();

        if (!includeArchived)
        {
            // 归档的任务默认不出现
            query = query.Where(t => !t.IsArchived);
        }

        if (projectId is not null)
        {
            query = query.Where(t => t.ProjectId == projectId.Value);
        }

        if (status is not null)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        if (milestoneId is not null)
        {
            query = query.Where(t => t.MilestoneId == milestoneId.Value);
        }

        if (type is not null)
        {
            query = query.Where(t => t.Type == type.Value);
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            var b = branch.Trim();
            query = query.Where(t => t.Branch != null && t.Branch == b);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // Contains 在 SQLite 里翻成 instr()，大小写敏感 —— 搜 "login" 找不到 "Login"。
            // 两边都 lower 一下再比。SQLite 的 lower() 只管 ASCII，但中文本来就没有大小写，
            // 真正受影响的只有英文标识符，那正是要照顾的地方。
            // 不用 LIKE：LIKE 的 % 和 _ 是通配符，而 SQLite 默认没有转义符，
            // 搜 "100%" 会匹配到 "1000"，反而更不可预测。
            var k = keyword.Trim().ToLowerInvariant();
            query = query.Where(t => t.Title.ToLower().Contains(k)
                                     || (t.Description != null && t.Description.ToLower().Contains(k)));
        }

        var tasks = await query.OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);

        // 编号匹配在内存里做：人嘴里说的是「req001」，库里存的是「REQ-001」，
        // 差一个连字符。归一化后（只留字母数字、转小写）两边才认得出是同一个。
        if (!string.IsNullOrWhiteSpace(key))
        {
            var k = NormalizeKey(key);
            tasks = tasks.Where(t => t.Key is not null && NormalizeKey(t.Key) == k).ToList();
        }

        // 分页。offset 跳过前 N 条，limit 截断。
        if (offset is > 0)
        {
            tasks = tasks.Skip(offset.Value).ToList();
        }

        if (limit is > 0)
        {
            tasks = tasks.Take(limit.Value).ToList();
        }

        if (tasks.Count == 0)
        {
            return [];
        }

        var projectIds = tasks.Select(t => t.ProjectId).Distinct().ToList();
        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // 未完成的前置任务数。一次查完，别一个任务查一次。
        var pendingCounts = await PendingDependencyCountsAsync(
            tasks.Select(t => t.Id).ToList(), cancellationToken);

        return tasks
            .Select(t => ToSummary(
                t,
                projects.TryGetValue(t.ProjectId, out var p) ? p : null,
                pendingCounts.GetValueOrDefault(t.Id)))
            .ToList();
    }

    /// <summary>批量算「还没完成的前置任务数」。口径见 <see cref="PendingDependencyCounts"/>。</summary>
    private Task<Dictionary<Guid, int>> PendingDependencyCountsAsync(
        IReadOnlyList<Guid> taskIds,
        CancellationToken cancellationToken) =>
        PendingDependencyCounts.CountAsync(db, taskIds, cancellationToken);

    /// <summary>
    /// 「返回任务摘要」的统一出口：带上项目名与真实的前置欠账。
    /// 别在各处各写一遍 —— 漏掉 pending 就等于告诉 agent「没人挡着你」。
    /// </summary>
    private async Task<TaskSummaryDto> SummaryWithPendingAsync(TaskItem task, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        var pending = await PendingDependencyCountsAsync([task.Id], cancellationToken);
        return ToSummary(task, project, pending.GetValueOrDefault(task.Id));
    }

    public async Task<TaskSummaryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        // 真算，别恒给 0 —— DTO 上写着「大于 0 表示动不了」，
        // 恒 0 会让 agent 以为随时能开工，撞在守卫上才发现不是那么回事。
        var pending = await PendingDependencyCountsAsync([id], cancellationToken);
        return ToSummary(task, project, pending.GetValueOrDefault(id));
    }

    public async Task<TaskSummaryDto> SetTypeAsync(
        Guid id,
        TaskType type,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        if (task.Type != type)
        {
            var before = task.Type;
            task.Type = type;
            task.UpdatedAt = clock.UtcNow;
            task.LastActivityAt = clock.UtcNow;

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = task.Id,
                // 这不是状态迁移 —— from/to 留空，消费方才能统一判定
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Human,
                Note = $"类型变更: {TaskTypes.Name(before)} → {TaskTypes.Name(type)}（编号 {task.Key} 不变）",
                EventType = TaskEventTypes.TypeChanged,
                Payload = WorkbenchJson.Serialize(new
                {
                    from = before.ToString(),
                    to = type.ToString(),
                    key = task.Key
                }),
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        return await SummaryWithPendingAsync(task, cancellationToken);
    }

    public async Task<TaskSummaryDto> BindBranchAsync(
        Guid id,
        string? branch,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        var value = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();

        if (!string.Equals(task.Branch, value, StringComparison.Ordinal))
        {
            var before = task.Branch;
            task.Branch = value;
            task.UpdatedAt = clock.UtcNow;
            task.LastActivityAt = clock.UtcNow;

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = task.Id,
                // 这不是状态迁移 —— from/to 留空
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Human,
                Note = before is null
                    ? $"绑定分支: {value}"
                    : value is null
                        ? $"解绑分支: {before}"
                        : $"绑定分支: {before} → {value}",
                EventType = TaskEventTypes.BranchBound,
                Payload = WorkbenchJson.Serialize(new { from = before, to = value }),
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        return await SummaryWithPendingAsync(task, cancellationToken);
    }

    public async Task<TaskSummaryDto> ChangeStatusAsync(
        Guid id,
        TaskStatus to,
        string? note = null,
        TaskEventSource source = TaskEventSource.System,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        var from = task.Status;
        TaskStatusMachine.EnsureTransition(from, to);

        // 分支守卫：绑了分支的任务，只有仓库当前就在那个分支上才允许开跑
        if (to == TaskStatus.InDev && branchGuard is not null)
        {
            await EnsureBranchAsync(id, cancellationToken);
        }

        // 依赖守卫：前置任务没做完就不许开写代码
        if (to == TaskStatus.InDev)
        {
            await EnsureDependenciesSatisfiedAsync(id, cancellationToken);
        }

        // 结项门禁：开过评审的任务，当前轮必须全部维度通过。
        // 没开过评审的老流程不受影响（门禁只在有评审记录时生效）。
        if (from == TaskStatus.Review && to == TaskStatus.Done && reviewGate is not null)
        {
            var gate = await reviewGate.GetGateAsync(id, cancellationToken);

            if (gate.HasReview &&
                (gate.PendingDimensions.Count > 0 || gate.FailedDimensions.Count > 0 ||
                 gate.BlockReason is not null))
            {
                throw new ReviewGateException(gate);
            }
        }

        var now = clock.UtcNow;
        var terminal = TaskStatusMachine.IsTerminal(to);

        // 条件更新：WHERE Status = from。
        // 「先读后写」挡不住并发 —— 两个请求都读到 Review，各自校验完门禁再写回，
        // 后写的把先写的盖掉，评审门禁的 TOCTOU 就是这么来的。
        // 影响行数 0 说明状态在这期间被人改了，这次迁移**没有生效**，必须报冲突。
        // 终态清租约也放进同一条 UPDATE：分两步写的话，中间被别的 agent 认领就会清掉新租约。
        var affected = terminal
            ? await db.Tasks
                .Where(t => t.Id == id && t.Status == from)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, to)
                    .SetProperty(t => t.UpdatedAt, now)
                    .SetProperty(t => t.LastActivityAt, now)
                    .SetProperty(t => t.ClaimedByAgentId, (string?)null)
                    .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken)
            : await db.Tasks
                .Where(t => t.Id == id && t.Status == from)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, to)
                    .SetProperty(t => t.UpdatedAt, now)
                    .SetProperty(t => t.LastActivityAt, now), cancellationToken);

        if (affected == 0)
        {
            var current = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => t.Status)
                .FirstOrDefaultAsync(cancellationToken);

            throw new StatusConflictException(current, to, TaskStatusMachine.AllowedFrom(current));
        }

        // ExecuteUpdateAsync 绕过变更跟踪 —— 被跟踪的那份还是旧值。
        // 不同步的话，下面写流水时的 SaveChanges 会把旧状态整行写回去，迁移当场丢失。
        task.Status = to;
        task.UpdatedAt = now;
        task.LastActivityAt = now;

        if (terminal)
        {
            task.ClaimedByAgentId = null;
            task.LeaseExpiresAt = null;
        }

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = task.Id,
            FromStatus = from,
            ToStatus = to,
            Source = source,
            Actor = actor,
            Note = note,
            EventType = TaskEventTypes.StatusChanged,
            Payload = WorkbenchJson.Serialize(new
            {
                from = from.ToString(),
                to = to.ToString(),
                actor
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return await SummaryWithPendingAsync(task, cancellationToken);
    }

    public async Task<TaskSummaryDto> ClaimAsync(
        Guid id,
        string agentId,
        int leaseMinutes = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new WorkbenchException("agentId 不能为空。");
        }

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        // 前置任务没做完就不能领 —— 依赖不是摆设。
        // 不拦的话 agent 会拿着没做完的前置开跑，出来的东西自然是错的。
        await EnsureDependenciesSatisfiedAsync(id, cancellationToken);

        // 领取 = agent 准备开跑，绑定分支的任务必须在对的分支上才能领
        if (branchGuard is not null)
        {
            await EnsureBranchAsync(id, cancellationToken);
        }

        var now = clock.UtcNow;
        var leaseAlive = task.ClaimedByAgentId is not null &&
                        task.LeaseExpiresAt is not null &&
                        task.LeaseExpiresAt.Value > now;

        if (leaseAlive && !string.Equals(task.ClaimedByAgentId, agentId, StringComparison.Ordinal))
        {
            throw new WorkbenchException(
                $"任务已被 {task.ClaimedByAgentId} 认领，租约到 {task.LeaseExpiresAt:u}。");
        }

        var expiresAt = now.AddMinutes(Math.Clamp(leaseMinutes, 1, 24 * 60));

        // 原子认领：条件更新抢租约。
        // 「先读后写」挡不住并发 —— 两个 agent 会同时读到租约已空，
        // 然后各写各的，后写的把先写的覆盖掉，先到的 agent 还以为自己还在跑。
        var affected = await db.Tasks
            .Where(t => t.Id == id &&
                        (t.ClaimedByAgentId == null ||
                         t.LeaseExpiresAt == null ||
                         t.LeaseExpiresAt <= now ||
                         t.ClaimedByAgentId == agentId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedByAgentId, agentId)
                .SetProperty(t => t.LeaseExpiresAt, expiresAt)
                .SetProperty(t => t.UpdatedAt, now)
                .SetProperty(t => t.LastActivityAt, now), cancellationToken);

        if (affected == 0)
        {
            throw new WorkbenchException("任务刚被别的 agent 认领了，请重新领取。");
        }

        // ExecuteUpdateAsync 绕过变更跟踪，被跟踪的那份还是旧值 ——
        // 不同步的话，后面任何一次 SaveChanges 都会把旧的 ClaimedByAgentId 写回去，
        // 刚抢到的租约当场丢失。
        task.ClaimedByAgentId = agentId;
        task.LeaseExpiresAt = expiresAt;
        task.UpdatedAt = now;
        task.LastActivityAt = now;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = id,
            // 这不是状态迁移，只是认领 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = agentId,
            Note = $"认领任务，租约 {expiresAt:u} 到期",
            EventType = TaskEventTypes.Claimed,
            Payload = WorkbenchJson.Serialize(new
            {
                agentId,
                leaseExpiresAt = expiresAt,
                leaseMinutes = Math.Clamp(leaseMinutes, 1, 24 * 60)
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return await SummaryWithPendingAsync(task, cancellationToken);
    }

    public async Task ReleaseAsync(Guid id, string agentId, CancellationToken cancellationToken = default)
    {
        var exists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException("任务", id);
        }

        var now = clock.UtcNow;

        // 条件更新：WHERE 租约持有者 = 调用方。
        // 「先读后写」会清掉别人的租约 —— 读的时候还是自己持有，
        // 写之前租约过期、别的 agent 接管了，这一写就把新 agent 的租约抹掉了。
        var affected = await db.Tasks
            .Where(t => t.Id == id && t.ClaimedByAgentId == agentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedByAgentId, (string?)null)
                .SetProperty(t => t.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(t => t.UpdatedAt, now)
                .SetProperty(t => t.LastActivityAt, now), cancellationToken);

        if (affected == 0)
        {
            throw new WorkbenchException(
                $"任务不由 {agentId} 持有，无法释放（租约可能已过期并被别的 agent 接管）。");
        }

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = id,
            // 这不是状态迁移 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = agentId,
            Note = "释放任务认领",
            EventType = TaskEventTypes.Released,
            Payload = WorkbenchJson.Serialize(new { agentId }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlanDto> SetPlanAsync(
        Guid id,
        string? background,
        string? understanding,
        string? breakdown,
        string? acceptanceCriteria,
        string? risks,
        string? author,
        CancellationToken cancellationToken = default)
    {
        // 版本号可能撞车：并发回写时两个请求会算出同一个 Max+1。
        // (TaskId, Version) 唯一索引挡得住，撞了就重算一个再来 ——
        // 与任务编号、评审轮次的既有模式一致。
        for (var attempt = 0; ; attempt++)
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                       ?? throw new NotFoundException("任务", id);

            var version = await db.Plans
                .Where(p => p.TaskId == id)
                .Select(p => (int?)p.Version)
                .MaxAsync(cancellationToken) ?? 0;

            var plan = new Plan
            {
                TaskId = id,
                Version = version + 1,
                Background = background,
                Understanding = understanding,
                Breakdown = breakdown,
                AcceptanceCriteria = acceptanceCriteria,
                Risks = risks,
                Author = author,
                CreatedAt = clock.UtcNow
            };

            db.Plans.Add(plan);
            task.UpdatedAt = clock.UtcNow;
            task.LastActivityAt = clock.UtcNow;

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = id,
                // 这不是状态迁移 —— from/to 留空
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Agent,
                Actor = author,
                Note = $"回写开发计划 v{plan.Version}",
                EventType = TaskEventTypes.PlanSaved,
                Payload = WorkbenchJson.Serialize(new { version = plan.Version, author }),
                CreatedAt = clock.UtcNow
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToPlanDto(plan);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.ClearTracking();
            }
        }
    }

    public async Task<ArtifactDto> AddArtifactAsync(
        Guid id,
        ArtifactType type,
        string title,
        string? content = null,
        string? externalPath = null,
        string? commitSha = null,
        string? branch = null,
        string? author = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        var artifact = new Artifact
        {
            TaskId = id,
            Type = type,
            Title = string.IsNullOrWhiteSpace(title) ? type.ToString() : title.Trim(),
            Content = content,
            ExternalPath = externalPath,
            CommitSha = commitSha,
            Branch = branch,
            Author = author,
            CreatedAt = clock.UtcNow
        };

        db.Artifacts.Add(artifact);
        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = id,
            // 这不是状态迁移 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = author,
            Note = $"回写产物 [{artifact.Title}]",
            EventType = TaskEventTypes.ArtifactAdded,
            Payload = WorkbenchJson.Serialize(new
            {
                artifactId = artifact.Id,
                artifactType = type.ToString(),
                title = artifact.Title,
                commitSha,
                branch,
                externalPath
            }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        return ToArtifactDto(artifact);
    }

    public async Task<TestResultDto> ReportTestResultAsync(
        Guid id,
        int total,
        int passed,
        string? failures = null,
        double? coverage = null,
        string? author = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        // 不校验的话会算出「通过率 > 1」这种数，指标看板直接挂
        if (total < 0)
        {
            throw new WorkbenchException("测试总数不能为负数。");
        }

        if (passed < 0)
        {
            throw new WorkbenchException("测试通过数不能为负数。");
        }

        if (passed > total)
        {
            throw new WorkbenchException($"测试通过数 {passed} 不能大于总数 {total}。");
        }

        var failed = total - passed;
        var summary = $"单元测试 {passed}/{total} 通过";
        if (coverage is not null)
        {
            summary += $"，覆盖率 {coverage:P1}";
        }

        var artifact = new Artifact
        {
            TaskId = id,
            Type = ArtifactType.TestReport,
            Title = summary,
            Content = failures,
            Author = author,
            CreatedAt = clock.UtcNow
        };

        db.Artifacts.Add(artifact);

        db.TestRuns.Add(new TestRun
        {
            TaskId = id,
            Total = total,
            Passed = passed,
            Failed = failed,
            Coverage = coverage,
            Failures = failures,
            Author = author,
            ReportedAt = clock.UtcNow
        });

        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = id,
            // 这不是状态迁移 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Agent,
            Actor = author,
            Note = summary,
            EventType = TaskEventTypes.TestReported,
            Payload = WorkbenchJson.Serialize(new { total, passed, failed, coverage }),
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

        return new TestResultDto(id, total, passed, failed, coverage, failures, author, clock.UtcNow);
    }

    public async Task<StatusEventDto> PostEventAsync(
        Guid id,
        string message,
        TaskEventSource source = TaskEventSource.System,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        var evt = new StatusEvent
        {
            TaskId = id,
            // 这不是状态迁移 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = source,
            Actor = actor,
            Note = message,
            EventType = TaskEventTypes.Note,
            Payload = WorkbenchJson.Serialize(new { message }),
            CreatedAt = clock.UtcNow
        };

        db.StatusEvents.Add(evt);
        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return ToEventDto(evt);
    }

    /// <summary>
    /// 列任务的事件流水（新到旧）。
    ///
    /// 账本是工作台三大能力之一，但以前只能从上下文里捞最近 N 条 ——
    /// 想翻一个任务完整的时间线做不到。
    /// </summary>
    public async Task<IReadOnlyList<StatusEventDto>> ListEventsAsync(
        Guid id,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException("任务", id);
        }

        var events = await db.StatusEvents
            .AsNoTracking()
            .Where(e => e.TaskId == id)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return events.Select(ToEventDto).ToList();
    }

    /// <summary>归档 / 取消归档。归档不是删除 —— 流水、产物、评审全都留着。</summary>
    public async Task<TaskSummaryDto> SetArchivedAsync(
        Guid id,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        if (task.IsArchived != archived)
        {
            task.IsArchived = archived;
            task.UpdatedAt = clock.UtcNow;
            task.LastActivityAt = clock.UtcNow;

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = id,
                // 这不是状态迁移 —— from/to 留空
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Human,
                Note = archived ? "归档任务" : "取消归档",
                EventType = TaskEventTypes.Archived,
                Payload = WorkbenchJson.Serialize(new { archived }),
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        return await SummaryWithPendingAsync(task, cancellationToken);
    }

    /// <summary>
    /// 设置分派信息：负责人、优先级、截止日、权重。
    /// 传 null 的字段不动 —— 清空用空串（负责人）或专门的 API。
    /// </summary>
    public async Task<TaskSummaryDto> SetAssignmentAsync(
        Guid id,
        string? assignee = null,
        TaskPriority? priority = null,
        DateTimeOffset? dueAt = null,
        double? weight = null,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        var changes = new List<string>();

        if (assignee is not null)
        {
            var value = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim();
            if (task.Assignee != value)
            {
                task.Assignee = value;
                changes.Add($"负责人 → {value ?? "(未分派)"}");
            }
        }

        if (priority is not null && task.Priority != priority.Value)
        {
            task.Priority = priority.Value;
            changes.Add($"优先级 → {priority.Value}");
        }

        if (dueAt is not null && task.DueAt != dueAt.Value)
        {
            task.DueAt = dueAt.Value;
            changes.Add($"截止日 → {dueAt.Value:u}");
        }

        if (weight is not null)
        {
            // 权重不能是 0 或负数 —— 进度是「权重和」的比例，
            // 出现 0 权重会让分母为 0，整个项目的进度就废了。
            var value = Math.Clamp(weight.Value, 0.01, 1000d);
            if (Math.Abs(task.Weight - value) > 0.0001)
            {
                task.Weight = value;
                changes.Add($"权重 → {value}");
            }
        }

        if (changes.Count > 0)
        {
            task.UpdatedAt = clock.UtcNow;
            task.LastActivityAt = clock.UtcNow;

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = id,
                // 这不是状态迁移 —— from/to 留空
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.Human,
                Note = string.Join("；", changes),
                EventType = TaskEventTypes.Assigned,
                Payload = WorkbenchJson.Serialize(new
                {
                    assignee = task.Assignee,
                    priority = task.Priority.ToString(),
                    dueAt = task.DueAt,
                    weight = task.Weight
                }),
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        return await SummaryWithPendingAsync(task, cancellationToken);
    }

    public async Task<TaskContextDto> GetContextAsync(
        Guid id,
        int eventLimit = 20,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        var plan = await db.Plans
            .AsNoTracking()
            .Where(p => p.TaskId == id)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(cancellationToken);

        var documents = await db.Documents
            .AsNoTracking()
            .Where(d => d.TaskId == id)
            .OrderBy(d => d.UploadedAt)
            .ToListAsync(cancellationToken);

        var artifacts = await db.Artifacts
            .AsNoTracking()
            .Where(a => a.TaskId == id)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var events = await db.StatusEvents
            .AsNoTracking()
            .Where(e => e.TaskId == id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(eventLimit, 1, 200))
            .ToListAsync(cancellationToken);

        var gitState = project?.RepoPath is null ? null : git.Inspect(project.RepoPath);

        var projectContext = new ProjectContextDto(
            project?.Id ?? task.ProjectId,
            project?.Name ?? "(未知项目)",
            project?.RepoPath,
            gitState?.Branch ?? project?.DefaultBranch,
            gitState?.HeadSha,
            gitState?.Dirty ?? false,
            project?.RepoRemote);

        // 分支就绪与否一并带出去：agent 拿到上下文就知道现在能不能开跑
        var branchCheck = branchGuard is null
            ? null
            : await branchGuard.CheckAsync(id, cancellationToken);

        // 前置欠账也要真算 —— 上下文是 agent 开工前唯一的一次性信息包，
        // 这里给 0 就等于告诉它「没人挡着你」。
        var pending = await PendingDependencyCountsAsync([id], cancellationToken);

        return new TaskContextDto(
            ToSummary(task, project, pending.GetValueOrDefault(id)),
            projectContext,
            plan is null ? null : ToPlanDto(plan),
            documents.Select(ToDocumentDto).ToList(),
            artifacts.Select(ToArtifactDto).ToList(),
            events.Select(ToEventDto).ToList(),
            TaskStatusMachine.AllowedFrom(task.Status).Select(s => s.ToString()).ToList(),
            gitState?.Branch,
            branchCheck?.Ok ?? true);
    }

    /// <summary>
    /// 分支守卫。绑定了分支的任务，只有仓库当前分支与之一致时才放行。
    /// 错误信息要带上「当前在哪个分支」，agent 才能自己纠正。
    /// </summary>
    private async Task EnsureBranchAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var check = await branchGuard!.CheckAsync(taskId, cancellationToken);

        if (!check.Ok)
        {
            throw new BranchMismatchException(check);
        }
    }

    /// <summary>
    /// 前置任务都做完了吗？没做完就拒。
    ///
    /// 依赖只在两处强制：领取任务、推进到 InDev。别的状态随便走 ——
    /// 文档还没准备好就卡住人没意义，卡的是「要动手写代码」那一刻。
    /// </summary>
    private async Task EnsureDependenciesSatisfiedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var dependencies = await db.TaskDependencies
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .Select(d => new { d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        if (dependencies.Count == 0)
        {
            return;
        }

        var prerequisiteIds = dependencies.Select(d => d.DependsOnTaskId).Distinct().ToList();

        var prerequisites = await db.Tasks
            .AsNoTracking()
            .Where(t => prerequisiteIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key, t.Status })
            .ToListAsync(cancellationToken);

        // 前置被删了不算还欠着 —— 查不到的前置压根不会出现在这个列表里，
        // 于是自然被判成「已满足」。依赖指向空气时不该永远挡着。
        var pending = prerequisites.Where(p => p.Status != TaskStatus.Done).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var names = pending
            .Select(p => p.Key is null ? p.Id.ToString() : p.Key)
            .ToList();

        throw new WorkbenchException(
            $"前置任务还没完成，不能开工: {string.Join("、", names)}。" +
            "先完成前置任务，或用 workbench_remove_task_dependency 移除依赖。");
    }

    /// <summary>
    /// 决定任务的编号。没给就按项目内序号自动分配（REQ-001、REQ-002…）；
    /// 给了就用给的，但项目内不能重号。
    /// </summary>
    private async Task<string> ResolveKeyAsync(
        Guid projectId,
        TaskType type,
        string? requested,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var key = requested.Trim();

            // 先用 SQL 粗筛一把，再在内存里归一化比较。
            // 归一化（只留字母数字、转小写）本身没法翻译成 SQL，只能在内存做，
            // 但完全不筛就等于把项目里所有编号都拉回内存 —— 粗筛之后只剩同前缀的那批。
            var candidates = await db.Tasks
                .Where(t => t.ProjectId == projectId && t.Key != null)
                .Where(KeyStartsWith(AlphaPrefixOf(key)))
                .Select(t => t.Key)
                .ToListAsync(cancellationToken);

            if (candidates.Any(k => k is not null && NormalizeKey(k) == NormalizeKey(key)))
            {
                throw new WorkbenchException($"编号 {key} 在该项目里已经有了。");
            }

            return key;
        }

        // 各类型独立计数：需求 REQ-001、缺陷 BUG-001、优化 OPT-001
        var prefix = TaskTypes.Prefix(type);

        // 同前缀的编号才影响本类型的序号 —— 只取这些，别把 BUG/OPT 的也拖回来。
        var sameProjectKeys = await db.Tasks
            .Where(t => t.ProjectId == projectId && t.Key != null)
            .Where(KeyStartsWith(prefix))
            .Select(t => t.Key)
            .ToListAsync(cancellationToken);

        var next = 1;
        foreach (var key in sameProjectKeys)
        {
            if (key is null)
            {
                continue;
            }

            // 只认「同前缀 + 能解析出数字」的，避免手填的编号把自动序号算歪
            var dash = key.LastIndexOf('-');
            if (dash <= 0)
            {
                continue;
            }

            if (!key[..dash].Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(key[(dash + 1)..], out var number))
            {
                continue;
            }

            next = Math.Max(next, number + 1);
        }

        return $"{prefix}-{next:000}";
    }

    /// <summary>
    /// SQL 侧的「编号以此开头」粗筛，**不区分大小写**。
    ///
    /// 以前写成 prefix/lower/upper 三种写法各比一次，混合大小写（"rEq-001"）
    /// 三种都不命中，粗筛直接把它漏掉 —— 后面的归一化比较再正确也没用，
    /// 于是重号就这么进了库。
    ///
    /// 现在两边都 lower 再比：SQLite 的 lower() 只管 ASCII，但编号里的字母段
    /// 本来就是 ASCII（REQ/BUG/OPT），中文编号没有大小写问题。
    /// 不用 LIKE：LIKE 的 % / _ 是通配符，而 SQLite 默认没有转义符，
    /// 也不受 case_sensitive_like 这个 pragma 的影响。
    ///
    /// 这只是**粗筛**（规矩 19）：精确的归一化比较仍然在内存里做。
    /// </summary>
    private static Expression<Func<TaskItem, bool>> KeyStartsWith(string prefix)
    {
        var lower = prefix.ToLowerInvariant();

        return t => t.Key != null && t.Key.ToLower().StartsWith(lower);
    }

    /// <summary>
    /// 取编号开头的字母段（"REQ-001" → "REQ"，"req001" → "req"）。
    ///
    /// 用来在 SQL 里粗筛候选编号：SQLite 的 LIKE 对 ASCII 大小写不敏感，
    /// 所以 "req" 和 "REQ" 筛出来的是同一批，剩下的归一化比较交给内存。
    /// 没有字母段（纯数字编号）就返回空串 —— 不筛，结果仍然正确。
    /// </summary>
    internal static string AlphaPrefixOf(string key)
    {
        var end = 0;
        while (end < key.Length && char.IsLetter(key[end]))
        {
            end++;
        }

        return key[..end];
    }

    /// <summary>
    /// 编号归一化：只留字母数字并转小写。
    /// 人嘴里的「req001」和库里的「REQ-001」得认成同一个编号，
    /// 少个连字符就不该让人再去猜写法。
    /// </summary>
    internal static string NormalizeKey(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var len = 0;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[len++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..len]);
    }

    internal TaskSummaryDto ToSummary(TaskItem task, Project? project, int pendingDependencies = 0) =>
        ToSummary(task, project, clock.UtcNow, pendingDependencies);

    /// <summary>
    /// 任务 → DTO 的**唯一映射**。评审包（ReviewService）也复用它 ——
    /// 抄第二份就会漏字段：IsArchived / Assignee / Priority / DueAt / Weight /
    /// PendingDependencies / Stale 全都漏过一次，agent 拿到的评审包比列表还少信息。
    /// </summary>
    internal static TaskSummaryDto ToSummary(
        TaskItem task,
        Project? project,
        DateTimeOffset now,
        int pendingDependencies = 0)
    {
        var lastActivity = task.LastActivityAt ?? task.UpdatedAt;
        return new TaskSummaryDto(
            task.Id,
            task.ProjectId,
            project?.Name ?? string.Empty,
            task.Title,
            task.Status,
            task.ClaimedByAgentId,
            task.LeaseExpiresAt,
            task.UpdatedAt,
            task.LastActivityAt,
            (now - lastActivity).TotalHours > StaleAfterHours,
            task.MilestoneId,
            task.Key,
            task.Type,
            task.Branch,
            task.IsArchived,
            task.Assignee,
            task.Priority,
            task.DueAt,
            task.Weight,
            pendingDependencies);
    }

    internal static StatusEventDto ToEventDto(StatusEvent e) => new(
        e.Id,
        e.FromStatus,
        e.ToStatus,
        e.Source.ToString(),
        e.Actor,
        e.Note,
        e.CreatedAt,
        e.EventType,
        e.Payload);

    internal static DocumentDto ToDocumentDto(Document d) => new(
        d.Id,
        d.FileName,
        d.SizeBytes,
        d.Format.ToString(),
        d.Kind,
        d.Parsed,
        d.UploadedAt,
        d.ParseError,
        d.StoredPath);

    internal static ArtifactDto ToArtifactDto(Artifact a) => new(
        a.Id,
        a.Type.ToString(),
        a.Title,
        a.Content,
        a.ExternalPath,
        a.CommitSha,
        a.Branch,
        a.Author,
        a.CreatedAt);

    internal static PlanDto ToPlanDto(Plan p) => new(
        p.Id,
        p.Version,
        p.Background,
        p.Understanding,
        p.Breakdown,
        p.AcceptanceCriteria,
        p.Risks,
        p.Author,
        p.CreatedAt);
}
