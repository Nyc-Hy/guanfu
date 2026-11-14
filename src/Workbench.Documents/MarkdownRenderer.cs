using Markdig;

namespace Workbench.Documents;

/// <summary>
/// Markdown → HTML。
///
/// 给「页面上直接看生成的文档」用：agent 回写的设计文档、计划、测试报告
/// 大多是 Markdown，直接当纯文本显示太难读。
///
/// **刻意关掉原始 HTML** —— 内容来自 agent 与外部上传的文档，属于不可信输入，
/// 渲染成 MarkupString 之前必须把裸 HTML 摘掉，不然就是 XSS 口子。
///
/// 另外还要卡链接的 scheme：`javascript:` / `data:` 这类 URL 在 href 里
/// 一样能执行脚本，所以渲染前在 AST 上把它们降级成纯文字
/// （见 <see cref="MarkdownLinkSanitizer"/> 与 <see cref="LinkUrlPolicy"/>）。
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// 扩展链见 <see cref="MarkdownPipelineFactory"/>：**不含 GenericAttributes**，
    /// 否则 <c>{onerror=...}</c> 会绕过 DisableHtml 直接挂到标签上。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = MarkdownPipelineFactory
        .CreateBuilder()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdig.Markdown.Parse(markdown, Pipeline);

        // 链接降级必须在 AST 上做：渲染完再改 HTML 串会破坏转义，也拦不住畸形 HTML
        MarkdownLinkSanitizer.Sanitize(document);

        return Markdig.Markdown.ToHtml(document, Pipeline);
    }

    /// <summary>粗略判断一段文本是不是 Markdown（有标题、列表、代码块这类结构）。</summary>
    public static bool LooksLikeMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("## ", StringComparison.Ordinal)
            || text.Contains("\n- ", StringComparison.Ordinal)
            || text.Contains("\n* ", StringComparison.Ordinal)
            || text.Contains("```", StringComparison.Ordinal)
            || text.Contains("\n1. ", StringComparison.Ordinal);
    }
}
