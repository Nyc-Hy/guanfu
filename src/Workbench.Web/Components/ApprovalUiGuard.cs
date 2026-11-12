using Microsoft.AspNetCore.Http;

namespace Workbench.Web.Components;

/// <summary>
/// 审批页的人工凭证门禁。纯函数，不碰 IO，方便单测。
///
/// 为什么审批页要自己判一次：审批页直接调应用服务裁决，不经过 REST，
/// 所以 HumanTokenMiddleware 那道门管不到它 ——
/// 少了这一判，内网里任何能打开页面的人点一下「批准」就真的批了。
///
/// 判定口径统一成「手上这份凭证必须和服务端的 token 一致」
/// （HumanTokenPolicy.TokenMatches，固定时间比较）；
/// 请求头 / Cookie 那一路等价于「浏览器已经拿到过凭证」。
/// </summary>
public static class ApprovalUiGuard
{
    /// <summary>没凭证时给用户看的话。</summary>
    public const string MissingTokenMessage = "请先提供人工凭证再裁决审批。";

    /// <summary>凭证不对时给用户看的话。</summary>
    public const string InvalidTokenMessage = "人工凭证不对。请从启动工作台的控制台复制正确的 token 再试。";

    /// <summary>手上的凭证与服务端 token 是否一致。</summary>
    public static bool CanDecide(string? presentedToken, string expectedToken) =>
        HumanTokenPolicy.TokenMatches(presentedToken, expectedToken);

    /// <summary>请求本身带没带凭证（服务端渲染时能看到请求才有意义）。</summary>
    public static bool CanDecide(HttpRequest? request, string expectedToken) =>
        request is not null
        && HumanTokenPolicy.IsAuthorized(
            request.Headers[HumanTokenPolicy.HeaderName],
            request.Cookies[HumanTokenPolicy.CookieName],
            expectedToken);
}
