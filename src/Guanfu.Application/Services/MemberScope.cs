using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 当前调用者的身份范围。
///
/// 团队部署时「每个人只能看到自己的东西」——
/// 这条规则落在**服务层**（不是页面层），所以 REST、MCP、界面三条路一样严。
/// </summary>
/// <param name="MemberId">成员 id。</param>
/// <param name="UserName">登录名。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Role">角色。</param>
/// <param name="AgentId">
/// 这次调用背后是哪个 agent（agent 凭证时才有）。
/// agent 认领的活算它的。
/// </param>
public sealed record MemberScope(
    Guid MemberId,
    string UserName,
    string DisplayName,
    MemberRole Role,
    string? AgentId = null)
{
    /// <summary>管理员看全部。</summary>
    public bool IsAdmin => Role == MemberRole.Admin;
}

/// <summary>
/// 当前身份。
///
/// **谁来填**：
/// - HTTP：宿主把成员会话翻译成 <see cref="MemberScope"/>；
/// - Blazor 界面：由电路处理器在交互操作之前核验身份。
///
/// 没有成员范围时按单人本机、owner 或 MCP 入口各自核验的权限处理。
/// </summary>
public interface ICurrentMemberScope
{
    MemberScope? Current { get; }
}

/// <summary>
/// 可见性规则。
///
/// 普通成员能看见的：
/// 1. 分派给自己的；
/// 2. 自己认领的（agent 凭证：它认领的）；
/// 3. **还没分派的** —— 不然没人能认领它。
///
/// 管理员看全部。
/// </summary>
public static class Visibility
{
    /// <summary>任务列表按身份过滤。</summary>
    public static IQueryable<TaskItem> VisibleTo(this IQueryable<TaskItem> query, MemberScope? scope)
    {
        if (scope is null || scope.IsAdmin)
        {
            return query;
        }

        var memberId = scope.MemberId;
        var agentId = scope.AgentId;

        return query.Where(t =>
            t.AssigneeMemberId == memberId
            || (agentId != null && t.ClaimedByAgentId == agentId)
            || (t.AssigneeMemberId == null
                && (t.Assignee == null || t.Assignee == "")
                && t.ClaimedByAgentId == null));
    }

    /// <summary>审批列表按身份过滤（普通成员只看自己的待批）。</summary>
    public static IQueryable<Approval> VisibleTo(this IQueryable<Approval> query, MemberScope? scope)
    {
        if (scope is null || scope.IsAdmin)
        {
            return query;
        }

        var memberId = scope.MemberId;

        return query.Where(a => a.AssigneeMemberId == memberId);
    }

    /// <summary>这个范围能不能看这个任务。</summary>
    public static bool CanSeeTask(this MemberScope scope, TaskItem task)
    {
        if (scope.IsAdmin)
        {
            return true;
        }

        if (task.AssigneeMemberId == scope.MemberId)
        {
            return true;
        }

        if (scope.AgentId is not null && task.ClaimedByAgentId == scope.AgentId)
        {
            return true;
        }

        return task.AssigneeMemberId is null
            && string.IsNullOrWhiteSpace(task.Assignee)
            && task.ClaimedByAgentId is null;
    }
}
