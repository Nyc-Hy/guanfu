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

    /// <summary>
    /// 浏览器自己去取 token 的脚本。
    ///
    /// 必须由浏览器发起：服务端自己调用 /api/human-token 会把「请求来自本机」
    /// 这件事洗白 —— 远程用户也能拿到 token，门禁就没了。
    /// 远程访问时这个接口返回 403，于是退回「粘贴控制台里的 token」这条路。
    /// </summary>
    public const string FetchTokenScript =
        "fetch('/api/human-token',{credentials:'same-origin'})"
        + ".then(function(r){return r.text().then(function(t){return JSON.stringify({ok:r.ok,status:r.status,body:t});});})"
        + ".catch(function(){return JSON.stringify({ok:false,status:0,body:''});})";

    /// <summary>从浏览器拿回来的 JSON 里抠出 token。取不到就当没有。</summary>
    public static string? ExtractToken(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var outer = System.Text.Json.JsonDocument.Parse(payload);

            if (!outer.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                return null;
            }

            if (!outer.RootElement.TryGetProperty("body", out var body))
            {
                return null;
            }

            using var inner = System.Text.Json.JsonDocument.Parse(body.GetString() ?? "{}");

            if (!inner.RootElement.TryGetProperty("token", out var token))
            {
                return null;
            }

            var value = token.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 页面级人工凭证门禁的状态机。审批 / 项目设置 / 启动 agent / MCP 开关共用。
///
/// 这些页面直接调应用服务，不经过 REST，HumanTokenMiddleware 管不到它们 ——
/// 每个敏感操作前都要过一遍 <see cref="Authorized"/>。
/// 纯状态 + 纯函数，不碰 JS / IO，方便单测；取 token 的 JS 调用留在页面里。
/// </summary>
public sealed class UiTokenGate
{
    private readonly string _expectedToken;

    public UiTokenGate(string expectedToken) => _expectedToken = expectedToken ?? string.Empty;

    /// <summary>已经持有有效凭证。</summary>
    public bool Authorized { get; private set; }

    /// <summary>给用户的提示（验证失败 / 自动获取失败）。</summary>
    public string? Hint { get; private set; }

    /// <summary>服务端渲染阶段：请求本身带过凭证（Cookie / 请求头）就直接放行。</summary>
    public void AuthorizeFromRequest(HttpRequest? request)
    {
        if (ApprovalUiGuard.CanDecide(request, _expectedToken))
        {
            Authorized = true;
            Hint = null;
        }
    }

    /// <summary>
    /// 校验用户粘贴的 token。成功则放行并抹掉输入痕迹；失败给提示。
    /// 返回是否通过。
    /// </summary>
    public bool TryVerify(string? presentedToken)
    {
        if (ApprovalUiGuard.CanDecide(presentedToken, _expectedToken))
        {
            Authorized = true;
            Hint = null;
            return true;
        }

        Hint = ApprovalUiGuard.InvalidTokenMessage;
        return false;
    }

    /// <summary>
    /// 消化浏览器自动取 token 的结果（<see cref="ApprovalUiGuard.FetchTokenScript"/> 的返回）。
    /// 已授权时不做任何事。
    /// </summary>
    public void ApplyFetchResult(string? payload)
    {
        if (Authorized)
        {
            return;
        }

        var token = ApprovalUiGuard.ExtractToken(payload);
        if (token is not null && ApprovalUiGuard.CanDecide(token, _expectedToken))
        {
            Authorized = true;
            Hint = null;
        }
        else
        {
            Hint = "没能自动取到人工凭证（远程访问时这是正常的）。请从启动工作台的控制台复制 token 粘贴到上面。";
        }
    }
}
