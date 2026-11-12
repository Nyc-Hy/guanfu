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

    Task<IReadOnlyList<TaskSummaryDto>> ListAsync(
        Guid? projectId = null,
        TaskStatus? status = null,
        string? keyword = null,
        Guid? milestoneId = null,
        string? key = null,
        TaskType? type = null,
        CancellationToken cancellationToken = default);

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

        var task = new TaskItem
        {
            ProjectId = projectId,
            ParentTaskId = parentTaskId,
            MilestoneId = milestoneId,
            Type = type,
            Branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
            Key = await ResolveKeyAsync(projectId, type, key, cancellationToken),
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
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(task, projectExists ? await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken) : null);
    }

    public async Task<IReadOnlyList<TaskSummaryDto>> ListAsync(
        Guid? projectId = null,
        TaskStatus? status = null,
        string? keyword = null,
        Guid? milestoneId = null,
        string? key = null,
        TaskType? type = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Tasks.AsNoTracking();

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

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            query = query.Where(t => t.Title.Contains(k) || (t.Description != null && t.Description.Contains(k)));
        }

        var tasks = await query.OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);

        // 编号匹配在内存里做：人嘴里说的是「req001」，库里存的是「REQ-001」，
        // 差一个连字符。归一化后（只留字母数字、转小写）两边才认得出是同一个。
        if (!string.IsNullOrWhiteSpace(key))
        {
            var k = NormalizeKey(key);
            tasks = tasks.Where(t => t.Key is not null && NormalizeKey(t.Key) == k).ToList();
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

        return tasks
            .Select(t => ToSummary(t, projects.TryGetValue(t.ProjectId, out var p) ? p : null))
            .ToList();
    }

    public async Task<TaskSummaryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);
        return ToSummary(task, project);
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
                FromStatus = task.Status,
                ToStatus = task.Status,
                Source = TaskEventSource.Human,
                Note = $"类型变更: {TaskTypes.Name(before)} → {TaskTypes.Name(type)}（编号 {task.Key} 不变）",
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);
        return ToSummary(task, project);
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
                FromStatus = task.Status,
                ToStatus = task.Status,
                Source = TaskEventSource.Human,
                Note = before is null
                    ? $"绑定分支: {value}"
                    : value is null
                        ? $"解绑分支: {before}"
                        : $"绑定分支: {before} → {value}",
                CreatedAt = clock.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);
        return ToSummary(task, project);
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

        // 结项门禁：开过评审的任务，当前轮必须全部维度通过。
        // 没开过评审的老流程不受影响（门禁只在有评审记录时生效）。
        if (from == TaskStatus.Review && to == TaskStatus.Done && reviewGate is not null)
        {
            var gate = await reviewGate.GetGateAsync(id, cancellationToken);

            if (gate.HasReview &&
                (gate.PendingDimensions.Count > 0 || gate.FailedDimensions.Count > 0))
            {
                throw new ReviewGateException(gate);
            }
        }

        task.Status = to;
        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;

        // 任务完成或取消时释放认领租约
        if (TaskStatusMachine.IsTerminal(to))
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
            CreatedAt = clock.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);
        return ToSummary(task, project);
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

        task.ClaimedByAgentId = agentId;
        task.LeaseExpiresAt = now.AddMinutes(Math.Clamp(leaseMinutes, 1, 24 * 60));
        task.UpdatedAt = now;
        task.LastActivityAt = now;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = task.Id,
            FromStatus = task.Status,
            ToStatus = task.Status,
            Source = TaskEventSource.Agent,
            Actor = agentId,
            Note = $"认领任务，租约 {task.LeaseExpiresAt:u} 到期",
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);
        return ToSummary(task, project);
    }

    public async Task ReleaseAsync(Guid id, string agentId, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                   ?? throw new NotFoundException("任务", id);

        if (!string.Equals(task.ClaimedByAgentId, agentId, StringComparison.Ordinal))
        {
            throw new WorkbenchException($"任务不由 {agentId} 持有，无法释放。");
        }

        task.ClaimedByAgentId = null;
        task.LeaseExpiresAt = null;
        task.UpdatedAt = clock.UtcNow;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = task.Id,
            FromStatus = task.Status,
            ToStatus = task.Status,
            Source = TaskEventSource.Agent,
            Actor = agentId,
            Note = "释放任务认领",
            CreatedAt = clock.UtcNow
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

        await db.SaveChangesAsync(cancellationToken);
        return ToPlanDto(plan);
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

        var failed = Math.Max(total - passed, 0);
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
            FromStatus = task.Status,
            ToStatus = task.Status,
            Source = TaskEventSource.Agent,
            Actor = author,
            Note = summary,
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
            FromStatus = task.Status,
            ToStatus = task.Status,
            Source = source,
            Actor = actor,
            Note = message,
            CreatedAt = clock.UtcNow
        };

        db.StatusEvents.Add(evt);
        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return ToEventDto(evt);
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

        return new TaskContextDto(
            ToSummary(task, project),
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

            var existing = await db.Tasks
                .Where(t => t.ProjectId == projectId && t.Key != null)
                .Select(t => t.Key)
                .ToListAsync(cancellationToken);

            if (existing.Any(k => k is not null && NormalizeKey(k) == NormalizeKey(key)))
            {
                throw new WorkbenchException($"编号 {key} 在该项目里已经有了。");
            }

            return key;
        }

        // 各类型独立计数：需求 REQ-001、缺陷 BUG-001、优化 OPT-001
        var prefix = TaskTypes.Prefix(type);

        var sameProjectKeys = await db.Tasks
            .Where(t => t.ProjectId == projectId && t.Key != null)
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

    internal TaskSummaryDto ToSummary(TaskItem task, Project? project)
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
            (clock.UtcNow - lastActivity).TotalHours > StaleAfterHours,
            task.MilestoneId,
            task.Key,
            task.Type,
            task.Branch);
    }

    internal static StatusEventDto ToEventDto(StatusEvent e) => new(
        e.Id,
        e.FromStatus,
        e.ToStatus,
        e.Source.ToString(),
        e.Actor,
        e.Note,
        e.CreatedAt);

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
