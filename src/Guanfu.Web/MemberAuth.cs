using System.Security.Claims;
using Guanfu.Domain;

namespace Guanfu.Web;

/// <summary>
/// 成员登录的口径（会话 Cookie 与声明）。
///
/// **为什么要有这一套**：一个人用的时候，本机那把共享人工凭证就够 ——
/// 「谁在操作」不是问题。团队用的时候它是问题：
/// 那把 token 是**共享**的，谁拿到都能用，审计里只能记一个指纹。
///
/// 所以团队部署走「成员 + 密码」：登录之后浏览器带一枚签名 Cookie，
/// Cookie 里装的是成员身份 —— 谁点的、什么角色，事后查得到。
///
/// 人工凭证**没有作废**：它是「本机引导 / 紧急恢复」用的 ——
/// 第一个管理员账号就是拿它建出来的。
/// </summary>
public static class MemberAuth
{
    /// <summary>会话 Cookie 名。</summary>
    public const string CookieName = "guanfu_session";

    /// <summary>角色声明（管理员 / 成员）。</summary>
    public const string RoleClaim = "guanfu-role";

    /// <summary>登录名（Cookie 里带着，显示名之外的另一个身份）。</summary>
    public const string UserNameClaim = "guanfu-username";

    /// <summary>成员 id 的声明。</summary>
    public const string MemberIdClaim = "guanfu-member";

    /// <summary>会话的有效期。</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    /// <summary>按成员构造登录身份。</summary>
    public static ClaimsPrincipal BuildPrincipal(Member member)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, member.Id.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(member.DisplayName)
                ? member.UserName
                : member.DisplayName),
            new(MemberIdClaim, member.Id.ToString()),
            new(UserNameClaim, member.UserName),
            new(RoleClaim, member.Role.ToString())
        };

        var identity = new ClaimsIdentity(claims, "guanfu-member", ClaimTypes.Name, RoleClaim);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>从登录身份里取出成员 id。</summary>
    public static Guid? MemberId(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(MemberIdClaim)?.Value;

        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>是不是管理员。</summary>
    public static bool IsAdmin(ClaimsPrincipal? principal)
    {
        var role = principal?.FindFirst(RoleClaim)?.Value;

        return string.Equals(role, nameof(MemberRole.Admin), StringComparison.Ordinal);
    }
}
