using System.Security.Claims;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Web;

/// <summary>
/// 当前身份（谁在操作）。
///
/// 两条来源：
/// - **HTTP 请求**（REST / MCP）：从认证结果读 ——
///   成员会话（Cookie）或 agent 凭证（<c>/mcp</c> 的门禁放在 Items 里）；
/// - **Blazor 电路**：电路里拿不到 HttpContext，
///   由布局把身份回填进来（<see cref="SetFallback"/>）。
///
/// **agent 凭证不产生成员范围** —— agent 要看得见整个看板才能干活
/// （调度板、依赖关系都要看）。隔离是给**人**的：普通成员只看自己的东西。
/// 这一点是已知的边界，写在这里，别以后当成漏洞。
/// </summary>
public sealed class CurrentMemberScope(IHttpContextAccessor http) : ICurrentMemberScope
{
    private MemberScope? _fallback;

    /// <summary>Blazor 电路里的身份回填（由 MainLayout 调用）。</summary>
    public void SetFallback(MemberScope? scope) => _fallback = scope;

    public MemberScope? Current
    {
        get
        {
            var context = http.HttpContext;

            if (context is not null)
            {
                var fromSession = FromPrincipal(context.User);

                if (fromSession is not null)
                {
                    return fromSession;
                }

                // agent 凭证：不给成员范围（agent 看全局看板）。
                return null;
            }

            return _fallback;
        }
    }

    /// <summary>从登录身份里读出成员范围。</summary>
    public static MemberScope? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var memberId = MemberAuth.MemberId(principal);

        if (memberId is null)
        {
            return null;
        }

        var role = principal.FindFirst(MemberAuth.RoleClaim)?.Value;
        var parsedRole = Enum.TryParse<MemberRole>(role, ignoreCase: true, out var r)
            ? r
            : MemberRole.Member;

        return new MemberScope(
            memberId.Value,
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
            principal.Identity.Name ?? string.Empty,
            parsedRole);
    }
}
