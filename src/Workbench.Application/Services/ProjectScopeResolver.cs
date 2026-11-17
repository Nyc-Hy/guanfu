using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 一次工具调用落在哪个项目 / 哪个任务 / 哪个 agent 上。
/// </summary>
public sealed record ProjectScope(
    Guid ProjectId,
    string ProjectName,
    Guid? SessionId,
    Guid? TaskId,
    string? AgentId,
    string? RepoPath = null);

/// <summary>
/// 当前请求的会话。由宿主提供 —— 应用层不认 HTTP，
/// 免得业务用例被绑死在某一种传输上。
/// </summary>
public interface IAgentSessionAccessor
{
    Task<AgentSessionDto?> CurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前调用方的标识：请求头里的 <c>X-Workbench-Agent</c>。
    ///
    /// 客户端不一定会给工具显式传 callerId（Claude Code 就不会），
    /// 但审计要能说出「是谁调的」—— 这个头就是那条线索。
    /// </summary>
    string? CurrentAgentId { get; }

    /// <summary>
    /// 当前请求的 MCP 协议会话 id（<c>Mcp-Session-Id</c> 头）。
    ///
    /// 绑会话时要把它一起记上 —— 客户端（Claude Code 等）只会在后续请求里
    /// 带这个协议会话 id，不带工作台自己的头。记上了，下次调用才认得出是同一条会话。
    /// 无状态模式下没有这个 id，返回 null。
    /// </summary>
    string? CurrentMcpSessionId { get; }
}

/// <summary>
/// 项目作用域解析。回答一个问题：这次工具调用算在谁头上？
///
/// 解析顺序：
///   1. 调用方显式传了 projectId → 用它，但要跟会话对账；
///   2. 没传但有会话 → 用会话绑定的项目；
///   3. 都没有 → 报错，并告诉调用方怎么绑。
///
/// 第 1 条里的「对账」是这套设计的核心：agent 明明绑在 A 项目，
/// 却调用了 B 项目的 id —— 这在以前是允许的（谁传什么算什么），
/// 现在直接拒。否则审计里的归属就是 agent 自己说了算。
/// </summary>
public interface IProjectScopeResolver
{
    Task<ProjectScope> ResolveAsync(
        Guid? explicitProjectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析不出就返回 null，不报错。
    /// 给「本来就可以不指定项目」的工具用（比如列任务：不绑会话时可以列全部）。
    /// </summary>
    Task<ProjectScope?> ResolveOrNullAsync(
        Guid? explicitProjectId,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectScopeResolver(
    IAppDbContext db,
    IAgentSessionAccessor? sessions = null) : IProjectScopeResolver
{
    public async Task<ProjectScope> ResolveAsync(
        Guid? explicitProjectId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveOrNullAsync(explicitProjectId, cancellationToken);

        if (scope is null)
        {
            throw new WorkbenchException(
                "当前调用没有绑定项目。请先调用 workbench_bind_session 绑定你所在的仓库，" +
                "或者显式传 projectId。");
        }

        return scope;
    }

    public async Task<ProjectScope?> ResolveOrNullAsync(
        Guid? explicitProjectId,
        CancellationToken cancellationToken = default)
    {
        var session = sessions is null
            ? null
            : await sessions.CurrentAsync(cancellationToken);

        if (explicitProjectId is Guid projectId)
        {
            if (session is not null && session.ProjectId != projectId)
            {
                throw new WorkbenchException(
                    $"本次调用指定的项目与会话绑定的项目不一致" +
                    $"（会话绑定的是「{session.ProjectName}」，本次指定的是 {projectId}）。" +
                    "一个会话只能操作它绑定的那个项目。");
            }

            return await BuildAsync(projectId, session, cancellationToken);
        }

        if (session is not null)
        {
            return await BuildAsync(session.ProjectId, session, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// 这个目录算不算在项目的仓库范围内。
    ///
    /// 两种算：在仓库根里面（子目录），或者是**这个仓库的 worktree**
    /// （兄弟目录，但属于同一个仓库 —— agent 并行干活时都待在那儿）。
    /// </summary>
    private static bool IsInsideProjectRepo(string projectRepoRoot, string sessionPath)
    {
        if (PathBoundary.IsSameOrInside(projectRepoRoot, sessionPath))
        {
            return true;
        }

        var owner = WorkPathPolicy.TryGetOwningRepoRoot(sessionPath);
        if (owner is null)
        {
            return false;
        }

        // 两边都解软链再比（macOS 上 /tmp 其实是 /private/tmp）
        return string.Equals(
            PathBoundary.ResolveRealPath(projectRepoRoot),
            PathBoundary.ResolveRealPath(owner),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProjectScope> BuildAsync(
        Guid projectId,
        AgentSessionDto? session,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        if (project is null)
        {
            throw new NotFoundException("项目", projectId);
        }

        // 会话的工作目录必须落在项目绑定的仓库范围内。
        // 绑会话时已经按这个规则挑过项目，这里再查一遍是防「配置改了」：
        // agent 绑完之后项目换了仓库，它还拿着旧会话往下写。
        //
        // **worktree 也算数**：它在仓库根外面（是兄弟目录），但属于同一个仓库 ——
        // agent 在自己的 worktree 里干活是正常用法，不该被当成越界。
        if (session is not null &&
            !string.IsNullOrWhiteSpace(session.RepoPath) &&
            !string.IsNullOrWhiteSpace(project.RepoRoot) &&
            !IsInsideProjectRepo(project.RepoRoot, session.RepoPath))
        {
            throw new WorkbenchException(
                $"会话的工作目录不在项目「{project.Name}」绑定的仓库范围内" +
                $"（会话目录：{session.RepoPath}，项目仓库：{project.RepoRoot}）。");
        }

        return new ProjectScope(
            project.Id,
            project.Name,
            session?.Id,
            session?.TaskId,
            // 没有会话时也带上 agent 身份（请求头里的）——
            // 工位是「这个 agent 手上那个活」的属性，
            // 身份丢了就找不到它在哪个目录干活。
            session?.AgentId ?? sessions?.CurrentAgentId,
            session?.RepoPath);
    }
}
