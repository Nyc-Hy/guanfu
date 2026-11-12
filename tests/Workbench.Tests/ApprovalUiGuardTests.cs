using Microsoft.AspNetCore.Http;
using Workbench.Web;
using Workbench.Web.Components;

namespace Workbench.Tests;

/// <summary>
/// 审批页的人工凭证门禁。
///
/// 核心承诺：**没有人工凭证就不许裁决审批**。
/// 审批页直接调应用服务，不经过 REST，所以 REST 上那道门管不到它 ——
/// 这里的判断就是最后一道闸。
/// </summary>
public class ApprovalUiGuardTests
{
    private const string Token = "test-token-abc123";

    // ---- token 比对 ----

    [Fact]
    public void 正确的token匹配()
    {
        Assert.True(HumanTokenPolicy.TokenMatches(Token, Token));
    }

    [Fact]
    public void 错误的token不匹配()
    {
        Assert.False(HumanTokenPolicy.TokenMatches("wrong-token", Token));
        Assert.False(HumanTokenPolicy.TokenMatches(Token + "x", Token));
        Assert.False(HumanTokenPolicy.TokenMatches(Token[..^1], Token));
    }

    [Fact]
    public void token比对区分大小写()
    {
        Assert.False(HumanTokenPolicy.TokenMatches(Token.ToUpperInvariant(), Token));
    }

    [Fact]
    public void 空串和null都不匹配()
    {
        Assert.False(HumanTokenPolicy.TokenMatches(null, Token));
        Assert.False(HumanTokenPolicy.TokenMatches(string.Empty, Token));
        Assert.False(HumanTokenPolicy.TokenMatches(Token, null!));
        Assert.False(HumanTokenPolicy.TokenMatches(Token, string.Empty));
    }

    // ---- 审批页的门禁 ----

    [Fact]
    public void 没有凭证不能裁决()
    {
        Assert.False(ApprovalUiGuard.CanDecide((string?)null, Token));
        Assert.False(ApprovalUiGuard.CanDecide(string.Empty, Token));
        Assert.False(ApprovalUiGuard.CanDecide("   ", Token));
    }

    [Fact]
    public void 凭证正确可以裁决()
    {
        Assert.True(ApprovalUiGuard.CanDecide(Token, Token));
    }

    [Fact]
    public void 凭证不匹配不能裁决()
    {
        Assert.False(ApprovalUiGuard.CanDecide("wrong-token", Token));
        Assert.False(ApprovalUiGuard.CanDecide(Token.ToUpperInvariant(), Token));
    }

    // ---- 请求本身带凭证（服务端渲染时能看到请求的那条路）----

    [Fact]
    public void 没有请求就不能裁决()
    {
        Assert.False(ApprovalUiGuard.CanDecide((HttpRequest?)null, Token));
    }

    [Fact]
    public void 请求头带正确凭证可以裁决()
    {
        var request = BuildRequest(headerToken: Token);

        Assert.True(ApprovalUiGuard.CanDecide(request, Token));
    }

    [Fact]
    public void Cookie带正确凭证可以裁决()
    {
        var request = BuildRequest(cookieToken: Token);

        Assert.True(ApprovalUiGuard.CanDecide(request, Token));
    }

    [Fact]
    public void 请求带错凭证不能裁决()
    {
        Assert.False(ApprovalUiGuard.CanDecide(BuildRequest(headerToken: "wrong"), Token));
        Assert.False(ApprovalUiGuard.CanDecide(BuildRequest(cookieToken: "wrong"), Token));
        Assert.False(ApprovalUiGuard.CanDecide(BuildRequest(), Token));
    }

    // ---- 页面错误提示 ----

    [Fact]
    public void 页面错误是人话加技术细节()
    {
        var error = PageError.From(new InvalidOperationException("boom"), "加载审批列表");

        // 人话部分要说清楚是哪个动作失败了
        Assert.Contains("加载审批列表", error.Message);
        Assert.DoesNotContain("boom", error.Message);

        // 技术细节不能吞掉：类型、消息、堆栈都要留着
        Assert.Contains("InvalidOperationException", error.Details);
        Assert.Contains("boom", error.Details);
    }

    [Fact]
    public void 简单提示没有技术细节()
    {
        var error = PageError.Simple("凭证不对");

        Assert.Equal("凭证不对", error.Message);
        Assert.True(string.IsNullOrEmpty(error.Details));
        Assert.True(error.IsError);
    }

    [Fact]
    public void 一般提示不算错误()
    {
        var notice = PageError.Notice("已拉起外部 agent");

        Assert.Equal("已拉起外部 agent", notice.Message);
        Assert.False(notice.IsError);
    }

    // ---- 防重复提交 ----

    [Fact]
    public void 同一个键只能提交一次()
    {
        var latch = new SubmitLatch();

        Assert.False(latch.IsLatched("a"));
        Assert.True(latch.TryLatch("a"));
        Assert.True(latch.IsLatched("a"));

        // 再占一次也是「已经占过了」
        Assert.False(latch.TryLatch("a"));
    }

    [Fact]
    public void 失败的提交要放掉闩锁()
    {
        var latch = new SubmitLatch();

        latch.TryLatch("a");
        latch.Release("a");

        Assert.False(latch.IsLatched("a"));
        Assert.True(latch.TryLatch("a"));
    }

    [Fact]
    public void 不同的键互不影响()
    {
        var latch = new SubmitLatch();

        latch.TryLatch("a");

        Assert.False(latch.IsLatched("b"));
        Assert.True(latch.TryLatch("b"));
    }

    private static HttpRequest BuildRequest(string? headerToken = null, string? cookieToken = null)
    {
        var context = new DefaultHttpContext();

        if (headerToken is not null)
        {
            context.Request.Headers[HumanTokenPolicy.HeaderName] = headerToken;
        }

        if (cookieToken is not null)
        {
            context.Request.Headers["Cookie"] = $"{HumanTokenPolicy.CookieName}={cookieToken}";
        }

        return context.Request;
    }
}
