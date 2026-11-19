using Microsoft.EntityFrameworkCore;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Web.Api;

/// <summary>
/// 任务可见性门禁（REST）。
///
/// **为什么需要它**：任务级的端点都是拿 `taskId` 来的 ——
/// 一个成员只要把别人的任务 id 填进 URL，就能读到那个任务的文档、产出、评审、
/// 流水。服务层的过滤只挡得住「列表」，挡不住「按 id 直取」。
///
/// 这道门把「按 id 直取」也收进同一套规则：
/// 看不见的任务 → 404（跟「不存在」一样，不告诉对方「它存在，只是你不能看」）。
/// </summary>
public sealed class TaskVisibilityFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var taskId = ResolveTaskId(context);

        if (taskId is not null)
        {
            // 从请求的服务里取（不注入）：没注册身份服务时这道门直接放行 ——
            // 别让「没装身份模块」变成 500。
            var currentMember = context.HttpContext.RequestServices
                .GetService<ICurrentMemberScope>();

            var scope = currentMember?.Current;

            // 没人登录 = 单人本机模式，不拦。
            if (scope is not null && !scope.IsAdmin)
            {
                var visible = await IsVisibleAsync(context, taskId.Value, scope);

                if (!visible)
                {
                    return Results.NotFound(new { error = "任务不存在。" });
                }
            }
        }

        return await next(context);
    }

    /// <summary>从路由值里取 taskId（没有就 null）。</summary>
    private static Guid? ResolveTaskId(EndpointFilterInvocationContext context)
    {
        foreach (var pair in context.HttpContext.Request.RouteValues)
        {
            if (!string.Equals(pair.Key, "taskId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value is Guid id)
            {
                return id;
            }

            if (pair.Value is string text && Guid.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static async Task<bool> IsVisibleAsync(
        EndpointFilterInvocationContext context,
        Guid taskId,
        MemberScope scope)
    {
        // 用这一层自己的 scope 查（不依赖 ambient）——
        // 端点过滤器拿得到 HttpContext，可以从那儿解析。
        var db = context.HttpContext.RequestServices.GetService(typeof(IAppDbContext)) as IAppDbContext;

        if (db is null)
        {
            return true;
        }

        var visible = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new
            {
                t.Id,
                t.AssigneeMemberId,
                t.Assignee,
                t.ClaimedByAgentId
            })
            .FirstOrDefaultAsync();

        if (visible is null)
        {
            return false;
        }

        return visible.AssigneeMemberId == scope.MemberId
            || (scope.AgentId is not null && visible.ClaimedByAgentId == scope.AgentId)
            || (visible.AssigneeMemberId is null
                && string.IsNullOrWhiteSpace(visible.Assignee)
                && visible.ClaimedByAgentId is null);
    }
}
