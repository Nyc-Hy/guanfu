using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Guanfu.Domain;

namespace Guanfu.Documents;

/// <summary>
/// Markdown 解析器。用 Markdig 完整 AST，不用正则。
/// 覆盖：标题 / 段落 / 列表（含任务清单）/ 表格 / 代码块 / 引用 / 脚注 / 删除线。
/// </summary>
public sealed class MarkdownDocumentParser : IDocumentParser
{
    /// <summary>
    /// Markdig pipeline 是只读的，构造一次就够。
    /// 以前每次 Parse 都重建一遍：AdvancedExtensions 里的插件链要注册几十个扩展，
    /// 解析一篇文档的固定开销比解析内容本身还大。
    ///
    /// 扩展链与渲染器共用 <see cref="MarkdownPipelineFactory"/>（不含 GenericAttributes），
    /// 保证「页面看到的」和「agent 读到的」是同一套语法。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = MarkdownPipelineFactory
        .CreateBuilder()
        .Build();

    public DocFormat Format => DocFormat.Markdown;

    /// <summary>
    /// v3：容器 inline 递归全部子节点（混排强调不再丢兄弟）；表格内容进 PlainText
    /// 且保留单元格内删除线；未知 inline 不再把类型名混进正文；脚注正文不再整组丢弃。
    /// </summary>
    public int ParserVersion => 3;

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
            foreach (var converted in ConvertBlocks(block, revisions, plain))
            {
                var index = blocks.Count;
                blocks.Add(converted);

                if (converted.Kind == BlockKind.Heading)
                {
                    outline.Add(new ParsedOutlineDto(converted.Level, InlineText(converted.Inlines), index));
                }
            }
        }

        var title = outline.Count > 0 ? outline[0].Title : string.Empty;

        return new ParsedDocDto(title, DocFormat.Markdown.ToString(), blocks, revisions, outline, plain.ToString());
    }

    /// <summary>
    /// 一个 AST 块可能展开成多个统一模型块（脚注组就是），所以返回序列而不是单个。
    /// </summary>
    private static IEnumerable<ParsedBlockDto> ConvertBlocks(
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
                yield return new ParsedBlockDto(BlockKind.Heading, Level: heading.Level, Inlines: inlines);
                break;
            }

            case ParagraphBlock paragraph:
            {
                var inlines = ConvertInlines(paragraph.Inline);
                plain.Append(SemanticText(inlines)).Append('\n');
                yield return new ParsedBlockDto(BlockKind.Paragraph, Inlines: inlines);
                break;
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

                yield return new ParsedBlockDto(BlockKind.List, ListKind: listKind, Ordered: ordered, Children: items);
                break;
            }

            case Table table:
            {
                var rows = new List<IReadOnlyList<IReadOnlyList<ParsedInlineDto>>>();
                var headers = new List<string>();

                foreach (var row in table.OfType<TableRow>())
                {
                    var cells = new List<IReadOnlyList<ParsedInlineDto>>();
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        // 单元格内走与正文完全相同的 inline 递归：删除线 / 强调 / 链接都要留住
                        var inlines = ConvertCellInlines(cell);
                        cells.Add(inlines);

                        if (row.IsHeader)
                        {
                            headers.Add(InlineText(inlines));
                        }
                    }

                    rows.Add(cells);

                    // 表格内容必须进 PlainText：GetTextAsync 是 agent 的主读入口，
                    // 只写「N 列表头, M 行」等于需求表格对 agent 不可见。
                    plain.Append(string.Join('\t', cells.Select(SemanticText))).Append('\n');
                }

                yield return new ParsedBlockDto(BlockKind.Table, Headers: headers, Rows: rows);
                break;
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

                    yield return new ParsedBlockDto(BlockKind.Diff, Language: language, Text: code, IsDiff: true, Lines: lines);
                    break;
                }

                yield return new ParsedBlockDto(BlockKind.Code, Language: language, Text: code);
                break;
            }

            case CodeBlock code:
            {
                var text = code.Lines.ToString();
                plain.Append(text).Append('\n');
                yield return new ParsedBlockDto(BlockKind.Code, Text: text);
                break;
            }

            case QuoteBlock quote:
            {
                var children = new List<ParsedBlockDto>();
                foreach (var child in quote)
                {
                    children.AddRange(ConvertBlocks(child, revisions, plain));
                }

                yield return new ParsedBlockDto(BlockKind.Quote, Children: children);
                break;
            }

            case FootnoteGroup group:
            {
                // 脚注正文也是文档内容，整组丢掉的话 agent 看不到注释
                foreach (var footnote in group.OfType<Footnote>())
                {
                    // Markdig 的 Label 自带前导 '^'（"^1"），别拼成 "[^^1]"
                    var name = footnote.Label?.TrimStart('^');
                    var label = $"[^{(string.IsNullOrEmpty(name) ? footnote.Order.ToString() : name)}]: ";
                    plain.Append(label);

                    var converted = new List<ParsedBlockDto>();
                    foreach (var child in footnote)
                    {
                        converted.AddRange(ConvertBlocks(child, revisions, plain));
                    }

                    if (converted.Count == 0)
                    {
                        continue;
                    }

                    var first = converted[0];
                    yield return first with
                    {
                        Inlines = first.Inlines is null
                            ? [new ParsedInlineDto(label)]
                            : [new ParsedInlineDto(label), .. first.Inlines]
                    };

                    foreach (var rest in converted.Skip(1))
                    {
                        yield return rest;
                    }
                }

                break;
            }

            case HtmlBlock html:
            {
                // 原始 HTML 块：源码本身就是内容，按字面保留（渲染侧另有 DisableHtml 把关）
                var text = html.Lines.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    plain.Append(text).Append('\n');
                    yield return new ParsedBlockDto(BlockKind.Paragraph, Inlines: [new ParsedInlineDto(text)]);
                }

                break;
            }

            case ThematicBreakBlock:
                yield return new ParsedBlockDto(BlockKind.Paragraph, Text: "---");
                break;

            case ContainerBlock container:
            {
                // 自定义容器 / 定义列表 / figure / footer 等：结构降级，内容一个都不丢
                foreach (var child in container)
                {
                    foreach (var converted in ConvertBlocks(child, revisions, plain))
                    {
                        yield return converted;
                    }
                }

                break;
            }

            case LeafBlock leaf when leaf.Inline is not null:
            {
                var inlines = ConvertInlines(leaf.Inline);
                if (inlines.Count > 0)
                {
                    plain.Append(SemanticText(inlines)).Append('\n');
                    yield return new ParsedBlockDto(BlockKind.Paragraph, Inlines: inlines);
                }

                break;
            }

            default:
                // 空行块、链接引用定义之类：没有正文，不进结果
                break;
        }
    }

    /// <summary>
    /// 表格单元格可能含多个段落 / 列表 / 引用，全部展开成 inline 序列，
    /// 块之间补一个空格，避免文字黏成一坨。
    /// </summary>
    private static IReadOnlyList<ParsedInlineDto> ConvertCellInlines(TableCell cell)
    {
        var result = new List<ParsedInlineDto>();
        foreach (var leaf in cell.Descendants().OfType<LeafBlock>())
        {
            if (leaf.Inline is null)
            {
                continue;
            }

            var converted = ConvertInlines(leaf.Inline);
            if (converted.Count == 0)
            {
                continue;
            }

            if (result.Count > 0)
            {
                result.Add(new ParsedInlineDto(" "));
            }

            result.AddRange(converted);
        }

        return result;
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
        var nested = new List<Block>();
        var firstParagraph = true;

        foreach (var child in item)
        {
            // 列表项的正文在第一个 ParagraphBlock 里，其余子块是嵌套内容
            if (firstParagraph && child is ParagraphBlock lead)
            {
                firstParagraph = false;
                foreach (var inline in lead.Inline ?? [])
                {
                    if (inline is TaskList taskList)
                    {
                        isChecked = taskList.Checked;
                        continue;
                    }

                    inlines.AddRange(ConvertInline(inline).Where(i => i.Text.Length > 0));
                }

                continue;
            }

            firstParagraph = false;
            nested.Add(child);
        }

        // 先写本项正文再写嵌套内容，否则 PlainText 里子列表会跑到父项前面
        plain.Append(SemanticText(inlines)).Append('\n');

        foreach (var child in nested)
        {
            children.AddRange(ConvertBlocks(child, revisions, plain));
        }

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
            AddNonEmpty(result, ConvertInline(inline));
        }

        return result;
    }

    /// <summary>
    /// 空片段不进结果：Markdig 会在表格单元格的强调后面留一个空 LiteralInline，
    /// 它不带任何信息，留着只是噪声（还会让 SemanticText 多包一层 "~~"）。
    /// </summary>
    private static void AddNonEmpty(List<ParsedInlineDto> target, IEnumerable<ParsedInlineDto> items)
    {
        foreach (var item in items)
        {
            if (item.Text.Length > 0)
            {
                target.Add(item);
            }
        }
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
                var url = link.GetDynamicUrl is not null ? link.GetDynamicUrl() ?? link.Url : link.Url;

                // 不放行的链接只留文字：解析结果会被 MCP / 页面直接取用，
                // 危险 scheme 不能出现在 Link 字段里
                var safeUrl = LinkUrlPolicy.IsSafe(url) ? url : null;

                // 链接文字里也可能有强调 / 删除线，必须遍历全部子节点：
                // 只取 FirstChild 会把 "[**a** b](url)" 的 " b" 丢掉
                var children = ConvertInlines(link);
                if (children.Count == 0)
                {
                    yield return new ParsedInlineDto(link.Url ?? string.Empty, Link: safeUrl);
                    break;
                }

                foreach (var child in children)
                {
                    yield return child with { Link = child.Link ?? safeUrl };
                }

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

                // 容器 inline 必须遍历**全部**子节点：
                // `**a *b* c**` 的子节点是 [文字"a ", 斜体"b", 文字" c"]，
                // 只转 FirstChild 会把 "b c" 整段丢掉。
                // 嵌套强调（***x*** 是 斜体(粗体(x))）按「节点本身」递归，内层格式才不会跳过。
                foreach (var item in ConvertInlines(emphasis))
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

            case HtmlInline html:
                // 原始 HTML 的字面内容（渲染侧 DisableHtml 会把它转义，这里保留原文给 agent 读）
                yield return new ParsedInlineDto(html.Tag ?? string.Empty);
                break;

            case HtmlEntityInline entity:
                // &amp; / &#58; 这类实体：取解码后的字符，别把实体源码当正文
                yield return new ParsedInlineDto(
                    entity.Transcoded.ToString() ?? entity.Original.ToString() ?? string.Empty);
                break;

            case FootnoteLink footnote:
                // 脚注引用降级成 Markdown 脚注标记，正文在 FootnoteGroup 里单独展开
                yield return new ParsedInlineDto(footnote.IsBackLink ? string.Empty : $"[^{footnote.Index}]");
                break;

            case MathInline math:
            {
                var delimiter = new string(math.Delimiter, math.DelimiterCount);
                yield return new ParsedInlineDto(delimiter + math.Content.ToString() + delimiter, Code: true);
                break;
            }

            case AbbreviationInline abbreviation:
                yield return new ParsedInlineDto(abbreviation.Abbreviation?.Label ?? string.Empty);
                break;

            case TaskList task:
                yield return new ParsedInlineDto(task.Checked ? "[x] " : "[ ] ");
                break;

            case DelimiterInline delimiter:
                // 没配对成功的分隔符（落单的 * / [ ）：Markdig 自己知道怎么还原字面量
                yield return new ParsedInlineDto(delimiter.ToLiteral());
                break;

            case ContainerInline container:
            {
                foreach (var item in ConvertInlines(container))
                {
                    yield return item;
                }

                break;
            }

            default:
                // 兜底绝不能输出 inline.ToString() —— 那是类型名
                // （"Markdig.Syntax.Inlines.HtmlInline" 曾经就是这么混进正文的）。
                // 认不出来的节点宁可不出内容，也不污染正文。
                yield break;
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
    ///
    /// 相邻同状态片段合并成一段标记：嵌套强调会拆出多个 inline，
    /// 逐个包 "~~" 会得到 "~~x ~~~~y~~~~ z~~" 这种没法读的东西。
    /// </summary>
    internal static string SemanticText(IReadOnlyList<ParsedInlineDto>? inlines)
    {
        if (inlines is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        var index = 0;

        while (index < inlines.Count)
        {
            // 空片段不占位，否则会出现 "~~~~" 这种空删除线
            if (inlines[index].Text.Length == 0)
            {
                index++;
                continue;
            }

            var strike = inlines[index].Strike;
            var start = index;
            while (index < inlines.Count && inlines[index].Strike == strike)
            {
                index++;
            }

            if (strike)
            {
                builder.Append("~~");
            }

            for (var i = start; i < index; i++)
            {
                builder.Append(inlines[i].Text);
            }

            if (strike)
            {
                builder.Append("~~");
            }
        }

        return builder.ToString();
    }
}
