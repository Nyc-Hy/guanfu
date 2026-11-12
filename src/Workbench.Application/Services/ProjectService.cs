using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

public interface IProjectService
{
    Task<ProjectSummaryDto> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectSummaryDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProjectSummaryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProjectSummaryDto> UpdateAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? repoPath = null,
        string? repoRemote = null,
        string? defaultBranch = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取项目配置（仓库路径、agent 启动命令、命令白名单、通知地址等）。</summary>
    Task<ProjectConfigDto> GetConfigAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新项目配置。空字符串表示「清空该字段」，
    /// 这样命令行/API 不需要额外的 null 语义来表达「改成没有」。
    /// </summary>
    Task<ProjectConfigDto> UpdateConfigAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? repoPath = null,
        string? repoRemote = null,
        string? defaultBranch = null,
        IReadOnlyList<string>? allowedCommands = null,
        string? agentCommand = null,
        string? notificationWebhookUrl = null,
        string? notificationFormat = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectService(IAppDbContext db, IClock clock) : IProjectService
{
    /// <summary>超过这个时长没有动静就算停滞。</summary>
    public const int StaleAfterHours = 72;

    public async Task<ProjectSummaryDto> CreateAsync(
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WorkbenchException("项目名称不能为空。");
        }

        var workspace = await db.Workspaces
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? await CreateDefaultWorkspaceAsync(cancellationToken);

        var project = new Project
        {
            WorkspaceId = workspace.Id,
            Name = name.Trim(),
            Description = description,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        return BuildSummary(project, []);
    }

    private async Task<Workspace> CreateDefaultWorkspaceAsync(CancellationToken cancellationToken)
    {
        var workspace = new Workspace { Name = "默认工作区", CreatedAt = clock.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(cancellationToken);
        return workspace;
    }

    public async Task<IReadOnlyList<ProjectSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var result = new List<ProjectSummaryDto>(projects.Count);
        foreach (var project in projects)
        {
            var tasks = await db.Tasks
                .Where(t => t.ProjectId == project.Id)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            result.Add(BuildSummary(project, tasks));
        }

        return result;
    }

    public async Task<ProjectSummaryDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (project is null)
        {
            return null;
        }

        var tasks = await db.Tasks
            .Where(t => t.ProjectId == id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return BuildSummary(project, tasks);
    }

    public async Task<ProjectSummaryDto> UpdateAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? repoPath = null,
        string? repoRemote = null,
        string? defaultBranch = null,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                      ?? throw new NotFoundException("项目", id);

        if (name is not null)
        {
            project.Name = name.Trim();
        }

        if (description is not null)
        {
            project.Description = description;
        }

        if (repoPath is not null)
        {
            project.RepoPath = repoPath;
        }

        if (repoRemote is not null)
        {
            project.RepoRemote = repoRemote;
        }

        if (defaultBranch is not null)
        {
            project.DefaultBranch = defaultBranch;
        }

        project.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var tasks = await db.Tasks
            .Where(t => t.ProjectId == id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return BuildSummary(project, tasks);
    }

    public async Task<ProjectConfigDto> GetConfigAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                      ?? throw new NotFoundException("项目", id);

        return BuildConfig(project);
    }

    public async Task<ProjectConfigDto> UpdateConfigAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? repoPath = null,
        string? repoRemote = null,
        string? defaultBranch = null,
        IReadOnlyList<string>? allowedCommands = null,
        string? agentCommand = null,
        string? notificationWebhookUrl = null,
        string? notificationFormat = null,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
                      ?? throw new NotFoundException("项目", id);

        if (name is not null)
        {
            project.Name = name.Trim();
        }

        if (description is not null)
        {
            project.Description = description;
        }

        if (repoPath is not null)
        {
            project.RepoPath = Normalize(repoPath);
        }

        if (repoRemote is not null)
        {
            project.RepoRemote = repoRemote;
        }

        if (defaultBranch is not null)
        {
            project.DefaultBranch = defaultBranch;
        }

        if (allowedCommands is not null)
        {
            var cleaned = allowedCommands
                .Select(c => c?.Trim() ?? string.Empty)
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 空列表 = 清空白名单
            project.AllowedCommandsJson = cleaned.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(cleaned);
        }

        if (agentCommand is not null)
        {
            project.AgentCommand = Normalize(agentCommand);
        }

        if (notificationWebhookUrl is not null)
        {
            project.NotificationWebhookUrl = Normalize(notificationWebhookUrl);
        }

        if (notificationFormat is not null)
        {
            var format = notificationFormat.Trim().ToLowerInvariant();
            if (format.Length > 0 && format is not ("generic" or "dingtalk" or "feishu"))
            {
                throw new WorkbenchException($"未知通知格式: {notificationFormat}。合法值: generic, dingtalk, feishu");
            }

            project.NotificationFormat = format.Length == 0 ? "generic" : format;
        }

        project.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return BuildConfig(project);
    }

    /// <summary>空字符串清空字段；仓库路径统一存绝对路径。</summary>
    private static string? Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed;
    }

    private static ProjectConfigDto BuildConfig(Project project) => new(
        project.Id,
        project.Name,
        project.Description,
        project.RepoPath,
        project.RepoRemote,
        project.DefaultBranch,
        ShellRunner.ParseAllowed(project.AllowedCommandsJson),
        project.AgentCommand,
        project.NotificationWebhookUrl,
        project.NotificationFormat);

    private ProjectSummaryDto BuildSummary(
        Project project,
        IReadOnlyList<TaskItem> tasks)
    {
        // 子任务归并到父任务，避免重复计数
        var topLevel = tasks.Where(t => t.ParentTaskId == null).ToList();
        var counted = topLevel.Count > 0 ? topLevel : tasks.ToList();

        var totalWeight = counted.Sum(t => Math.Max(t.Weight, 0.0001));

        // 没有任务时 totalWeight 为 0，直接相除会得到 NaN，
        // 而 NaN 无法序列化成合法 JSON，会让整个列表接口 500。
        var progress = totalWeight > 0d
            ? counted.Sum(t => TaskStatusMachine.ProgressWeight(t.Status) * Math.Max(t.Weight, 0.0001)) / totalWeight
            : 0d;

        if (!double.IsFinite(progress))
        {
            progress = 0d;
        }

        DateTimeOffset? lastActivity = tasks.Count == 0
            ? null
            : tasks.Max(t => t.LastActivityAt ?? t.UpdatedAt);

        var stale = lastActivity is not null &&
                    (clock.UtcNow - lastActivity.Value).TotalHours > StaleAfterHours;

        return new ProjectSummaryDto(
            project.Id,
            project.Name,
            project.Description,
            Math.Round(progress, 4),
            counted.Count,
            counted.Count(t => t.Status == TaskStatus.Done),
            counted.Count(t => t.Status == TaskStatus.Blocked),
            counted.Count(t => !TaskStatusMachine.IsTerminal(t.Status) && t.Status != TaskStatus.Blocked),
            lastActivity,
            stale,
            project.RepoPath,
            project.DefaultBranch,
            project.RepoRemote);
    }
}
