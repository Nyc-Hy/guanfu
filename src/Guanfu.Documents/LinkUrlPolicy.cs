using System.Net;
using System.Text;

namespace Guanfu.Documents;

/// <summary>
/// 链接 URL 的安全策略。
///
/// agent 回写的 Markdown 会被渲染成 MarkupString 直接塞进页面，
/// 所以 href / src 里的 scheme 必须过白名单：
/// 只放行 http / https / mailto 以及相对路径、页内锚点；
/// javascript: / data: / vbscript: / file: 一律不放行（调用方负责降级成纯文本）。
///
/// 为什么不用正则去改 HTML 串：正则会破坏已经转义过的内容，
/// 而且畸形 HTML 能绕过它。判断放在 URL 本身，只做「放行 / 不放行」的决策，
/// 真正的降级由 <see cref="MarkdownLinkSanitizer"/> 在 AST 上完成。
/// </summary>
public static class LinkUrlPolicy
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>URL 是否可以安全地放进 href / src。</summary>
    public static bool IsSafe(string? url)
    {
        // 空目标没有 scheme，点开也只是刷新页面，无害
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var normalized = Normalize(url);
        if (normalized.Length == 0)
        {
            return true;
        }

        // 协议相对 URL（//host/path）：看着像相对路径，实际会跳到外站
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var colon = IndexOfSchemeSeparator(normalized);
        if (colon < 0)
        {
            // 相对路径 / 页内锚点 / 查询串，没有 scheme
            return true;
        }

        var scheme = normalized[..colon];
        if (!IsValidScheme(scheme))
        {
            // 形如 "1abc:" 的畸形 scheme 一律不放行
            return false;
        }

        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按浏览器认 URL 的规则归一化：去掉空白与控制字符、解 HTML 实体。
    ///
    /// 浏览器在判断 scheme 之前会先剥掉 URL 里的制表符/换行，
    /// 源码里的 "java&#9;script:" 在浏览器眼里也是 "javascript:"，
    /// 所以判断之前必须先归一化，否则能被绕过。
    /// </summary>
    private static string Normalize(string url)
    {
        var builder = new StringBuilder(url.Length);
        foreach (var ch in url)
        {
            if (ch <= ' ' || ch == '\u007F')
            {
                continue;
            }

            builder.Append(ch);
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }

    /// <summary>找到 scheme 的分隔符 ':'。它必须出现在 '/' '?' '#' 之前，否则只是路径里的冒号。</summary>
    private static int IndexOfSchemeSeparator(string url)
    {
        for (var i = 0; i < url.Length; i++)
        {
            var c = url[i];
            if (c == ':')
            {
                return i;
            }

            if (c is '/' or '?' or '#')
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>RFC 3986 的 scheme 语法：字母开头，后面跟字母数字或 + - .</summary>
    private static bool IsValidScheme(string scheme)
    {
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        for (var i = 1; i < scheme.Length; i++)
        {
            var c = scheme[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }
}
