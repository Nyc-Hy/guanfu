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
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        return Markdig.Markdown.ToHtml(markdown, Pipeline);
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
