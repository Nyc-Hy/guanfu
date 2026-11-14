using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Workbench.Application;
using Workbench.Application.Services;

namespace Workbench.Mcp;

/// <summary>
/// 绑定会话的结果。绑不上时把候选项目带回去 —— agent 读到候选能自己改对，
/// 而不是卡在这一步反复重试。
/// </summary>
public sealed record BindSessionToolResult(
    bool Ok,
    AgentSessionDto? Session,
    string? Error = null,
    IReadOnlyList<string>? CandidateProjects = null);

/// <summary>
/// 从 HTTP 头里认出「当前这次调用属于哪个会话」。
///
/// 三个来源，优先级从高到低：
///   1. <c>X-Workbench-Session</c> —— 明确的会话 id（桥接绑定后回填）；
///   2. <c>Mcp-Session-Id</c> —— MCP 协议会话。同一个协议会话复用同一条会话；
///   3. <c>X-Workbench-Repo</c> —— 桥接注入的工作目录。第一次看到它就建会话，
///      之后靠第 2 条复用。
///
/// 三条都拿不到就当「没绑定」：这时工具仍然可以显式传 projectId，
/// 老调用不受影响。
/// </summary>
public sealed class AgentSessionAccessor(
    IHttpContextAccessor? http,
    IAgentSessionService? sessions) : IAgentSessionAccessor
{
    internal const string SessionHeader = "X-Workbench-Session";
    internal const string RepoHeader = "X-Workbench-Repo";
    internal const string AgentHeader = "X-Workbench-Agent";

    public async Task<AgentSessionDto?> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var context = http?.HttpContext;
        if (context is null || sessions is null)
        {
            return null;
        }

        var request = context.Request;
        var mcpSessionId = request.Headers["Mcp-Session-Id"].ToString();

        var explicitId = request.Headers[SessionHeader].ToString();
        if (Guid.TryParse(explicitId, out var sessionId))
        {
            var session = await sessions.GetAsync(sessionId, cancellationToken);
            if (session is not null)
            {
                await sessions.TouchAsync(sessionId, cancellationToken);
                return session;
            }
        }

        if (!string.IsNullOrWhiteSpace(mcpSessionId))
        {
            var session = await sessions.FindByMcpSessionIdAsync(mcpSessionId, cancellationToken);
            if (session is not null)
            {
                await sessions.TouchAsync(session.Id, cancellationToken);
                return session;
            }
        }

        var repo = request.Headers[RepoHeader].ToString();
        if (!string.IsNullOrWhiteSpace(repo))
        {
            var agentId = request.Headers[AgentHeader].ToString();
            if (string.IsNullOrWhiteSpace(agentId))
            {
                agentId = "unknown";
            }

            var bound = await sessions.BindRepoAsync(
                repo,
                agentId,
                string.IsNullOrWhiteSpace(mcpSessionId) ? null : mcpSessionId,
                projectId: null,
                cancellationToken);

            return bound.Session;
        }

        return null;
    }
}

/// <summary>
/// 会话相关的 MCP 工具。
///
/// <c>workbench_bind_session</c> 是这套绑定链路的入口：
/// agent 告诉工作台「我在这个目录干活」，工作台认出对应的项目并记住这次会话。
/// 绑完之后，工具调用不再需要 agent 自己背项目 GUID。
/// </summary>
[McpServerToolType]
public sealed class SessionMcpTools(
    IAgentSessionService sessions,
    ITaskService tasks,
    IAgentSessionAccessor? sessionAccessor = null)
{
    [McpServerTool(Name = "workbench_bind_session")]
    [Description(
        "把当前这次干活绑定到工作台里的项目：传你所在仓库的绝对路径，工作台会找到绑定了这个仓库的项目，" +
        "并记住本次会话。之后所有工具调用都会自动归属到该项目与任务，不用再自己传 projectId。" +
        "同一个仓库被多个项目绑过时，返回候选项目名 —— 照着挑一个再传 projectId 绑一次。")]
    public async Task<BindSessionToolResult> BindSessionAsync(
        string repoPath,
        string agentId,
        string? taskKey = null,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var bound = await sessions.BindRepoAsync(repoPath, agentId, projectId: projectId, cancellationToken: cancellationToken);

        if (!bound.Ok || bound.Session is null)
        {
            return new BindSessionToolResult(false, null, bound.Error, bound.CandidateProjects);
        }

        // 顺手把任务挂上：agent 说「我在干 REQ-001」，这次会话的归属就定死了。
        if (!string.IsNullOrWhiteSpace(taskKey))
        {
            var matches = await tasks.ListAsync(
                bound.Session.ProjectId, null, null, null, taskKey, null, cancellationToken);

            var task = matches.FirstOrDefault();

            if (task is null)
            {
                return new BindSessionToolResult(
                    false,
                    bound.Session,
                    $"项目「{bound.Session.ProjectName}」里没有编号为 {taskKey} 的任务。" +
                    "会话已绑定成功，但任务没挂上 —— 先确认编号再重试。",
                    bound.CandidateProjects);
            }

            var attached = await sessions.AttachTaskAsync(bound.Session.Id, task.Id, cancellationToken);

            return new BindSessionToolResult(true, attached ?? bound.Session);
        }

        return new BindSessionToolResult(true, bound.Session);
    }

    [McpServerTool(Name = "workbench_get_session")]
    [Description("查看当前会话：绑的是哪个项目、在哪个仓库、有没有挂着任务。")]
    public async Task<BindSessionToolResult> GetSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var current = sessionAccessor is null
            ? null
            : await sessionAccessor.CurrentAsync(cancellationToken);

        if (current is null)
        {
            return new BindSessionToolResult(
                false,
                null,
                "当前调用没有绑定会话。先调 workbench_bind_session 绑定你所在的仓库。");
        }

        return new BindSessionToolResult(true, current);
    }
}
