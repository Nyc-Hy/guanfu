using System.Net;
using System.Text;

namespace Guanfu.Documents;

/// <summary>
/// HTML 正文抽取。
///
/// 项目里没有 HTML DOM 解析库，也不打算为这个兜底格式引入新依赖，
/// 所以这里用一个手写扫描器代替正则：
///   1. 整块剔掉 script / style / 注释 —— 它们的源码不是正文；
///   2. title 单独抽出来当标题，不混进正文（否则标题会在正文里重复一遍）；
///   3. 块级标签断开行，其余标签丢掉；
///   4. HTML 实体用 WebUtility 解码（&amp; &lt; &nbsp; &#58; 这些）。
///
/// 真要上真正的 DOM 解析器，得先确认能不能引入 AngleSharp 之类的包。
/// </summary>
internal static class HtmlTextExtractor
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "blockquote", "pre", "table", "thead", "tbody",
        "ul", "ol", "dl", "dt", "dd", "br", "hr", "header", "footer", "nav",
        "aside", "main", "figure", "figcaption", "form", "fieldset", "address"
    };

    public static (string Title, string Text) Extract(string html)
    {
        var title = string.Empty;
        var body = new StringBuilder();

        var index = 0;
        while (index < html.Length)
        {
            var c = html[index];
            if (c != '<')
            {
                var next = html.IndexOf('<', index);
                if (next < 0)
                {
                    next = html.Length;
                }

                body.Append(html, index, next - index);
                index = next;
                continue;
            }

            // 注释：整块丢掉
            if (Matches(html, index, "<!--"))
            {
                var end = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = end < 0 ? html.Length : end + 3;
                continue;
            }

            // "<" 后面不是字母 / 斜杠 / 感叹号 —— 不是标签，是正文里的普通字符
            if (!IsTagStart(html, index))
            {
                body.Append('<');
                index++;
                continue;
            }

            var tagEnd = FindTagEnd(html, index);
            if (tagEnd < 0)
            {
                // 没有闭合的 '>'，后面按纯文本处理
                body.Append(html, index, html.Length - index);
                break;
            }

            var tag = html[index..(tagEnd + 1)];
            index = tagEnd + 1;

            var name = TagName(tag);
            var isClosing = tag.StartsWith("</", StringComparison.Ordinal);

            if (name is "script" or "style")
            {
                if (!isClosing)
                {
                    // 整块内容都跳过：脚本源码不是正文
                    var close = IndexOfClosingTag(html, index, name);
                    index = close < 0 ? html.Length : close;
                }

                continue;
            }

            if (name == "title")
            {
                if (!isClosing)
                {
                    var close = IndexOfClosingTag(html, index, name);
                    var end = close < 0 ? html.Length : close;
                    title = WebUtility.HtmlDecode(html[index..end]).Trim();
                    index = close < 0 ? html.Length : close;
                }

                continue;
            }

            if (BlockTags.Contains(name))
            {
                // 开标签与闭标签都断行：<p>a</p><p>b</p> 不能黏成一行
                body.Append('\n');
            }
        }

        var text = Normalize(WebUtility.HtmlDecode(body.ToString()));
        return (title, text);
    }

    /// <summary>判断 '&lt;' 后面是不是标签的开始。</summary>
    private static bool IsTagStart(string html, int index)
    {
        if (index + 1 >= html.Length)
        {
            return false;
        }

        var c = html[index + 1];
        return char.IsLetter(c) || c is '/' or '!' or '?';
    }

    /// <summary>找到标签的结束 '&gt;'，注意跳过引号里的 '&gt;'。</summary>
    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>取标签名（小写）。</summary>
    private static string TagName(string tag)
    {
        // 开标签 "<p" 从 1 开始，闭标签 "</p" 从 2 开始
        var start = tag.StartsWith("</", StringComparison.Ordinal) ? 2 : 1;
        var end = start;
        while (end < tag.Length && (char.IsLetterOrDigit(tag[end]) || tag[end] == '-'))
        {
            end++;
        }

        return tag[start..end].ToLowerInvariant();
    }

    /// <summary>找结束标签的位置（'&lt;' 的下标），找不到返回 -1。</summary>
    private static int IndexOfClosingTag(string html, int start, string name)
    {
        var index = start;
        while (index >= 0 && index < html.Length)
        {
            var next = html.IndexOf("</", index, StringComparison.Ordinal);
            if (next < 0)
            {
                return -1;
            }

            var end = next + 2;
            var tagStart = end;
            while (end < html.Length && char.IsLetterOrDigit(html[end]))
            {
                end++;
            }

            if (html[tagStart..end].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return next;
            }

            index = next + 2;
        }

        return -1;
    }

    private static bool Matches(string html, int index, string prefix) =>
        index + prefix.Length <= html.Length &&
        html.AsSpan(index, prefix.Length).SequenceEqual(prefix);

    /// <summary>归一化空白：逐行裁掉首尾空格，去掉空行。</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }
}
