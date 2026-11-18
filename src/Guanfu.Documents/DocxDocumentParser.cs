using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Guanfu.Domain;

namespace Guanfu.Documents;

/// <summary>
/// DOCX 解析器。核心是还原两类语义：
///   1) 删除线 —— run 级 <c>w:strike</c> / <c>w:dstrike</c>、样式级 strike，以及修订里的删除内容；
///   2) 修订（补丁）—— <c>w:ins</c> / <c>w:del</c> / <c>w:moveFrom</c> / <c>w:moveTo</c>，带作者与时间。
///
/// 表格与正文走**同一套 run 级解析**：早先表格用 <c>InnerText</c> 扁平化，
/// 表内 <c>w:delText</c> 会混进正文、删除线丢失、修订不进 Revisions —— 需求表格恰恰是
/// 最容易带修订的地方，不能降级成纯字符串。
/// </summary>
public sealed class DocxDocumentParser : IDocumentParser
{
    private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public DocFormat Format => DocFormat.Docx;

    /// <summary>
    /// v3：表格结构化（行/单元格 + run 级语义）；修订容器递归到 run；超链接解析真实 URL；
    /// 内容控件解包；样式级删除线；编号列表区分有序/无序；页眉页脚脚注批注提取。
    /// </summary>
    public int ParserVersion => 3;

