using System.Net;

namespace Workbench.Web;

/// <summary>
/// 敏感端点的人工凭证门禁。
///
/// 放在最前面（静态文件 / 路由之前）：敏感端点没凭证就别让它走到业务代码。
/// </summary>
public sealed class HumanTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HumanTokenAccessor _tokens;

    public HumanTokenMiddleware(RequestDelegate next, HumanTokenAccessor tokens)
    {
        _next = next;
        _tokens = tokens;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HumanTokenPolicy.IsSensitiveEndpoint(context.Request.Method, context.Request.Path.Value))
        {
            // 普通端点保持开放：读 + 普通任务写，agent / CI 都要用
            await _next(context);
            return;
        }

        if (_tokens.IsAuthorized(context.Request))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(HumanTokenPolicy.UnauthorizedBody, context.RequestAborted);
    }
}

/// <summary>
/// 记下转发头生效之前的真实对端地址。
///
/// 必须排在 UseForwardedHeaders 之前：不然「本机引导」接口会被 X-Forwarded-For
/// 骗过去 —— 远程攻击者只要带一个 X-Forwarded-For: 127.0.0.1 就能伪装成本机。
/// </summary>
public sealed class OriginalRemoteAddressMiddleware
{
    internal const string ItemKey = "workbench.original-remote-address";

    private readonly RequestDelegate _next;

    public OriginalRemoteAddressMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Items[ItemKey] = context.Connection.RemoteIpAddress;
        return _next(context);
    }

    /// <summary>转发之前的对端地址；中间件没跑过就是 null。</summary>
    public static IPAddress? Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as IPAddress : null;
}
