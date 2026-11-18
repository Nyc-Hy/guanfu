using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>agent 会话。对外给 agent 看的那一份。</summary>
public sealed record AgentSessionDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    Guid? TaskId,
    string? TaskKey,
    string AgentId,
    string RepoPath,
    string? Branch,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt);

/// <summary>会话绑定结果。绑不上时把候选项目带回去，agent 能自己改对。</summary>
public sealed record SessionBindResult(
    bool Ok,
    AgentSessionDto? Session,
    string? Error = null,
    IReadOnlyList<string>? CandidateProjects = null)
{
    public static SessionBindResult Succeeded(AgentSessionDto session) => new(true, session);

    public static SessionBindResult Failed(string error, IReadOnlyList<string>? candidates = null) =>
        new(false, null, error, candidates);
}

/// <summary>
/// 会话服务。把「agent 这次干活」跟「观复里的项目 / 任务」接上。
///
/// 绑定键是**仓库目录**，不是项目 GUID：
/// agent 的工作目录落在哪个项目绑定的仓库范围内，就绑到那个项目。
/// 用目录而不是 GUID，是因为 GUID 得靠 agent 自己记着传，
/// 而「我在哪个目录干活」这件事 agent 天然知道、也不会传错。
/// </summary>
public interface IAgentSessionService
{
    /// <summary>
    /// 按仓库路径绑定会话。仓库落在某个项目绑定的目录内 → 绑定到该项目。
    /// 同一个 <paramref name="mcpSessionId"/> 复用同一条会话。
    /// </summary>
    Task<SessionBindResult> BindRepoAsync(
        string repoPath,
        string agentId,
        string? mcpSessionId = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>按仓库路径找项目。不建会话 —— 只想知道这个目录归谁。</summary>
    Task<ProjectSummaryDto?> FindProjectByRepoAsync(
        string repoPath,
        CancellationToken cancellationToken = default);

    Task<AgentSessionDto?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>按 MCP 协议会话 id 找会话。同一个协议会话不该碎成一堆会话。</summary>
    Task<AgentSessionDto?> FindByMcpSessionIdAsync(
        string mcpSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>认领任务后把任务挂到会话上。挂上之后工具调用自动归属到这个任务。</summary>
    Task<AgentSessionDto?> AttachTaskAsync(
        Guid sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>更新会话的「最后一次有动静」时间。</summary>
    Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>列出干过这个任务的会话（谁在什么时候接的活）。</summary>
    /// <summary>
    /// 最近还活着的 agent 会话（按最后有动静的时间倒序）。
    ///
    /// 「接入 agent」页拿它当心跳：人复制完配置，得能一眼看出
    /// 客户端到底连上没有、连上之后还在不在调工具。
    /// </summary>
    Task<IReadOnlyList<AgentSessionDto>> ListRecentAsync(
        int limit = 5,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSessionDto>> ListByTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}

public sealed class AgentSessionService(IAppDbContext db, IClock clock) : IAgentSessionService
{
    public async Task<SessionBindResult> BindRepoAsync(
        string repoPath,
        string agentId,
        string? mcpSessionId = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return SessionBindResult.Failed("仓库路径不能为空。");
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return SessionBindResult.Failed("agentId 不能为空。");
        }

        var canonical = Canonicalize(repoPath);
        if (!Directory.Exists(canonical))
        {
            return SessionBindResult.Failed($"目录不存在: {canonical}");
        }

        Project? project;

        if (projectId is Guid explicitId)
        {
            project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == explicitId, cancellationToken);

            if (project is null)
            {
                return SessionBindResult.Failed($"项目 {explicitId} 不存在。");
            }

            // 显式指定了项目，还得确认这个目录确实在它的仓库范围内 ——
            // 否则「在 A 仓库干活、却绑到 B 项目」就又回来了。
            if (string.IsNullOrWhiteSpace(project.RepoRoot) ||
                !PathBoundary.IsSameOrInside(project.RepoRoot, canonical))
            {
                return SessionBindResult.Failed(
                    $"目录 {canonical} 不在项目「{project.Name}」绑定的仓库范围内" +
                    $"（{project.RepoRoot ?? "未绑仓库"}）。");
            }
        }
        else
        {
            var matches = await FindProjectMatchesAsync(canonical, cancellationToken);

            if (matches.Count == 0)
            {
                return SessionBindResult.Failed(
                    $"目录 {canonical} 不在任何项目绑定的仓库范围内。" +
                    "请先在观复里创建项目并绑定这个仓库，再让 agent 干活。",
                    await ListBoundProjectNamesAsync(cancellationToken));
            }

            // 同样具体的两个项目 —— 分不出来就不绑。
            // 猜一个会让审计把干活记到随机的那个项目上。
            if (matches.Count > 1 &&
                matches[0].RepoRoot!.Length == matches[1].RepoRoot!.Length)
            {
                return SessionBindResult.Failed(
                    $"目录 {canonical} 同时落在多个项目绑定的仓库范围内" +
                    $"（{string.Join("、", matches.Select(p => p.Name))}）。" +
                    "请显式指定 projectId 绑定到其中一个。",
                    matches.Select(p => p.Name).ToList());
            }

            project = matches[0];
        }

        AgentSession? session = null;

        if (!string.IsNullOrWhiteSpace(mcpSessionId))
        {
            session = await db.AgentSessions
                .FirstOrDefaultAsync(s => s.McpSessionId == mcpSessionId, cancellationToken);
        }

        if (session is null)
        {
            // 没有协议会话 id 时（直接走 HTTP 的 agent、或者桥接还没拿到 id），
            // 复用「同一个 agent 在同一个仓库里刚开过的那条会话」——
            // 不然每次工具调用都新开一条，审计里会碎成一堆。
            var since = clock.UtcNow - RecentSessionWindow;

            session = await db.AgentSessions
                .Where(s =>
                    s.ProjectId == project.Id &&
                    s.RepoPath == canonical &&
                    s.AgentId == agentId.Trim() &&
                    s.EndedAt == null &&
                    s.LastSeenAt >= since)
                .OrderByDescending(s => s.LastSeenAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (session is null)
        {
            session = new AgentSession
            {
                ProjectId = project.Id,
                AgentId = agentId.Trim(),
                RepoPath = canonical,
                McpSessionId = string.IsNullOrWhiteSpace(mcpSessionId) ? null : mcpSessionId,
                StartedAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow
            };

            db.AgentSessions.Add(session);
        }
        else
        {
            session.ProjectId = project.Id;
            session.RepoPath = canonical;
            session.AgentId = agentId.Trim();
            session.LastSeenAt = clock.UtcNow;
            session.EndedAt = null;

            if (!string.IsNullOrWhiteSpace(mcpSessionId))
            {
                session.McpSessionId = mcpSessionId;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return SessionBindResult.Succeeded(await BuildDtoAsync(session, cancellationToken));
    }

    public async Task<ProjectSummaryDto?> FindProjectByRepoAsync(
        string repoPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }

        var matches = await FindProjectMatchesAsync(Canonicalize(repoPath), cancellationToken);
        var project = matches.Count > 0 ? matches[0] : null;

        if (project is null)
        {
            return null;
        }

        var tasks = await db.Tasks
            .Where(t => t.ProjectId == project.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new ProjectSummaryDto(
            project.Id,
            project.Name,
            project.Description,
            0,
            tasks.Count,
            0,
            0,
            0,
            null,
            false,
            project.RepoPath,
            project.DefaultBranch,
            project.RepoRemote,
            project.WorkspaceId);
    }

    public async Task<AgentSessionDto?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.AgentSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        return session is null ? null : await BuildDtoAsync(session, cancellationToken);
    }

    public async Task<AgentSessionDto?> FindByMcpSessionIdAsync(
        string mcpSessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mcpSessionId))
        {
            return null;
        }

        var session = await db.AgentSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.McpSessionId == mcpSessionId, cancellationToken);

        return session is null ? null : await BuildDtoAsync(session, cancellationToken);
    }

    public async Task<AgentSessionDto?> AttachTaskAsync(
        Guid sessionId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.AgentSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        session.TaskId = taskId;
        session.LastSeenAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(session, cancellationToken);
    }

    public async Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.AgentSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return;
        }

        // 心跳别每次调用都写库 —— agent 高频调用时会把这张表写爆。
        // 一分钟内的重复调用直接跳过。
        if (clock.UtcNow - session.LastSeenAt < HeartbeatInterval)
        {
            return;
        }

        session.LastSeenAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>同一 agent、同一仓库的会话在这个时间窗内可以复用。</summary>
    private static readonly TimeSpan RecentSessionWindow = TimeSpan.FromMinutes(30);

    /// <summary>会话心跳的最小间隔。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 找「这个目录落在哪些项目绑定的仓库内」。用包含关系而不是字符串相等 ——
    /// agent 在仓库的子目录里干活也算在同一个项目下。
    ///
    /// 多个项目都能匹配时取**最具体**的那个（路径最长）：
    /// 一个大仓库里再套一个小仓库时，agent 在小仓库里干活应该归小仓库。
    /// </summary>
    private async Task<List<Project>> FindProjectMatchesAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        var bound = await db.Projects
            .AsNoTracking()
            .Where(p => p.RepoRoot != null)
            .ToListAsync(cancellationToken);

        // worktree 也是「这个仓库的目录」—— 它在仓库根外面（是兄弟目录），
        // 但属于同一个仓库。agent 在 worktree 里干活时照样要能绑上会话，
        // 不然它连「自己在哪个项目」都认不出来。
        // 两边都解软链再比：macOS 上 /tmp 其实是 /private/tmp，
        // 不解就比不上，worktree 照样绑不上会话。
        var owningRepoRoot = WorkPathPolicy.TryGetOwningRepoRoot(canonicalPath) is { } owner
            ? PathBoundary.ResolveRealPath(owner)
            : null;

        return bound
            .Where(p => PathBoundary.IsSameOrInside(p.RepoRoot!, canonicalPath) ||
                        (owningRepoRoot is not null &&
                         string.Equals(PathBoundary.ResolveRealPath(p.RepoRoot!), owningRepoRoot,
                             StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.RepoRoot!.Length)
            .ToList();
    }

    /// <summary>已绑仓库的项目名。绑不上时告诉 agent「有哪些项目可以选」。</summary>
    private async Task<IReadOnlyList<string>> ListBoundProjectNamesAsync(
        CancellationToken cancellationToken)
        => await db.Projects
            .AsNoTracking()
            .Where(p => p.RepoRoot != null)
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync(cancellationToken);

    private async Task<AgentSessionDto> BuildDtoAsync(
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == session.ProjectId, cancellationToken);

        string? taskKey = null;
        if (session.TaskId is not null)
        {
            taskKey = await db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == session.TaskId)
                .Select(t => t.Key)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new AgentSessionDto(
            session.Id,
            session.ProjectId,
            project?.Name ?? string.Empty,
            session.TaskId,
            taskKey,
            session.AgentId,
            session.RepoPath,
            project?.DefaultBranch,
            session.StartedAt,
            session.LastSeenAt);
    }

    /// <summary>列出干过这个任务的会话（谁在什么时候接的活）。</summary>
    public async Task<IReadOnlyList<AgentSessionDto>> ListRecentAsync(
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var sessions = await db.AgentSessions
            .AsNoTracking()
            .OrderByDescending(s => s.LastSeenAt)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);

        var result = new List<AgentSessionDto>(sessions.Count);
        foreach (var session in sessions)
        {
            result.Add(await BuildDtoAsync(session, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<AgentSessionDto>> ListByTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var sessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken);

        var result = new List<AgentSessionDto>(sessions.Count);
        foreach (var session in sessions)
        {
            result.Add(await BuildDtoAsync(session, cancellationToken));
        }

        return result;
    }

    /// <summary>绝对路径 + 解符号链接。macOS 上 /tmp 其实是 /private/tmp。</summary>
    public static string Canonicalize(string path) =>
        PathBoundary.ResolveRealPath(path.Trim());
}