    public bool CanParse(string fileName) =>
        Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            // OpenXml 需要可 seek 的流：先缓冲到内存
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            using var document = WordprocessingDocument.Open(buffer, isEditable: false);
            return ParseDocument(document, fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 损坏 / 不是 docx 的文件：包格式异常（FileFormatException / OpenXmlPackageException）
            // 不能穿到上层，上层只认 IO 异常，接不住就会变成页面 500。
            // 解析器自己兜住：返回空结果 + 明确错误信息。
            return ParseFailure.Create(fileName, DocFormat.Docx, ex, "docx 文件损坏或不是有效的 Word 文档");
        }
    }

    private static ParsedDocDto ParseDocument(WordprocessingDocument document, string fileName)
    {
        var main = document.MainDocumentPart;
        var body = main?.Document?.Body;
        if (main is null || body is null)
        {
            return new ParsedDocDto(
                Path.GetFileNameWithoutExtension(fileName),
                DocFormat.Docx.ToString(), [], [], [], string.Empty);
        }

        var context = new DocxContext(main);
        var blocks = new List<ParsedBlockDto>();
        var revisions = new List<ParsedRevisionDto>();
        var outline = new List<ParsedOutlineDto>();
        var plain = new StringBuilder();

        foreach (var element in UnwrapContentControls(body.ChildElements))
        {
            switch (element)
            {
                case Paragraph paragraph:
                {
                    var block = ConvertParagraph(paragraph, context, revisions, plain);
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
                    blocks.Add(ConvertTable(table, context, revisions, plain));
                    break;
                }
            }
        }

        AppendAncillaryParts(main, context, blocks, revisions, plain);

        var title = outline.Count > 0
            ? outline[0].Title
            : document.PackageProperties.Title ?? Path.GetFileNameWithoutExtension(fileName);

        return new ParsedDocDto(title, DocFormat.Docx.ToString(), blocks, revisions, outline, plain.ToString());
    }

    /// <summary>
    /// 内容控件（<c>w:sdt</c>）是一层壳，正文在 <c>w:sdtContent</c> 里。
    /// 整块丢掉的话，用模板生成的需求文档会大面积缺内容。
    /// </summary>
    private static IEnumerable<OpenXmlElement> UnwrapContentControls(IEnumerable<OpenXmlElement> elements)
    {
        foreach (var element in elements)
        {
            if (element is SdtBlock sdt)
            {
                var content = sdt.GetFirstChild<SdtContentBlock>();
                if (content is not null)
                {
                    foreach (var inner in UnwrapContentControls(content.ChildElements))
                    {
                        yield return inner;
                    }
                }

                continue;
            }

            yield return element;
        }
    }

    private static ParsedBlockDto? ConvertParagraph(
        Paragraph paragraph,
        DocxContext context,
        List<ParsedRevisionDto> revisions,
        StringBuilder plain)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var (styleName, outlineLevel) = context.ResolveStyle(styleId);

        var inlines = new List<ParsedInlineDto>();
        CollectInlines(paragraph, inlines, revisions, context, hyperlink: null, paragraphStyleId: styleId);

        var text = MarkdownDocumentParser.InlineText(inlines);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 纯文本通道保留删除线标记：agent 不能把已废弃的内容当需求读
        plain.Append(MarkdownDocumentParser.SemanticText(inlines)).Append('\n');

        var headingLevel = ResolveHeadingLevel(styleName, outlineLevel);
        if (headingLevel is not null)
        {
            return new ParsedBlockDto(BlockKind.Heading, Level: headingLevel.Value, Inlines: inlines);
        }

        if (IsCodeStyle(styleName))
        {
            return new ParsedBlockDto(BlockKind.Code, Text: text);
        }

        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        if (numbering is not null)
        {
            // numbering.xml 能认出来就按真实格式分：decimal 是有序，bullet 才是无序
            var ordered = context.IsOrderedNumbering(numbering);
            return new ParsedBlockDto(
                BlockKind.List,
                ListKind: ordered ? "ordered" : "bullet",
                Ordered: ordered,
                Children: [new ParsedBlockDto(BlockKind.ListItem, Inlines: inlines)]);
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

    private static bool IsCodeStyle(string? styleName)
    {
        if (string.IsNullOrEmpty(styleName))
        {
            return false;
        }

        var lower = styleName.ToLowerInvariant();
        return lower.Contains("code") || lower.Contains("source") || lower.Contains("html") ||
               lower.Contains("preformatted") || lower.Contains("代码");
    }

    /// <summary>
    /// 递归收集行内内容，同时识别修订容器。
    /// 修订容器（w:ins / w:del / w:moveFrom / w:moveTo）必须继续往下走到 run：
    /// 用 InnerText 会把修订内部的删除线、嵌套修订、超链接全部抹平。
    /// </summary>
    private static void CollectInlines(
        OpenXmlElement element,
        List<ParsedInlineDto> inlines,
        List<ParsedRevisionDto> revisions,
        DocxContext context,
        string? hyperlink,
        string? paragraphStyleId)
    {
        foreach (var child in element.ChildElements)
        {
            CollectInline(child, inlines, revisions, context, hyperlink, paragraphStyleId);
        }
    }

    /// <summary>
    /// 处理**一个**行内级元素：run / 修订容器 / 超链接 / 内容控件 / 未知节点。
    ///
    /// 与 <see cref="CollectInlines"/> 拆开是必要的：CollectInlines 收的是「容器的子元素」，
    /// 而未知 w:hyperlink 的兜底路径手上已经是子元素本身 —— 再套一层 CollectInlines
    /// 就把 w:r 当容器展开到 w:t，run 自己没人处理，修订里的链接文字整段消失。
    /// 正文与修订内部共用这一份分派，rId → URL 也共用 <see cref="DocxContext"/> 那一张关系表。
    /// </summary>
    private static void CollectInline(
        OpenXmlElement child,
        List<ParsedInlineDto> inlines,
        List<ParsedRevisionDto> revisions,
        DocxContext context,
        string? hyperlink,
        string? paragraphStyleId)
    {
        switch (child)
        {
            case InsertedRun inserted:
                CollectRevision(
                    inserted, RevisionKindName.Insert, "w:ins",
                    inserted.Author?.Value, inserted.Date?.Value, strike: false,
                    inlines, revisions, context, hyperlink, paragraphStyleId);
                break;

            case DeletedRun deleted:
                CollectRevision(
                    deleted, RevisionKindName.Delete, "w:del",
                    deleted.Author?.Value, deleted.Date?.Value, strike: true,
                    inlines, revisions, context, hyperlink, paragraphStyleId);
                break;

            case MoveFromRun moveFrom:
                CollectRevision(
                    moveFrom, RevisionKindName.MoveFrom, "w:moveFrom",
                    moveFrom.Author?.Value, moveFrom.Date?.Value, strike: true,
                    inlines, revisions, context, hyperlink, paragraphStyleId);
                break;

            case MoveToRun moveTo:
                CollectRevision(
                    moveTo, RevisionKindName.MoveTo, "w:moveTo",
                    moveTo.Author?.Value, moveTo.Date?.Value, strike: false,
                    inlines, revisions, context, hyperlink, paragraphStyleId);
                break;

            case Hyperlink hyperlinkElement:
                CollectInlines(
                    hyperlinkElement, inlines, revisions, context,
                    context.ResolveHyperlink(hyperlinkElement) ?? hyperlink,
                    paragraphStyleId);
                break;

            case SdtRun sdtRun:
            {
                // 行内内容控件：只取内容，别把 w:sdtPr 里的属性当正文
                var content = sdtRun.GetFirstChild<SdtContentRun>();
                if (content is not null)
                {
                    CollectInlines(content, inlines, revisions, context, hyperlink, paragraphStyleId);
                }

                break;
            }

            case Run run:
            {
                var format = context.ResolveFormat(run.RunProperties, paragraphStyleId);
                var text = RunText(run);
                if (text.Length > 0)
                {
                    inlines.Add(new ParsedInlineDto(
                        text, format.Bold, format.Italic, format.Strike, format.Code, hyperlink));
                }

                break;
            }

            case Paragraph nested:
                // 文本框 / 嵌套段落：段落样式可能不同，按它自己的样式解析
                CollectInlines(
                    nested, inlines, revisions, context, hyperlink,
                    nested.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? paragraphStyleId);
                break;

            case OpenXmlUnknownElement unknown:
                // SDK 的强类型子元素表之外的节点会被读成 OpenXmlUnknownElement，
                // 例如 w:ins 里直接塞 w:hyperlink（Word 之外的产出工具常这么写）。
                // 不兜这一层，整段内容就凭空消失了。
                CollectUnknown(unknown, inlines, revisions, context, hyperlink, paragraphStyleId);
                break;

            default:
                CollectInlines(child, inlines, revisions, context, hyperlink, paragraphStyleId);
                break;
        }
    }

    /// <summary>
    /// 强类型识别不了的节点：按 XML 本地名兜底，至少把文字与链接捞回来。
    /// </summary>
    private static void CollectUnknown(
        OpenXmlElement element,
        List<ParsedInlineDto> inlines,
        List<ParsedRevisionDto> revisions,
        DocxContext context,
        string? hyperlink,
        string? paragraphStyleId)
    {
        switch (element.LocalName)
        {
            case "hyperlink":
            {
                // r:id → 真实 URL 走与正文同一张关系表（DocxContext），
                // 危险 scheme 也一样在那里被降级。
                var id = element.GetAttribute("id", RelationshipNamespace).Value;
                var url = context.ResolveHyperlinkId(id);

                foreach (var child in element.ChildElements)
                {
                    // 手上已经是子元素本身（w:r / w:ins …），按单个行内元素分派，
                    // 不能再套 CollectInlines —— 那会把 w:r 当容器展开，链接文字就丢了。
                    CollectInline(child, inlines, revisions, context, url ?? hyperlink, paragraphStyleId);
                }

                return;
            }

            case "r":
            {
                var text = new StringBuilder();
                var strike = false;

                foreach (var child in element.ChildElements)
                {
                    switch (child.LocalName)
                    {
                        case "t":
                        case "delText":
                            text.Append(child.InnerText);
                            break;

                        case "tab":
                            text.Append('\t');
                            break;

                        case "br":
                        case "cr":
                            text.Append('\n');
                            break;

                        case "rPr":
                            strike = child.ChildElements.Any(p =>
                                p.LocalName is "strike" or "dstrike" &&
                                p.GetAttribute("val", WordprocessingNamespace).Value is not ("0" or "false"));
                            break;
                    }
                }

                if (text.Length > 0)
                {
                    inlines.Add(new ParsedInlineDto(text.ToString(), Strike: strike, Link: hyperlink));
                }

                return;
            }

            default:
                foreach (var child in element.ChildElements)
                {
                    CollectInline(child, inlines, revisions, context, hyperlink, paragraphStyleId);
                }

                return;
        }
    }

    /// <summary>
    /// 修订容器：先递归拿到 run 级 inline（保留删除线 / 粗斜体 / 链接），
    /// 再整体登记一条修订。删除类修订的内容同时呈现为删除线 —— 语义上就是「已废弃」。
    /// </summary>
    private static void CollectRevision(
        OpenXmlElement element,
        string kind,
        string source,
        string? author,
        DateTime? date,
        bool strike,
        List<ParsedInlineDto> inlines,
        List<ParsedRevisionDto> revisions,
        DocxContext context,
        string? hyperlink,
        string? paragraphStyleId)
    {
        var inner = new List<ParsedInlineDto>();
        CollectInlines(element, inner, revisions, context, hyperlink, paragraphStyleId);

        if (strike)
        {
            for (var i = 0; i < inner.Count; i++)
            {
                inner[i] = inner[i] with { Strike = true };
            }
        }

        inlines.AddRange(inner);

        var text = MarkdownDocumentParser.InlineText(inner);
        if (text.Length > 0)
        {
            revisions.Add(new ParsedRevisionDto(kind, author, date, text, source));
        }
    }

    /// <summary>
    /// run 的字面文本。不用 <c>Run.InnerText</c>：它会把 <c>w:tab</c> / <c>w:br</c> 吃掉，
    /// 又把域指令之类的噪声算进来。这里按子元素类型逐个还原。
    /// </summary>
    private static string RunText(Run run)
    {
        var builder = new StringBuilder();
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;

                case DeletedText deleted:
                    // w:delText 只在 w:del 里出现，但 run 级也要认，否则内容凭空消失
                    builder.Append(deleted.Text);
                    break;

                case TabChar:
                    builder.Append('\t');
                    break;

                case Break:
                case CarriageReturn:
                    builder.Append('\n');
                    break;

                case NoBreakHyphen:
                    builder.Append('\u2011');
                    break;

                case SoftHyphen:
                    // 可选连字符：只在换行时显示，正文里不算字符
                    break;

                case FootnoteReference footnote:
                    builder.Append("[^").Append(footnote.Id?.Value).Append(']');
                    break;

                case EndnoteReference endnote:
                    builder.Append("[^").Append(endnote.Id?.Value).Append(']');
                    break;

                default:
                    // RunProperties / FieldCode / ProofError / 域字符：不是正文
                    break;
            }
        }

        return builder.ToString();
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

    /// <summary>
    /// 表格解析成结构化块：行 / 单元格，单元格内段落走与正文相同的 run 级解析。
    /// 单元格文本进 PlainText（制表符分隔），删除线标记一并保留。
    /// </summary>
    private static ParsedBlockDto ConvertTable(
        Table table,
        DocxContext context,
        List<ParsedRevisionDto> revisions,
        StringBuilder plain)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<ParsedInlineDto>>>();
        var headers = new List<string>();
        var first = true;

        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<IReadOnlyList<ParsedInlineDto>>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var inlines = CollectCellInlines(cell, context, revisions);
                cells.Add(inlines);

                if (first)
                {
                    headers.Add(MarkdownDocumentParser.InlineText(inlines));
                }
            }

            rows.Add(cells);
            plain.Append(string.Join('\t', cells.Select(MarkdownDocumentParser.SemanticText))).Append('\n');
            first = false;
        }

        return new ParsedBlockDto(BlockKind.Table, Headers: headers, Rows: rows);
    }

    /// <summary>单元格可能含多个段落、内容控件甚至嵌套表格，全部展开，块之间补空格。</summary>
    private static IReadOnlyList<ParsedInlineDto> CollectCellInlines(
        TableCell cell,
        DocxContext context,
        List<ParsedRevisionDto> revisions)
    {
        var inlines = new List<ParsedInlineDto>();

        foreach (var element in UnwrapContentControls(cell.ChildElements))
        {
            switch (element)
            {
                case Paragraph paragraph:
                {
                    var collected = new List<ParsedInlineDto>();
                    CollectInlines(
                        paragraph, collected, revisions, context, null,
                        paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
                    AppendWithSeparator(inlines, collected);
                    break;
                }

                case Table nested:
                {
                    // 嵌套表格：逐行逐单元格取文本，别整块丢掉
                    foreach (var row in nested.Elements<TableRow>())
                    {
                        foreach (var nestedCell in row.Elements<TableCell>())
                        {
                            AppendWithSeparator(inlines, CollectCellInlines(nestedCell, context, revisions));
                        }
                    }

                    break;
                }
            }
        }

        return inlines;
    }

    private static void AppendWithSeparator(List<ParsedInlineDto> target, IReadOnlyList<ParsedInlineDto> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (target.Count > 0)
        {
            target.Add(new ParsedInlineDto(" "));
        }

        target.AddRange(items);
    }

    /// <summary>
    /// 页眉 / 页脚 / 脚注 / 尾注 / 批注。它们不在 body 里，但都是文档内容：
    /// 页眉常放版本号与密级，脚注常放约束，批注是评审意见（必须带作者）。
    /// </summary>
    private static void AppendAncillaryParts(
        MainDocumentPart main,
        DocxContext context,
        List<ParsedBlockDto> blocks,
        List<ParsedRevisionDto> revisions,
        StringBuilder plain)
    {
        foreach (var part in main.HeaderParts)
        {
            AppendPartContainer(part.Header, BlockKind.Header, "[页眉]", context, blocks, revisions, plain);
        }

        foreach (var part in main.FooterParts)
        {
            AppendPartContainer(part.Footer, BlockKind.Footer, "[页脚]", context, blocks, revisions, plain);
        }

        AppendNotes(main.FootnotesPart?.Footnotes?.Elements<Footnote>(), context, blocks, revisions, plain);
        AppendNotes(main.EndnotesPart?.Endnotes?.Elements<Endnote>(), context, blocks, revisions, plain);

        foreach (var comment in main.WordprocessingCommentsPart?.Comments?.Elements<Comment>() ?? [])
        {
            var inlines = new List<ParsedInlineDto>();
            foreach (var paragraph in comment.Elements<Paragraph>())
            {
                CollectInlines(
                    paragraph, inlines, revisions, context, null,
                    paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
            }

            if (MarkdownDocumentParser.InlineText(inlines).Length == 0)
            {
                continue;
            }

            var author = comment.Author?.Value;
            plain.Append("[批注");
            if (!string.IsNullOrEmpty(author))
            {
                plain.Append(' ').Append(author);
            }

            plain.Append("] ").Append(MarkdownDocumentParser.SemanticText(inlines)).Append('\n');

            blocks.Add(new ParsedBlockDto(
                BlockKind.Comment,
                Inlines: inlines,
                Author: author,
                Date: comment.Date?.Value));
        }
    }

    private static void AppendPartContainer(
        OpenXmlElement? root,
        string kind,
        string marker,
        DocxContext context,
        List<ParsedBlockDto> blocks,
        List<ParsedRevisionDto> revisions,
        StringBuilder plain)
    {
        if (root is null)
        {
            return;
        }

        var children = new List<ParsedBlockDto>();
        var local = new StringBuilder();

        foreach (var element in UnwrapContentControls(root.ChildElements))
        {
            switch (element)
            {
                case Paragraph paragraph:
                {
                    var block = ConvertParagraph(paragraph, context, revisions, local);
                    if (block is not null)
                    {
                        children.Add(block);
                    }

                    break;
                }

                case Table table:
                    children.Add(ConvertTable(table, context, revisions, local));
                    break;
            }
        }

        if (children.Count == 0)
        {
            return;
        }

        plain.Append(marker).Append('\n').Append(local);
        blocks.Add(new ParsedBlockDto(kind, Children: children));
    }

    private static void AppendNotes(
        IEnumerable<FootnoteEndnoteType>? notes,
        DocxContext context,
        List<ParsedBlockDto> blocks,
        List<ParsedRevisionDto> revisions,
        StringBuilder plain)
    {
        if (notes is null)
        {
            return;
        }

        foreach (var note in notes)
        {
            var id = note.Id?.Value;

            // 分隔符 / 续接符不是正文：Word 用 -1 / 0 占位，Type 也会标出来
            if (id is null || id <= 0)
            {
                continue;
            }

            var type = note.Type?.Value;
            if (type == FootnoteEndnoteValues.Separator ||
                type == FootnoteEndnoteValues.ContinuationSeparator ||
                type == FootnoteEndnoteValues.ContinuationNotice)
            {
                continue;
            }

            var inlines = new List<ParsedInlineDto> { new($"[^{id}]: ") };
            foreach (var paragraph in note.Elements<Paragraph>())
            {
                CollectInlines(
                    paragraph, inlines, revisions, context, null,
                    paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
            }

            if (inlines.Count <= 1)
            {
                continue;
            }

            plain.Append(MarkdownDocumentParser.SemanticText(inlines)).Append('\n');
            blocks.Add(new ParsedBlockDto(BlockKind.Footnote, Inlines: inlines));
        }
    }

    /// <summary>
    /// 一次解析里共享的包级信息：样式表（含继承链与样式级删除线）、编号定义、
    /// 超链接关系（rId → 真实 URL）。
    /// </summary>
    private sealed class DocxContext
    {
        private readonly Dictionary<string, Style> _styles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hyperlinks = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _numberToAbstract = new();
        private readonly Dictionary<(int Abstract, int Level), Level> _levels = new();
        private readonly OpenXmlElement? _defaultRunProperties;

        public DocxContext(MainDocumentPart main)
        {
            var styles = main.StyleDefinitionsPart?.Styles;
            if (styles is not null)
            {
                foreach (var style in styles.Elements<Style>())
                {
                    if (style.StyleId?.Value is string id && id.Length > 0)
                    {
                        _styles[id] = style;
                    }
                }

                // docDefaults 里的 w:rPr 在 SDK 里是 RunPropertiesBaseStyle，不是 RunProperties
                _defaultRunProperties = styles.DocDefaults?.RunPropertiesDefault?.GetFirstChild<RunPropertiesBaseStyle>();
            }

            // w:hyperlink r:id 只是关系 id，真实 URL 在包的 relationships 里
            foreach (var relationship in main.HyperlinkRelationships)
            {
                if (relationship.Id is string relationId && relationship.Uri is not null)
                {
                    _hyperlinks[relationId] = relationship.Uri.ToString();
                }
            }

            var numbering = main.NumberingDefinitionsPart?.Numbering;
            if (numbering is not null)
            {
                foreach (var instance in numbering.Elements<NumberingInstance>())
                {
                    if (instance.NumberID?.Value is int numberId && instance.AbstractNumId?.Val?.Value is int abstractId)
                    {
                        _numberToAbstract[numberId] = abstractId;
                    }
                }

                foreach (var abstractNumbering in numbering.Elements<AbstractNum>())
                {
                    if (abstractNumbering.AbstractNumberId?.Value is not int abstractId)
                    {
                        continue;
                    }

                    foreach (var level in abstractNumbering.Elements<Level>())
                    {
                        if (level.LevelIndex?.Value is int levelIndex)
                        {
                            _levels[(abstractId, levelIndex)] = level;
                        }
                    }
                }
            }
        }

        /// <summary>样式 id → 名称 / 大纲级别。不同模板的标题样式 id 差异很大，必须走样式表解析。</summary>
        public (string Name, int? OutlineLevel) ResolveStyle(string? styleId)
        {
            var name = string.Empty;
            int? outlineLevel = null;

            // 派生样式在前、基样式在后：派生样式没写的属性才回落到基样式
            foreach (var style in StyleChain(styleId))
            {
                if (name.Length == 0)
                {
                    name = style.StyleName?.Val?.Value ?? string.Empty;
                }

                outlineLevel ??= style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            }

            return (name, outlineLevel);
        }

        /// <summary>
        /// run 的有效格式。Word 的叠加顺序：docDefaults → 段落样式链 → 字符样式链 → 直接格式，
        /// 后者覆盖前者。样式级 strike 就是这么来的（模板里把「废弃」定义成带删除线的字符样式）。
        /// </summary>
        public (bool Strike, bool Bold, bool Italic, bool Code) ResolveFormat(
            RunProperties? direct,
            string? paragraphStyleId)
        {
            var strike = false;
            var bold = false;
            var italic = false;

            Apply(_defaultRunProperties, ref strike, ref bold, ref italic);

            foreach (var style in StyleChain(paragraphStyleId))
            {
                Apply(style.StyleRunProperties, ref strike, ref bold, ref italic);
            }

            var runStyleId = direct?.RunStyle?.Val?.Value;
            foreach (var style in StyleChain(runStyleId))
            {
                Apply(style.StyleRunProperties, ref strike, ref bold, ref italic);
            }

            Apply(direct, ref strike, ref bold, ref italic);

            var code = IsCodeStyle(runStyleId)
                       || IsCodeStyle(NameOf(runStyleId))
                       || IsCodeStyle(NameOf(paragraphStyleId));

            return (strike, bold, italic, code);
        }

        /// <summary>rId → 真实 URL；内部书签转成页内锚点。危险 scheme 与 Markdown 侧一样不放行。</summary>
        public string? ResolveHyperlink(Hyperlink hyperlink)
        {
            var url = ResolveHyperlinkId(hyperlink.Id?.Value);
            if (url is not null)
            {
                return url;
            }

            var anchor = hyperlink.Anchor?.Value;
            return string.IsNullOrEmpty(anchor) ? null : "#" + anchor;
        }

        /// <summary>只按关系 id 查 URL，供强类型识别不了的 w:hyperlink 兜底用。</summary>
        public string? ResolveHyperlinkId(string? id)
        {
            if (id is not null &&
                _hyperlinks.TryGetValue(id, out var url) &&
                url.Length > 0)
            {
                return LinkUrlPolicy.IsSafe(url) ? url : null;
            }

            return null;
        }

        /// <summary>编号是有序还是无序：认不出 numbering.xml 就退回无序（保持老行为）。</summary>
        public bool IsOrderedNumbering(NumberingProperties numbering)
        {
            if (numbering.NumberingId?.Val?.Value is not int numberId)
            {
                return false;
            }

            var levelIndex = numbering.NumberingLevelReference?.Val?.Value ?? 0;

            if (!_numberToAbstract.TryGetValue(numberId, out var abstractId) ||
                !_levels.TryGetValue((abstractId, levelIndex), out var level))
            {
                return false;
            }

            var format = level.NumberingFormat?.Val?.Value;
            return format is not null &&
                   format.Value != NumberFormatValues.Bullet &&
                   format.Value != NumberFormatValues.None;
        }

        private string? NameOf(string? styleId) =>
            styleId is not null && _styles.TryGetValue(styleId, out var style)
                ? style.StyleName?.Val?.Value
                : null;

        /// <summary>沿 w:basedOn 往上取整条样式链，返回顺序是「基样式 → 派生样式」。</summary>
        private IEnumerable<Style> StyleChain(string? styleId)
        {
            var chain = new List<Style>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = styleId;

            while (current is not null && seen.Add(current) && _styles.TryGetValue(current, out var style))
            {
                chain.Add(style);
                current = style.BasedOn?.Val?.Value;
            }

            chain.Reverse();
            return chain;
        }

        private static void Apply(OpenXmlElement? properties, ref bool strike, ref bool bold, ref bool italic)
        {
            if (properties is null)
            {
                return;
            }

            var single = properties.GetFirstChild<Strike>();
            var doubled = properties.GetFirstChild<DoubleStrike>();
            if (single is not null || doubled is not null)
            {
                strike = IsOn(single) || IsOn(doubled);
            }

            if (properties.GetFirstChild<Bold>() is { } boldValue)
            {
                bold = IsOn(boldValue);
            }

            if (properties.GetFirstChild<Italic>() is { } italicValue)
            {
                italic = IsOn(italicValue);
            }
        }
    }
}
