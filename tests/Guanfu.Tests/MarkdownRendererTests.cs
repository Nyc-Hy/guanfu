using System.Text;
using Guanfu.Documents;

namespace Guanfu.Tests;

/// <summary>
/// Markdown → HTML 的渲染安全底线。
///
/// 这里盯的是两个**实测**过的 XSS payload：
///   1. <c>![a](https://e/a.png){onerror=alert(1)}</c>
///      —— UseAdvancedExtensions() 里的 GenericAttributes 会把 {onerror=...} 直接挂成标签属性，
///      DisableHtml() 拦不住它（它压根不是 HTML），修复前渲染出 &lt;img ... onerror="alert(1)"&gt;。
///   2. <c>[&lt;javascript:alert(1)&gt;](javascript:void(0))</c>
///      —— 外层链接被降级时把子节点原样搬走，子节点里的 AutolinkInline 没人消毒，
///      修复前渲染出 &lt;a href="javascript:alert(1)"&gt;。
///
/// 断言口径：危险串不能出现在**标签**里（属性 / URL）。
/// 被降级的内容会以转义后的纯文本留在页面上（`{onerror=alert(1)}`、`javascript:alert(1)`），
/// 这是正确行为 —— 用户看得见原文，浏览器不会执行它，所以不能对整串 HTML 做裸子串断言。
/// </summary>
public class MarkdownRendererTests
{
    private const string ImageAttributePayload = "![a](https://e/a.png){onerror=alert(1)}";
    private const string NestedAutolinkPayload = "[<javascript:alert(1)>](javascript:void(0))";

    [Fact]
    public void 实测payload_图片上的事件属性注入被挡掉()
    {
        var html = MarkdownRenderer.ToHtml(ImageAttributePayload);

        AssertNoScriptCarrier(html);

        // 图片本身还在，只是属性注入没了；{...} 退化成可见的纯文本
        Assert.Contains("<img src=\"https://e/a.png\" alt=\"a\" />", html);
        Assert.DoesNotContain("onerror=\"", html);
        Assert.DoesNotContain("onclick=\"", html);
    }

    [Fact]
    public void 实测payload_链接文字里的自动链接不会漏出javascript协议()
    {
        var html = MarkdownRenderer.ToHtml(NestedAutolinkPayload);

        AssertNoScriptCarrier(html);

        // 链接壳整个没了，只剩文字
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("href=", html);
    }

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("![a](https://e/a.png){onerror=alert(1) onload=alert(2)}")]
    [InlineData("[x](https://ok.com){onclick=alert(1)}")]
    [InlineData("[点我](<java\tscript:alert(1)>)")]
    public void 裸HTML与其它属性注入同样挡住(string markdown)
    {
        var html = MarkdownRenderer.ToHtml(markdown);

        AssertNoScriptCarrier(html);
        Assert.DoesNotContain("onerror=\"", html);
        Assert.DoesNotContain("onclick=\"", html);
    }

    [Fact]
    public void 标题上的属性注入不会变成事件属性()
    {
        var html = MarkdownRenderer.ToHtml("# h{onclick=alert(1)}");
        var tags = TagsOnly(html);

        // AutoIdentifiers 会把 {..} 里的字符揉进 id（id="honclickalert1"），
        // 所以这里断言的是「属性赋值」形式，而不是裸单词
        Assert.DoesNotContain("onclick=", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick=\"", html);
        Assert.StartsWith("<h1 ", html);
    }

    [Theory]
    [InlineData("~~删~~", "<del>删</del>")]
    [InlineData("| a | b |\n| --- | --- |\n| 1 | 2 |", "<table>")]
    [InlineData("- [x] done", "type=\"checkbox\" checked=\"checked\"")]
    [InlineData("# 标题", "<h1 id=")]
    [InlineData("https://example.com", "<a href=\"https://example.com\">")]
    [InlineData("正文[^1]\n\n[^1]: 注", "class=\"footnotes\"")]
    [InlineData("$x^2$", "class=\"math\"")]
    [InlineData("a~b~ c^d^", "<sub>b</sub>")]
    [InlineData("> [!NOTE]\n> 提示", "markdown-alert-note")]
    [InlineData(":::\n内容\n:::", "<div>")]
    [InlineData("术语\n\n*[术语]: 解释", "<abbr title=\"解释\">")]
    public void 剔除GenericAttributes不会顺手关掉其它高级扩展(string markdown, string expected)
    {
        // 修复手法是「显式列出 UseAdvancedExtensions 的其余扩展」，
        // 漏一个就是功能回退，所以逐个盯住
        Assert.Contains(expected, MarkdownRenderer.ToHtml(markdown));
    }

    [Fact]
    public void 安全链接保留内部的强调()
    {
        var html = MarkdownRenderer.ToHtml("[**bold** link](https://ok.com)");

        Assert.Contains("<a href=\"https://ok.com\"><strong>bold</strong> link</a>", html);
    }

    [Fact]
    public void 降级不安全链接时不会把安全子链接一起删掉()
    {
        var html = MarkdownRenderer.ToHtml("[a <https://ok.com> b](javascript:alert(1))");

        // 递归消毒只摘危险的那一层，安全的子链接要留下
        Assert.Contains("<a href=\"https://ok.com\">", html);
        AssertNoScriptCarrier(html);
    }

    [Fact]
    public void 空输入返回空串()
    {
        Assert.Equal(string.Empty, MarkdownRenderer.ToHtml(null));
        Assert.Equal(string.Empty, MarkdownRenderer.ToHtml("   "));
    }

    /// <summary>标签里不许出现任何能执行脚本的载体。</summary>
    private static void AssertNoScriptCarrier(string html)
    {
        var tags = TagsOnly(html);

        Assert.DoesNotContain("onerror", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", tags, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 只取 HTML 里的标签部分（&lt;...&gt;），丢掉标签之间的文本。
    /// 不用正则：畸形 HTML 能绕过正则，逐字符扫描不会。
    /// </summary>
    private static string TagsOnly(string html)
    {
        var builder = new StringBuilder();
        var inTag = false;

        foreach (var c in html)
        {
            if (c == '<')
            {
                inTag = true;
            }

            if (inTag)
            {
                builder.Append(c);
            }

            if (c == '>')
            {
                inTag = false;
            }
        }

        return builder.ToString();
    }
}