using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

public interface IProjectService
{
    Task<ProjectSummaryDto> CreateAsync(
        string name,
        string? description = null,
        CancellationToken cancellationToken = default,
        /// <summary>
        /// 项目绑定的本地仓库路径。
        ///
        /// 参数放在 CancellationToken 之后：插在中间会破坏既有的位置参数调用
        /// （老代码第 3 个位置传的是 ct，直接编译不过）。
        /// </summary>
        string? repoPath = null);
    Task<IReadOnlyList<ProjectSummaryDto>> ListAsync(
        CancellationToken cancellationToken = default, IReadOnlyList<Guid>? projectIds = null);
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
        CancellationToken cancellationToken = default,
        /// <summary>
        /// 同时最多几个 agent 能领活（0 = 不限）。
        ///
        /// 参数放在 CancellationToken 之后，不破坏既有位置参数调用。
        /// </summary>
        int? maxConcurrentAgents = null,
        /// <summary>
        /// 每个任务最多能调多少次工具（0 = 不限）。
        /// </summary>
        int? maxToolCallsPerTask = null);
}

public sealed class ProjectService(
    IAppDbContext db,
    IClock clock,
    ICurrentMemberScope? currentMember = null) : IProjectService
{
    /// <summary>当前身份（没人登录 = 单人本机模式 = 不过滤）。</summary>
    private MemberScope? Scope => currentMember?.Current;
    /// <summary>超过这个时长没有动静就算停滞。</summary>
    public const int StaleAfterHours = 72;

    public async Task<ProjectSummaryDto> CreateAsync(
        string name,
        string? description = null,
        CancellationToken cancellationToken = default,
        string? repoPath = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new GuanfuException("项目名称不能为空。");
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

        // 仓库路径在这里就落定：项目跟本地仓库的绑定是**创建时**建立的，
        // 不是「以后有空再补」。绑了之后 agent 在这个仓库里干活才能落到本项目上。
        ApplyRepoPathAsync(project, repoPath);

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        return BuildSummary(project, []);
    }

    /// <summary>
    /// 设置仓库路径并算出绑定键 <see cref="Project.RepoRoot"/>。
    ///
    /// 绑定键是**规范化后的绝对路径**（解过符号链接）——
    /// macOS 上 /tmp 其实是 /private/tmp，不规范化就会同一个仓库出现两个键。
    ///
    /// 同一个仓库不允许绑两个项目：反查仓库→项目时才会唯一，
    /// 不唯一的话审计会把干活记到随机的那个项目上。
    /// </summary>
    private void ApplyRepoPathAsync(
        Project project,
        string? repoPath)
    {
        if (repoPath is null)
        {
            return;
        }

        var trimmed = repoPath.Trim();
        if (trimmed.Length == 0)
        {
            project.RepoPath = null;
            project.RepoRoot = null;
            return;
        }

        // 相对路径直接拒：人填「myrepo」时心里想的是自己 shell 里的那个目录，
        // 而 Path.GetFullPath 会拿**服务端的当前目录**去补全 ——
        // 结果绑到一个八竿子打不着的路径上，界面还标着「已绑仓库」。
        // 这种「看起来成功、其实绑错」比报错难查得多。
        if (!Path.IsPathRooted(trimmed))
        {
            throw new GuanfuException(
                $"仓库路径要填绝对路径（例如 /Users/you/code/your-repo）。" +
                $"现在填的「{trimmed}」是相对路径 —— 相对哪个目录说不清，" +
                "系统只能拿服务端的工作目录去补，绑出来的多半不是你想要的。");
        }

        // 两套路径，分工明确：
        //   RepoPath —— 人填的（绝对化，但不解软链），界面上显示的就是它；
        //   RepoRoot —— 规范化后的绑定键（解软链），反查仓库→项目只用它。
        // 分开存是因为 macOS 上 /tmp 其实是 /private/tmp：绑定键必须规范化，
        // 但把用户填的路径改写成 /private/tmp/... 会让人认不出自己填了什么。
        var absolute = Path.GetFullPath(trimmed);
        var canonical = PathBoundary.ResolveRealPath(trimmed);

        project.RepoPath = absolute;
        project.RepoRoot = canonical;
    }

    /// <summary>
    /// 找出也绑了同一个仓库的其它项目。
    ///
    /// 同一个仓库绑两个项目是**允许**的（前后端同仓就绑各的子目录，
    /// 反查时按「取最具体的那个」能分开）。但**完全相同**的目录会让
    /// 「agent 按目录认项目」分不出来 —— 那时必须显式指定 projectId。
    ///
    /// 所以这里不拦，只把这件事在绑定的那一刻讲清楚，
    /// 免得等到 agent 绑会话失败才发现。
    /// </summary>
    private async Task<string?> BuildRepoWarningAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        // 目录还不存在 —— 不拦（可能先建项目再 clone），但要说一声。
        if (!string.IsNullOrEmpty(project.RepoPath) && !Directory.Exists(project.RepoPath))
        {
            notes.Add($"这个目录现在还不存在（{project.RepoPath}）—— " +
                "agent 在这里干活前，要先把它建出来或把仓库 clone 进来。");
        }

        if (string.IsNullOrEmpty(project.RepoRoot))
        {
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        // 精确比较放内存里：路径大小写的口径跟 PathBoundary 一致（Windows 不敏感）。
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var others = await db.Projects.AsNoTracking()
            .Where(p => p.Id != project.Id && p.RepoRoot != null)
            .Select(p => new { p.Id, p.Name, p.RepoRoot })
            .ToListAsync(cancellationToken);

        var owners = others
            .Where(p => string.Equals(p.RepoRoot, project.RepoRoot, comparison))
            .Select(p => p.Name)
            .Distinct()
            .ToList();

        if (owners.Count > 0)
        {
            notes.Add($"这个仓库也绑在项目「{string.Join("、", owners)}」上。" +
                "agent 按工作目录自动认项目时会分不出来（得显式指定项目），" +
                "前后端同仓的话，各绑各的子目录就行。");
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private async Task<Workspace> CreateDefaultWorkspaceAsync(CancellationToken cancellationToken)
    {
        var workspace = new Workspace { Name = "默认工作区", CreatedAt = clock.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(cancellationToken);
        return workspace;
    }

    public async Task<IReadOnlyList<ProjectSummaryDto>> ListAsync(
        CancellationToken cancellationToken = default, IReadOnlyList<Guid>? projectIds = null)
    {
        var query = db.Projects.AsQueryable();
        if (projectIds is not null)
        {
            query = query.Where(p => projectIds.Contains(p.Id));
        }

        var projects = await query
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var result = new List<ProjectSummaryDto>(projects.Count);
        foreach (var project in projects)
        {
            // 可见性：项目卡片上的计数也按身份收 ——
            // 否则「这个项目有 40 个活」本身就是一种泄露。
            var tasks = await db.Tasks
                .VisibleTo(Scope)
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
            .VisibleTo(Scope)
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
            ApplyRepoPathAsync(project, repoPath);
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

        return BuildConfig(project, await BuildRepoWarningAsync(project, cancellationToken));
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
        CancellationToken cancellationToken = default,
        int? maxConcurrentAgents = null,
        int? maxToolCallsPerTask = null)
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
            ApplyRepoPathAsync(project, repoPath);
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
                throw new GuanfuException($"未知通知格式: {notificationFormat}。合法值: generic, dingtalk, feishu");
            }

            project.NotificationFormat = format.Length == 0 ? "generic" : format;
        }

        if (maxConcurrentAgents is not null)
        {
            // 0 = 不限；负数没有意义，直接归零
            project.MaxConcurrentAgents = Math.Max(0, maxConcurrentAgents.Value);
        }

        if (maxToolCallsPerTask is not null)
        {
            project.MaxToolCallsPerTask = Math.Max(0, maxToolCallsPerTask.Value);
        }

        project.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return BuildConfig(project, await BuildRepoWarningAsync(project, cancellationToken));
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

    private static ProjectConfigDto BuildConfig(Project project, string? repoWarning = null) => new(
        project.Id,
        project.Name,
        project.Description,
        project.RepoPath,
        project.RepoRemote,
        project.DefaultBranch,
        ShellRunner.ParseAllowed(project.AllowedCommandsJson),
        project.AgentCommand,
        project.NotificationWebhookUrl,
        project.NotificationFormat,
        project.RepoRoot,
        repoWarning,
        project.MaxConcurrentAgents,
        project.MaxToolCallsPerTask);

    private ProjectSummaryDto BuildSummary(
        Project project,
        IReadOnlyList<TaskItem> tasks)
    {
        // 统计口径统一走 TaskProgress：排除归档与取消，子任务归并到父任务。
        // 归档的任务以前只在列表里被排除，统计里照样算进分母 ——
        // 于是「清理掉误建的任务」之后项目进度永远到不了 100%。
        var counted = TaskProgress.Countable(tasks);

        // 没有任务时 totalWeight 为 0，直接相除会得到 NaN，
        // 而 NaN 无法序列化成合法 JSON，会让整个列表接口 500。
        var progress = TaskProgress.Weighted(counted);

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
            project.RepoRemote,
            project.WorkspaceId,
            project.RepoRoot);
    }
}
