using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Workbench.Domain;

namespace Workbench.Documents;

/// <summary>
/// Markdown 解析器。用 Markdig 完整 AST，不用正则。
/// 覆盖：标题 / 段落 / 列表（含任务清单）/ 表格 / 代码块 / 引用 / 删除线。
/// </summary>
public sealed class MarkdownDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.Markdown;

    public bool CanParse(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".md" or ".markdown" or ".mdown" or ".mkd";
    }

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return Parse(text);
    }

    public ParsedDocDto Parse(string text)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()   // 含 GFM 表格、删除线、任务列表、脚注等
            .Build();

        var document = Markdig.Markdown.Parse(text, pipeline);
        var blocks = new List<ParsedBlockDto>();
        var revisions = new List<ParsedRevisionDto>();
        var outline = new List<ParsedOutlineDto>();
        var plain = new System.Text.StringBuilder();

        // MarkdownDocument 本身就是 ContainerBlock，直接遍历
        foreach (var block in document)
        {
            var converted = ConvertBlock(block, revisions, plain);
            if (converted is null)
            {
                continue;
            }

            var index = blocks.Count;
            blocks.Add(converted);

            if (converted.Kind == BlockKind.Heading)
            {
                outline.Add(new ParsedOutlineDto(converted.Level, InlineText(converted.Inlines), index));
            }
        }

        var title = outline.Count > 0 ? outline[0].Title : string.Empty;

        return new ParsedDocDto(title, DocFormat.Markdown.ToString(), blocks, revisions, outline, plain.ToString());
    }

    private static ParsedBlockDto? ConvertBlock(
        Block block,
        List<ParsedRevisionDto> revisions,
        System.Text.StringBuilder plain)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var inlines = ConvertInlines(heading.Inline);
                plain.Append(InlineText(inlines)).Append('\n');
                return new ParsedBlockDto(BlockKind.Heading, Level: heading.Level, Inlines: inlines);
            }

            case ParagraphBlock paragraph:
            {
                var inlines = ConvertInlines(paragraph.Inline);
                plain.Append(InlineText(inlines)).Append('\n');
                return new ParsedBlockDto(BlockKind.Paragraph, Inlines: inlines);
            }

            case ListBlock list:
            {
                var items = new List<ParsedBlockDto>();
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    items.Add(ConvertListItem(item, list, revisions, plain));
                }

                var ordered = list.IsOrdered;
                var listKind = items.Any(i => i.Checked.HasValue)
                    ? "task"
                    : ordered ? "ordered" : "bullet";

                return new ParsedBlockDto(BlockKind.List, ListKind: listKind, Ordered: ordered, Children: items);
            }

            case Markdig.Extensions.Tables.Table table:
            {
                var rows = new List<IReadOnlyList<IReadOnlyList<ParsedInlineDto>>>();
                var headers = new List<string>();

                foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                {
                    var cells = new List<IReadOnlyList<ParsedInlineDto>>();
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var text = cell.Count > 0
                            ? InlineText(ConvertInlines((cell[0] as ParagraphBlock)?.Inline))
                            : string.Empty;
                        cells.Add([new ParsedInlineDto(text)]);

                        if (row.IsHeader)
                        {
                            headers.Add(text);
                        }
                    }

                    rows.Add(cells);
                }

                plain.Append(headers.Count).Append(" 列表头, ").Append(rows.Count).Append(" 行\n");
                return new ParsedBlockDto(BlockKind.Table, Headers: headers, Rows: rows);
            }

            case FencedCodeBlock fenced:
            {
                var info = fenced.Info?.ToString() ?? string.Empty;
                var language = info.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var code = fenced.Lines.ToString();
                var isDiff = string.Equals(language, "diff", StringComparison.OrdinalIgnoreCase);
                plain.Append(code).Append('\n');

                if (isDiff)
                {
                    var lines = DiffTextParser.ParseLines(code);
                    foreach (var line in lines.Where(l => l.Kind is DiffTextParser.Added or DiffTextParser.Removed))
                    {
                        revisions.Add(new ParsedRevisionDto(
                            line.Kind == DiffTextParser.Added ? RevisionKindName.Insert : RevisionKindName.Delete,
                            Text: line.Text,
                            Context: "markdown diff 代码块"));
                    }

                    return new ParsedBlockDto(BlockKind.Diff, Language: language, Text: code, IsDiff: true, Lines: lines);
                }

                return new ParsedBlockDto(BlockKind.Code, Language: language, Text: code);
            }

            case CodeBlock code:
            {
                var text = code.Lines.ToString();
                plain.Append(text).Append('\n');
                return new ParsedBlockDto(BlockKind.Code, Text: text);
            }

            case QuoteBlock quote:
            {
                var children = new List<ParsedBlockDto>();
                foreach (var child in quote)
                {
                    var converted = ConvertBlock(child, revisions, plain);
                    if (converted is not null)
                    {
                        children.Add(converted);
                    }
                }

                return new ParsedBlockDto(BlockKind.Quote, Children: children);
            }

            case ThematicBreakBlock:
                return new ParsedBlockDto(BlockKind.Paragraph, Text: "---");

            default:
                return null;
        }
    }

    private static ParsedBlockDto ConvertListItem(
        ListItemBlock item,
        ListBlock list,
        List<ParsedRevisionDto> revisions,
        System.Text.StringBuilder plain)
    {
        bool? isChecked = null;
        var inlines = new List<ParsedInlineDto>();

        if (list.IsOrdered)
        {
            inlines.Add(new ParsedInlineDto($"{list.OrderedStart}{item.Order}. "));
        }

        var children = new List<ParsedBlockDto>();
        var firstParagraph = true;

        foreach (var child in item)
        {
            // 列表项的正文在第一个 ParagraphBlock 里，其余子块是嵌套内容
            if (firstParagraph && child is ParagraphBlock lead)
            {
                firstParagraph = false;
                foreach (var inline in lead.Inline ?? [])
                {
                    if (inline is Markdig.Extensions.TaskLists.TaskList taskList)
                    {
                        isChecked = taskList.Checked;
                        continue;
                    }

                    inlines.AddRange(ConvertInline(inline));
                }

                continue;
            }

            firstParagraph = false;
            var converted = ConvertBlock(child, revisions, plain);
            if (converted is not null)
            {
                children.Add(converted);
            }
        }

        plain.Append(InlineText(inlines)).Append('\n');
        return new ParsedBlockDto(BlockKind.ListItem, Checked: isChecked, Inlines: inlines, Children: children);
    }

    private static IReadOnlyList<ParsedInlineDto> ConvertInlines(ContainerInline? container)
    {
        if (container is null)
        {
            return [];
        }

        var result = new List<ParsedInlineDto>();
        foreach (var inline in container)
        {
            result.AddRange(ConvertInline(inline));
        }

        return result;
    }

    /// <summary>
    /// 取容器内的行内内容。
    /// 注意：EmphasisInline / LinkInline 的唯一子节点常常是 LeafInline（不是容器），
    /// 直接 as ContainerInline 会拿到 null，内容就整段丢了。
    /// </summary>
    private static IReadOnlyList<ParsedInlineDto> ConvertChildInlines(Inline? firstChild)
    {
        if (firstChild is null)
        {
            return [];
        }

        return firstChild is ContainerInline container
            ? ConvertInlines(container)
            : ConvertInline(firstChild).ToList();
    }

    private static IEnumerable<ParsedInlineDto> ConvertInline(Inline? inline)
    {
        switch (inline)
        {
            case null:
                yield break;

            case LiteralInline literal:
                yield return new ParsedInlineDto(literal.Content.ToString() ?? string.Empty);
                break;

            case CodeInline code:
                yield return new ParsedInlineDto(code.Content, Code: true);
                break;

            case LineBreakInline:
                yield return new ParsedInlineDto("\n");
                break;

            case LinkInline link:
            {
                var text = InlineText(ConvertChildInlines(link.FirstChild));
                yield return new ParsedInlineDto(
                    string.IsNullOrEmpty(text) ? link.Url ?? string.Empty : text,
                    Link: link.Url);
                break;
            }

            case AutolinkInline autolink:
                yield return new ParsedInlineDto(autolink.Url, Link: autolink.Url);
                break;

            case EmphasisInline emphasis:
            {
                // Markdig 的 EmphasisExtra 扩展把删除线也表达成 EmphasisInline，DelimiterChar == '~'
                var delimiter = emphasis.DelimiterChar;
                var strike = delimiter == '~';
                var bold = delimiter == '*';
                var italic = delimiter == '_';

                var inner = ConvertChildInlines(emphasis.FirstChild);
                foreach (var item in inner)
                {
                    yield return item with
                    {
                        Strike = item.Strike || strike,
                        Bold = item.Bold || bold,
                        Italic = item.Italic || italic
                    };
                }

                break;
            }

            case ContainerInline container:
            {
                foreach (var item in ConvertInlines(container))
                {
                    yield return item;
                }

                break;
            }

            default:
                yield return new ParsedInlineDto(inline.ToString() ?? string.Empty);
                break;
        }
    }

    internal static string InlineText(IReadOnlyList<ParsedInlineDto>? inlines) =>
        inlines is null ? string.Empty : string.Concat(inlines.Select(i => i.Text));
}
