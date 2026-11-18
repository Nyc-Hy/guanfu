using System.Net;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Web;

/// <summary>
/// MCP 端点的门禁。
///
/// **为什么必须有**：MCP 的工具是在**服务器上**执行的 —— 文件读的是服务器上的仓库，
/// git 提交、shell 命令也都在服务器上跑。一个团队把观复部署到内网服务器之后，
/// 「谁能连上 /mcp」就等于「谁能读所有项目的代码、跑命令」。
/// 一个人用的时候无所谓（只有他自己），团队用的时候这就是大门敞着。
///
/// 两种钥匙：
/// - **凭证**（推荐）：签发时绑定了身份（成员名 / agent id）与可见项目；
/// - **共享人工凭证**（本机单人用）：数据目录里那个 token —— 它是 owner，全权。
///
/// 认证通过后，身份放进 <see cref="HttpContext.Items"/>，
/// 工具层拿它当「我是谁」与「我能看哪些项目」。
/// </summary>
public sealed class CredentialMiddleware
{
    /// <summary>认证出来的身份放在这里（HttpContext.Items 的键）。</summary>
    internal const string ItemKey = "guanfu.credential";

    /// <summary>请求头名（agent 客户端配置里写这个）。</summary>
    public const string HeaderName = "X-Guanfu-Token";

    /// <summary>也认标准的 Authorization: Bearer。</summary>
    public const string AuthorizationScheme = "Bearer";

    private const string UnauthorizedBody =
        "{\"ok\":false,\"error\":\"" +
        "MCP 端点需要凭证：在请求头 " + HeaderName + "（或 Authorization: Bearer）里带上，" +
        "凭证可以在「访问凭证」页签签发。" +
        "\"}";

    private readonly RequestDelegate _next;

    public CredentialMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsMcpEndpoint(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var token = ReadToken(context.Request);

        var credentials = context.RequestServices.GetService<ICredentialService>();
        CredentialIdentity? identity = null;

        if (credentials is not null && !string.IsNullOrWhiteSpace(token))
        {
            identity = await credentials.AuthenticateAsync(token, context.RequestAborted);
        }

        if (identity is null)
        {
            // 共享人工凭证也算 —— 本机单人用时，agent 用的就是它（owner，全权）。
            // 比对的是**刚才取出来的那把钥匙**，不限于它出现在哪个头里。
            var owner = context.RequestServices.GetService<HumanTokenAccessor>();

            if (owner is not null && HumanTokenPolicy.TokenMatches(token, owner.Token))
            {
                // owner 的钥匙 = 全权（含管理员角色）。
                identity = new CredentialIdentity(
                    CredentialKind.Member,
                    "owner",
                    [],
                    HumanTokenPolicy.Fingerprint(owner.Token) ?? "owner",
                    MemberId: null,
                    MemberRole: MemberRole.Admin);
            }
        }

        if (identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(UnauthorizedBody, context.RequestAborted);
            return;
        }

        context.Items[ItemKey] = identity;

        await _next(context);
    }

    /// <summary>MCP 端点（只有它需要这把钥匙）。</summary>
    private static bool IsMcpEndpoint(PathString path) =>
        path.StartsWithSegments("/mcp");

    /// <summary>
    /// 从请求里取凭证。
    ///
    /// 三处都认：<c>Authorization: Bearer</c>、专用头、旧的
    /// <c>X-Guanfu-Human-Token</c>（agent 的配置里可能还写着它）。
    /// </summary>
    private static string? ReadToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();

        if (!string.IsNullOrWhiteSpace(authorization) &&
            authorization.StartsWith(AuthorizationScheme, StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization[AuthorizationScheme.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                return bearer;
            }
        }

        var dedicated = request.Headers[HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(dedicated))
        {
            return dedicated;
        }

        var legacy = request.Headers[HumanTokenPolicy.LegacyHeaderName].ToString();

        return string.IsNullOrWhiteSpace(legacy)
            ? request.Headers[HumanTokenPolicy.HeaderName].ToString()
            : legacy;
    }
}
