using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Workbench.Documents;

/// <summary>
/// Markdown AST 级的链接消毒：把危险链接降级成纯文字。
///
/// 为什么在 AST 上做：渲染之后再用正则改 HTML 串会破坏已经转义过的内容，
/// 而且畸形 HTML 能绕过正则。走 AST 则每个链接节点都看得见，
/// 不放行的链接直接换成没有链接壳的纯文字。
/// </summary>
internal static class MarkdownLinkSanitizer
{
    /// <summary>遍历整篇文档，把不放行的链接 / 图片 / 自动链接降级成纯文字。</summary>
    public static void Sanitize(MarkdownDocument document)
    {
        foreach (var block in document)
        {
            SanitizeBlock(block);
        }
    }

    private static void SanitizeBlock(MarkdownObject block)
    {
        switch (block)
        {
            case ContainerBlock container:
                foreach (var child in container)
                {
                    SanitizeBlock(child);
                }

                break;

            case LeafBlock leaf when leaf.Inline is not null:
                SanitizeInlines(leaf.Inline);
                break;
        }
    }

    private static void SanitizeInlines(ContainerInline container)
    {
        // 先把子节点取出来再改：替换节点会动容器内部的链表
        foreach (var inline in container.ToList())
        {
            switch (inline)
            {
                case LinkInline link:
                {
                    var url = link.GetDynamicUrl is not null ? link.GetDynamicUrl() ?? link.Url : link.Url;
                    if (!LinkUrlPolicy.IsSafe(url))
                    {
                        // 降级：留文字不留链接。换成空壳容器，子节点（文字与内嵌格式）原样搬过去
                        link.ReplaceBy(new ContainerInline(), copyChildren: true);
                        continue;
                    }

                    SanitizeInlines(link);
                    break;
                }

                case AutolinkInline autolink:
                {
                    var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                    if (!LinkUrlPolicy.IsSafe(url))
                    {
                        autolink.ReplaceBy(
                            new LiteralInline { Content = new Markdig.Helpers.StringSlice(autolink.Url) },
                            copyChildren: false);
                    }

                    break;
                }

                case ContainerInline child:
                    SanitizeInlines(child);
                    break;
            }
        }
    }
}
