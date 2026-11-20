using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Web;

/// <summary>
/// 当前身份（谁在操作）。
///
/// 两条来源：
/// - **HTTP 请求**：由成员门禁解析会话 Cookie；
/// - **Blazor 电路**：由电路处理器在交互操作之前核验身份。
///
/// **每次都要核对库**：Cookie 里写着「我是谁」，但那是签发时写下的 ——
/// 成员被**停用**、被删掉、改了角色之后，老 Cookie 还在浏览器里，
/// 不核对就等于「停用不影响已登录的人」。
///
/// **agent 凭证不产生成员范围** —— agent 要看得见整个看板才能干活
/// （调度板、依赖关系都要看）。隔离是给**人**的：普通成员只看自己的东西。
/// 这一点是已知的边界，写在这里，别以后当成漏洞。
/// </summary>
public sealed class CurrentMemberScope : ICurrentMemberScope
{
    /// <summary>
    /// 身份是**环境里的**（ambient），不是某个 scope 的私有字段。
    ///
    /// 为什么：Blazor 的页面为了避开电路级 DbContext 的冲突，会
    /// `ScopeFactory.CreateScope()` 开新的 scope —— **新的 scope 里就没有身份了**。
    /// 那样一来：成员在界面上看到的活**没经过可见性过滤**（REST 过滤了，界面没过滤）——
    /// 隔离在界面上是漏的。
    ///
    /// AsyncLocal 跟着**执行上下文**走：新 scope 仍在同一个上下文里，
    /// 所以身份跟得过去。ASP.NET Core 每个请求一个 ExecutionContext，
    /// 所以请求之间不会串。
    /// </summary>
    private static readonly AsyncLocal<MemberScope?> Ambient = new();

    /// <summary>当前身份。没人登录 = 单人本机模式（不过滤）。</summary>
    public MemberScope? Current => Ambient.Value;

    public void Set(MemberScope? scope) => Ambient.Value = scope;
}

/// <summary>
/// 把登录身份解析成成员范围 —— **查库核对**。
///
/// 人还在、没停用，才算数；名字与角色也以库里的为准
/// （Cookie 里的可能是旧的）。
///
/// 这个类是 scoped（要用库），身份本身由 <see cref="CurrentMemberScope"/>
/// 那个单例存着（AsyncLocal）。
/// </summary>
public sealed class MemberIdentityResolver(
    IHttpContextAccessor http,
    IAppDbContext db)
{
    public Task<bool> HasMembersAsync(CancellationToken cancellationToken = default) =>
        db.Members.AnyAsync(cancellationToken);

    public async Task<MemberScope?> ResolveAsync(ClaimsPrincipal? principal = null)
    {
        principal ??= http.HttpContext?.User;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var memberId = MemberAuth.MemberId(principal);

        if (memberId is null)
        {
            return null;
        }

        var member = await db.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == memberId.Value);

        if (member is null || member.IsDisabled)
        {
            return null;
        }

        return new MemberScope(
            member.Id,
            member.UserName,
            member.DisplayName,
            member.Role);
    }
}

/// <summary>
/// 请求进来时把「现在是谁」解析好。
///
/// 排在 <c>UseAuthentication</c> 之后 —— Cookie 得先被读出来。
/// 解析一次，后面服务层读 <see cref="CurrentMemberScope.Current"/> 就是同步取值，
/// 不用在每个服务里 await 一遍。
/// </summary>
public sealed class MemberScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CurrentMemberScope scope,
        MemberIdentityResolver resolver,
        HumanTokenAccessor tokens)
    {
        var path = context.Request.Path;

        // 分享链接与 MCP 各自核验凭证。框架资源与健康检查无需成员会话。
        if (path.StartsWithSegments("/mcp") || path.StartsWithSegments("/delivery") ||
            path.StartsWithSegments("/_framework") || path == "/health")
        {
            await next(context);
            return;
        }

        var resolved = await resolver.ResolveAsync(context.User);
        var hadSession = context.User.Identity?.IsAuthenticated == true;

        if (resolved is not null)
        {
            context.User = MemberAuth.BuildPrincipal(new Member
            {
                Id = resolved.MemberId,
                UserName = resolved.UserName,
                DisplayName = resolved.DisplayName,
                Role = resolved.Role
            });
        }
        else if (hadSession)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        var route = (path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        var api = path.StartsWithSegments("/api");
        var owner = api && tokens.IsAuthorized(context.Request);
        var publicRoute = route is "/login" or "/api/auth/login" or "/api/auth/logout" or "/api/auth/me";
        var allow = publicRoute || owner || (route != "/api/human-token" && resolved is not null);

        if (!allow && !hadSession && tokens.IsDirectLoopbackRequest(context))
        {
            allow = !await resolver.HasMembersAsync(context.RequestAborted);
        }

        if (!allow)
        {
            if (api || path.StartsWithSegments("/_blazor") || !HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new { ok = false, error = "请登录观复，或为 REST 请求提供人工凭证。" },
                    context.RequestAborted);
            }
            else
            {
                var returnUrl = context.Request.PathBase.Add(path).ToUriComponent() +
                    context.Request.QueryString.ToUriComponent();
                context.Response.Redirect("/login" + QueryString.Create("returnUrl", returnUrl));
            }

            return;
        }

        scope.Set(resolved);
        await next(context);
    }
}

/// <summary>交互操作用当前成员范围。停用、改角色或进入团队模式后，旧电路须重新连接。</summary>
public sealed class MemberCircuitHandler(
    AuthenticationStateProvider authentication,
    IServiceScopeFactory scopeFactory,
    CurrentMemberScope currentMember) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async activity =>
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        using var scope = scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<MemberIdentityResolver>();
        var member = await resolver.ResolveAsync(user);

        var valid = user.Identity?.IsAuthenticated == true
            ? member is not null && MemberAuth.IsAdmin(user) == member.IsAdmin
            : !await resolver.HasMembersAsync();

        if (!valid)
        {
            throw new UnauthorizedAccessException("会话身份已变更，请重新加载页面。");
        }

        currentMember.Set(member);
        await next(activity);
    };
}
