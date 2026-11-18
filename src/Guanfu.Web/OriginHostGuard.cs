using Microsoft.AspNetCore.Http;

namespace Guanfu.Web;

/// <summary>
/// 跨站 / DNS rebinding 防护的策略层。纯函数，不碰 IO，方便单测。
///
/// 两道检查：
/// 1. Origin：/api 的写方法（POST/PUT/DELETE/PATCH）带了 Origin 头，
///    但 Origin 的 host:port 与请求 Host 不一致 → 跨站请求，拒。
///    浏览器同源请求的 Origin 一定等于 Host；CLI / MCP / 脚本不带 Origin，放行。
///    手工 ReadFormAsync 的上传（/api/tasks/{id}/documents）也是 /api 写，同样覆盖。
/// 2. Host：Host 头不在允许清单（默认回环）→ 拒。
///    DNS rebinding 的页面跑在攻击者域名下，浏览器发出的 Host 就是那个域名 ——
///    只监听回环也挡不住它，必须验 Host。内网部署用 Guanfu:AllowedHosts 显式放行。
/// </summary>
public static class OriginHostPolicy
{
    /// <summary>默认只认回环。内网部署要在配置里显式加自己的主机名 / IP。</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>Host 头（可带端口）是不是在允许清单里。缺失 / 空一律不算。</summary>
    public static bool IsAllowedHost(string? hostHeader, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(hostHeader) || allowedHosts.Count == 0)
        {
            return false;
        }

        // HostString 正确处理 [::1]:5000 这种带方括号的 IPv6，
        // 但它的 Host 属性会保留方括号（"[::1]"）—— 归一化掉再比，
        // 才能和允许清单里的 "::1" 以及 Uri.Host 的 "::1" 对上。
        var host = new HostString(hostHeader).Host?.Trim('[', ']');
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        foreach (var allowed in allowedHosts)
        {
            var trimmed = allowed?.Trim().Trim('[', ']');
            if (!string.IsNullOrEmpty(trimmed)
                && string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这条请求要不要做 Origin 校验：/api 下的写方法。
    /// 读不带副作用，跨站读到的也只是本机数据（Host 校验仍然兜底）。
    /// </summary>
    public static bool RequiresOriginCheck(string? method, string? path)
    {
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // /api 本身或 /api/... 都算；/apixxx 不算
        if (path.Length > 4 && path[4] != '/' && path[4] != '?')
        {
            return false;
        }

        return IsWriteMethod(method);
    }

    /// <summary>
    /// Origin 与请求 Host 是否同源。没有 Origin 头 = 非浏览器请求，放行。
    /// Origin 是 "null"（沙箱 iframe / file://）或解析不出 host 的一律拒。
    /// </summary>
    public static bool IsOriginAllowed(string? originHeader, string? hostHeader, string? requestScheme)
    {
        if (string.IsNullOrEmpty(originHeader))
        {
            return true;
        }

        if (!Uri.TryCreate(originHeader, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        var host = new HostString(hostHeader);
        // .NET 8 里 Uri.Host 与 HostString.Host 对 IPv6 都保留方括号（"[::1]"），
        // 但允许清单 / 不同来源可能带或不带 —— 两边都归一化掉方括号再比。
        var hostName = host.Host?.Trim('[', ']');
        var originHost = origin.Host?.Trim('[', ']');
        if (string.IsNullOrEmpty(hostName))
        {
            return false;
        }

        if (!string.Equals(originHost, hostName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 端口要归一化：Origin 里 http 默认 80 / https 默认 443 会被 Uri 省略，
        // Host 头里却可能显式写着 —— 两边都补上各自协议的默认端口再比。
        var originPort = origin.Port;
        var hostPort = host.Port ?? DefaultPort(requestScheme);
        return originPort == hostPort;
    }

    private static bool IsWriteMethod(string method) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

    private static int DefaultPort(string? scheme) =>
        string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}

/// <summary>
/// 跨站 / DNS rebinding 防护中间件。放在转发头之后、限流与业务之前。
/// 拒绝时返回 403 + 结构化 JSON（与其余门禁口径一致）。
/// </summary>
public sealed class OriginHostGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyCollection<string> _allowedHosts;

    public OriginHostGuardMiddleware(RequestDelegate next, IReadOnlyCollection<string> allowedHosts)
    {
        _next = next;
        _allowedHosts = allowedHosts.Count > 0 ? allowedHosts : OriginHostPolicy.DefaultAllowedHosts;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var hostHeader = request.Host.HasValue ? request.Host.Value : null;

        // Host 校验对所有请求生效（含 /mcp 与页面）：CLI 用 localhost 连接不受影响
        if (!OriginHostPolicy.IsAllowedHost(hostHeader, _allowedHosts))
        {
            await RejectAsync(context, "Host 头不在允许清单里。内网部署请配置 Guanfu:AllowedHosts。");
            return;
        }

        if (OriginHostPolicy.RequiresOriginCheck(request.Method, request.Path.Value)
            && !OriginHostPolicy.IsOriginAllowed(request.Headers.Origin, hostHeader, request.Scheme))
        {
            await RejectAsync(context, "跨站写请求被拒绝：Origin 与 Host 不一致。");
            return;
        }

        await _next(context);
    }

    private static async Task RejectAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            "{\"ok\":false,\"error\":\"" + reason + "\"}",
            context.RequestAborted);
    }
}

/// <summary>从配置读允许的主机清单。支持数组与逗号分隔字符串两种写法。</summary>
public static class OriginHostAllowedHosts
{
    public const string ConfigKey = "Guanfu:AllowedHosts";

    /// <summary>改名之前的键 —— 仍然认，别让人以为配置丢了。</summary>
    public const string LegacyConfigKey = "Workbench:AllowedHosts";

    public static IReadOnlyList<string> Read(IConfiguration configuration)
    {
        // 新键优先；旧键（改名之前配的）仍然认 —— 改名不该让人以为配置丢了
        foreach (var key in new[] { ConfigKey, LegacyConfigKey })
        {
            var items = ReadFrom(configuration, key);
            if (items is { Count: > 0 })
            {
                return items;
            }
        }

        return OriginHostPolicy.DefaultAllowedHosts;
    }

    private static IReadOnlyList<string>? ReadFrom(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var items = section.Get<string[]>()?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        if (items is null || items.Count == 0)
        {
            var raw = configuration[key];
            if (!string.IsNullOrWhiteSpace(raw))
            {
                items = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
        }

        return items;
    }
}