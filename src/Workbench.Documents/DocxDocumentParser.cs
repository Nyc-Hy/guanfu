using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Workbench.Domain;

namespace Workbench.Documents;

/// <summary>
/// DOCX 解析器。核心是还原两类语义：
///   1) 删除线 —— run 级 <c>w:strike</c> / <c>w:dstrike</c>，以及修订里的删除内容；
///   2) 修订（补丁）—— <c>w:ins</c> / <c>w:del</c> / <c>w:moveFrom</c> / <c>w:moveTo</c>，带作者与时间。
/// </summary>
public sealed class DocxDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.Docx;

    public bool CanParse(string fileName) =>
        Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        // OpenXml 需要可 seek 的流：先缓冲到内存
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        using var document = WordprocessingDocument.Open(buffer, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return new ParsedDocDto(
                Path.GetFileNameWithoutExtension(fileName),
                DocFormat.Docx.ToString(), [], [], [], string.Empty);
        }

        var styleMap = BuildStyleMap(document);
        var blocks = new List<ParsedBlockDto>();
        var revisions = new List<ParsedRevisionDto>();
        var outline = new List<ParsedOutlineDto>();
        var plain = new System.Text.StringBuilder();

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                {
                    var block = ConvertParagraph(paragraph, styleMap, revisions, plain);
                    if (block is null)
                    {
                        break;
                    }

                    var index = blocks.Count;
                    blocks.Add(block);
                    if (block.Kind == BlockKind.Heading)
                    {
                        outline.Add(new ParsedOutlineDto(
                            block.Level,
                            MarkdownDocumentParser.InlineText(block.Inlines),
                            index));
                    }

                    break;
                }

                case Table table:
                {
                    blocks.Add(ConvertTable(table, plain));
                    break;
                }
            }
        }

        var title = outline.Count > 0
            ? outline[0].Title
            : document.PackageProperties.Title ?? Path.GetFileNameWithoutExtension(fileName);

        return new ParsedDocDto(title, DocFormat.Docx.ToString(), blocks, revisions, outline, plain.ToString());
    }

    /// <summary>样式 id → 名称 / 大纲级别。不同模板的标题样式 id 差异很大，必须走样式表解析。</summary>
    private static Dictionary<string, (string Name, int? OutlineLevel)> BuildStyleMap(WordprocessingDocument document)
    {
        var map = new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);
        var styles = document.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return map;
        }

        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var name = style.StyleName?.Val?.Value ?? string.Empty;
            var outlineLevel = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            map[id] = (name, outlineLevel);
        }

        return map;
    }

    private static ParsedBlockDto? ConvertParagraph(
        Paragraph paragraph,
        Dictionary<string, (string Name, int? OutlineLevel)> styleMap,
        List<ParsedRevisionDto> revisions,
        System.Text.StringBuilder plain)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var styleName = styleId is not null && styleMap.TryGetValue(styleId, out var info) ? info.Name : string.Empty;
        var outlineLevel = styleId is not null && styleMap.TryGetValue(styleId, out var info2) ? info2.OutlineLevel : null;

        var inlines = new List<ParsedInlineDto>();
        CollectInlines(paragraph, inlines, revisions, null);

        var text = MarkdownDocumentParser.InlineText(inlines);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        plain.Append(text).Append('\n');

        var headingLevel = ResolveHeadingLevel(styleName, outlineLevel);
        if (headingLevel is not null)
        {
            return new ParsedBlockDto(BlockKind.Heading, Level: headingLevel.Value, Inlines: inlines);
        }

        if (IsCodeStyle(styleName))
        {
            return new ParsedBlockDto(BlockKind.Code, Text: text);
        }

        if (paragraph.ParagraphProperties?.NumberingProperties is not null)
        {
            return new ParsedBlockDto(BlockKind.List, ListKind: "bullet", Children:
            [
                new ParsedBlockDto(BlockKind.ListItem, Inlines: inlines)
            ]);
        }

        return new ParsedBlockDto(BlockKind.Paragraph, Inlines: inlines);
    }

    private static int? ResolveHeadingLevel(string styleName, int? outlineLevel)
    {
        if (outlineLevel is not null && outlineLevel >= 0 && outlineLevel <= 8)
        {
            return outlineLevel.Value + 1;
        }

        if (string.IsNullOrEmpty(styleName))
        {
            return null;
        }

        var lower = styleName.ToLowerInvariant();
        if (lower.Contains("heading") || lower.Contains("标题"))
        {
            var digits = new string(lower.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out var level))
            {
                return Math.Clamp(level, 1, 6);
            }

            return 1;
        }

        return null;
    }

    private static bool IsCodeStyle(string styleName)
    {
        if (string.IsNullOrEmpty(styleName))
        {
            return false;
        }

        var lower = styleName.ToLowerInvariant();
        return lower.Contains("code") || lower.Contains("source") || lower.Contains("html") ||
               lower.Contains("preformatted") || lower.Contains("代码");
    }

    /// <summary>递归收集行内内容，同时识别修订容器。</summary>
    private static void CollectInlines(
        OpenXmlElement element,
        List<ParsedInlineDto> inlines,
        List<ParsedRevisionDto> revisions,
        string? hyperlink)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case InsertedRun inserted:
                {
                    var text = inserted.InnerText;
                    if (!string.IsNullOrEmpty(text))
                    {
                        inlines.Add(new ParsedInlineDto(text));
                        revisions.Add(new ParsedRevisionDto(
                            RevisionKindName.Insert,
                            inserted.Author?.Value,
                            inserted.Date?.Value,
                            text,
                            "w:ins"));
                    }

                    break;
                }

                case DeletedRun deleted:
                {
                    var text = deleted.InnerText;
                    if (!string.IsNullOrEmpty(text))
                    {
                        // 删除的内容同时呈现为删除线：语义上就是「已废弃」
                        inlines.Add(new ParsedInlineDto(text, Strike: true));
                        revisions.Add(new ParsedRevisionDto(
                            RevisionKindName.Delete,
                            deleted.Author?.Value,
                            deleted.Date?.Value,
                            text,
                            "w:del"));
                    }

                    break;
                }

                case MoveFromRun moveFrom:
                {
                    var text = moveFrom.InnerText;
                    if (!string.IsNullOrEmpty(text))
                    {
                        inlines.Add(new ParsedInlineDto(text, Strike: true));
                        revisions.Add(new ParsedRevisionDto(
                            RevisionKindName.MoveFrom,
                            moveFrom.Author?.Value,
                            moveFrom.Date?.Value,
                            text,
                            "w:moveFrom"));
                    }

                    break;
                }

                case MoveToRun moveTo:
                {
                    var text = moveTo.InnerText;
                    if (!string.IsNullOrEmpty(text))
                    {
                        inlines.Add(new ParsedInlineDto(text));
                        revisions.Add(new ParsedRevisionDto(
                            RevisionKindName.MoveTo,
                            moveTo.Author?.Value,
                            moveTo.Date?.Value,
                            text,
                            "w:moveTo"));
                    }

                    break;
                }

                case Hyperlink hyperlinkElement:
                {
                    CollectInlines(hyperlinkElement, inlines, revisions, hyperlinkElement.Id?.Value ?? hyperlink);
                    break;
                }

                case Run run:
                {
                    var props = run.RunProperties;
                    var strike = props?.Strike is not null && IsOn(props.Strike) ||
                                 props?.DoubleStrike is not null && IsOn(props.DoubleStrike);
                    var bold = props?.Bold is not null && IsOn(props.Bold);
                    var italic = props?.Italic is not null && IsOn(props.Italic);
                    var code = IsCodeStyle(props?.RunStyle?.Val?.Value ?? string.Empty);

                    var text = run.InnerText;
                    if (!string.IsNullOrEmpty(text))
                    {
                        inlines.Add(new ParsedInlineDto(text, bold, italic, strike, code, hyperlink));
                    }

                    break;
                }

                default:
                    CollectInlines(child, inlines, revisions, hyperlink);
                    break;
            }
        }
    }

    private static bool IsOn(OnOffType? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value.Val is null)
        {
            return true;   // <w:strike/> 等价于 true
        }

        return value.Val.Value;
    }

    private static ParsedBlockDto ConvertTable(Table table, System.Text.StringBuilder plain)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<ParsedInlineDto>>>();
        var headers = new List<string>();
        var first = true;

        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<IReadOnlyList<ParsedInlineDto>>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var text = cell.InnerText.Trim();
                cells.Add([new ParsedInlineDto(text)]);
                if (first)
                {
                    headers.Add(text);
                }
            }

            rows.Add(cells);
            first = false;
        }

        plain.Append(headers.Count).Append(" 列表头, ").Append(rows.Count).Append(" 行\n");

        return new ParsedBlockDto(BlockKind.Table, Headers: headers, Rows: rows);
    }
}
