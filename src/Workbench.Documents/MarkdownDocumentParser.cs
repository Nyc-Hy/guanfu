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
    /// <summary>
    /// Markdig pipeline 是只读的，构造一次就够。
    /// 以前每次 Parse 都重建一遍：AdvancedExtensions 里的插件链要注册几十个扩展，
    /// 解析一篇文档的固定开销比解析内容本身还大。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // 含 GFM 表格、删除线、任务列表、脚注等
        .Build();

    public DocFormat Format => DocFormat.Markdown;

    /// <summary>v2：强调按分隔符个数判断（*斜体* / **粗体**），纯文本保留删除线标记。</summary>
    public int ParserVersion => 2;

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
        var document = Markdig.Markdown.Parse(text, Pipeline);
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
                plain.Append(SemanticText(inlines)).Append('\n');
                return new ParsedBlockDto(BlockKind.Heading, Level: heading.Level, Inlines: inlines);
            }

            case ParagraphBlock paragraph:
            {
                var inlines = ConvertInlines(paragraph.Inline);
                plain.Append(SemanticText(inlines)).Append('\n');
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

        plain.Append(SemanticText(inlines)).Append('\n');
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
                var url = link.GetDynamicUrl is not null ? link.GetDynamicUrl() ?? link.Url : link.Url;

                // 不放行的链接只留文字：解析结果会被 MCP / 页面直接取用，
                // 危险 scheme 不能出现在 Link 字段里
                var safeUrl = LinkUrlPolicy.IsSafe(url) ? url : null;

                yield return new ParsedInlineDto(
                    string.IsNullOrEmpty(text) ? link.Url ?? string.Empty : text,
                    Link: safeUrl);
                break;
            }

            case AutolinkInline autolink:
            {
                var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                yield return new ParsedInlineDto(autolink.Url, Link: LinkUrlPolicy.IsSafe(url) ? url : null);
                break;
            }

            case EmphasisInline emphasis:
            {
                // Markdig 的 EmphasisExtra 扩展把删除线也表达成 EmphasisInline（DelimiterChar == '~'）
                var strike = emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2;
                var bold = false;
                var italic = false;

                if (emphasis.DelimiterChar is '*' or '_')
                {
                    // CommonMark：`*` 和 `_` 都能表粗体也能表斜体，靠分隔符个数区分
                    // 1 = 斜体，2 = 粗体，3 = 粗斜体，4 个以上按粗体算
                    switch (emphasis.DelimiterCount)
                    {
                        case 1:
                            italic = true;
                            break;
                        case 2:
                            bold = true;
                            break;
                        case 3:
                            bold = true;
                            italic = true;
                            break;
                        default:
                            bold = true;
                            break;
                    }
                }

                // 子节点可能是嵌套的强调（***x*** 是 斜体(粗体(x))）：
                // 必须按「节点本身」转换，直接展开子节点会把内层的粗体跳过
                var inner = ConvertInline(emphasis.FirstChild).ToList();
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

    /// <summary>
    /// 带语义的纯文本：删除线片段保留 Markdown 删除线标记。
    ///
    /// 纯文本通道（GetTextAsync）是 agent 读文档的主要入口，
    /// 把废弃内容和正常内容直接拼在一起的话，agent 分不出哪段已经作废。
    /// 保留 "~~" 标记后纯文本本身仍是合法 Markdown，语义不丢。
    /// </summary>
    internal static string SemanticText(IReadOnlyList<ParsedInlineDto>? inlines)
    {
        if (inlines is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline.Strike)
            {
                builder.Append("~~").Append(inline.Text).Append("~~");
            }
            else
            {
                builder.Append(inline.Text);
            }
        }

        return builder.ToString();
    }
}
