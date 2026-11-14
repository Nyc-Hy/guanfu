using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Workbench.Documents;

namespace Workbench.Tests;

/// <summary>
/// DOCX 的核心价值是把「删除线」和「修订（补丁）」还原出来。
/// 这里直接生成带这些标记的 docx 来验证解析。
/// </summary>
public class DocxDocumentParserTests
{
    private readonly DocxDocumentParser _parser = new();

    [Fact]
    public async Task 删除线与修订标记被还原()
    {
        using var stream = BuildDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        // ---- 删除线 ----
        var struck = parsed.Blocks
            .SelectMany(b => b.Inlines ?? [])
            .Where(i => i.Strike)
            .ToList();

        Assert.Contains(struck, i => i.Text.Contains("废弃的需求"));

        // ---- 修订（补丁） ----
        Assert.Contains(parsed.Revisions, r =>
            r.Kind == RevisionKindName.Insert &&
            r.Author == "张三" &&
            r.Text.Contains("新增的验收标准"));

        Assert.Contains(parsed.Revisions, r =>
            r.Kind == RevisionKindName.Delete &&
            r.Author == "李四" &&
            r.Text.Contains("删掉的旧标准"));
    }

    [Fact]
    public async Task 删除的修订内容同时呈现为删除线()
    {
        using var stream = BuildDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var struckTexts = parsed.Blocks
            .SelectMany(b => b.Inlines ?? [])
            .Where(i => i.Strike)
            .Select(i => i.Text)
            .ToList();

        Assert.Contains(struckTexts, t => t.Contains("删掉的旧标准"));
    }

    private static MemoryStream BuildDocument()
    {
        var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var paragraph = new Paragraph();
            paragraph.Append(new Run(new Text("正常内容。")));

            // run 级删除线
            paragraph.Append(new Run(
                new RunProperties(new Strike()),
                new Text("废弃的需求")));

            // 插入修订
            var inserted = new InsertedRun { Author = "张三", Date = DateTime.UtcNow };
            inserted.Append(new Run(new Text("新增的验收标准")));
            paragraph.Append(inserted);

            // 删除修订
            var deleted = new DeletedRun { Author = "李四", Date = DateTime.UtcNow };
            deleted.Append(new Run(new Text("删掉的旧标准")));
            paragraph.Append(deleted);

            body.Append(paragraph);
            mainPart.Document.Save();
        }

        stream.Position = 0;
        return stream;
    }

    // ------------------------------------------------------------------
    // 表格：以前用 InnerText 压平，delText 漏进正文、删除线与修订全丢
    // ------------------------------------------------------------------

    [Fact]
    public async Task 表格被还原成结构化行列而不是压平()
    {
        using var stream = BuildTableDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var table = parsed.Blocks.Single(b => b.Kind == BlockKind.Table);
        Assert.Equal(new[] { "字段", "说明" }, table.Headers);
        Assert.Equal(2, table.Rows!.Count);
        Assert.Equal("id", table.Rows[1][0][0].Text);

        var cell = table.Rows[1][1];
        Assert.Contains(cell, i => i is { Text: "旧说明", Strike: true });
        Assert.Contains(cell, i => i is { Text: "新说明", Strike: false });
        Assert.Contains(cell, i => i is { Text: "被删的说明", Strike: true });
    }

    [Fact]
    public async Task 表格内容进纯文本通道()
    {
        using var stream = BuildTableDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        // GetTextAsync 是 agent 的主读入口，以前这里只有「2 列表头, 2 行」
        Assert.DoesNotContain("列表头", parsed.PlainText);
        Assert.Contains("字段\t说明", parsed.PlainText);
        Assert.Contains("id\t~~旧说明~~新说明~~被删的说明~~", parsed.PlainText);
    }

    [Fact]
    public async Task 表格里的修订带作者被记下来()
    {
        using var stream = BuildTableDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        Assert.Contains(parsed.Revisions, r =>
            r.Kind == RevisionKindName.Insert && r.Author == "王五" && r.Text == "新说明");
        Assert.Contains(parsed.Revisions, r =>
            r.Kind == RevisionKindName.Delete && r.Author == "赵六" && r.Text == "被删的说明");
    }

    // ------------------------------------------------------------------
    // 修订：以前用 InnerText，修订内部的 run 级删除线 / 嵌套链接全丢
    // ------------------------------------------------------------------

    [Fact]
    public async Task 修订内部的run级删除线不会丢()
    {
        using var stream = BuildRevisionDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        Assert.Contains(inlines, i => i is { Text: "插入里被划掉的字", Strike: true });
        Assert.Contains(inlines, i => i is { Text: "插入里的正常字", Strike: false });
        Assert.Contains(inlines, i => i is { Text: "移走的字", Strike: true });
        Assert.Contains(inlines, i => i is { Text: "移来的字", Strike: false });

        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.MoveFrom && r.Author == "李四");
        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.MoveTo && r.Author == "李四");
    }

    [Fact]
    public async Task 修订内部嵌套的链接不会丢()
    {
        using var stream = BuildRevisionDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        Assert.Contains(inlines, i => i.Text == "修订里的链接" && i.Link == "https://example.com/ins");

        // Word 实际产出的方向：hyperlink 在外、ins 在内
        Assert.Contains(inlines, i => i.Text == "链接里的修订" && i.Link == "https://example.com/ins");
        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.Insert && r.Text == "链接里的修订");
    }

    [Fact]
    public async Task 删除修订的delText不会漏成正常正文()
    {
        using var stream = BuildRevisionDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        // delText 必须呈现为删除线（废弃语义），不能当成正常内容
        var deleted = parsed.Blocks.SelectMany(b => b.Inlines ?? []).Single(i => i.Text == "被删的字");
        Assert.True(deleted.Strike);

        // 相邻的删除线片段会合并成一个 ~~...~~（这里紧跟着 moveFrom，也是删除线）
        Assert.Contains("~~被删的字", parsed.PlainText);
    }

    // ------------------------------------------------------------------
    // 超链接 / 内容控件
    // ------------------------------------------------------------------

    [Fact]
    public async Task 超链接存的是真实URL而不是关系Id()
    {
        using var stream = BuildLinkAndControlDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        var link = inlines.Single(i => i.Text == "规格文档");
        Assert.Equal("https://example.com/spec", link.Link);
        Assert.True(link.Strike);

        // rId 不能漏到链接字段里
        Assert.DoesNotContain(inlines, i => i.Link is not null && i.Link.StartsWith("rId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 危险协议的超链接被降级()
    {
        using var stream = BuildLinkAndControlDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        Assert.Null(inlines.Single(i => i.Text == "危险链接").Link);
        Assert.Equal("#章节", inlines.Single(i => i.Text == "书签链接").Link);
    }

    [Fact]
    public async Task 内容控件里的段落与run不会被丢掉()
    {
        using var stream = BuildLinkAndControlDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        Assert.Contains(parsed.Blocks, b =>
            b.Inlines is not null && b.Inlines.Any(i => i.Text == "内容控件里的段落"));

        // SdtRun 在段落内部
        Assert.Contains(parsed.Blocks.SelectMany(b => b.Inlines ?? []), i => i.Text == "控件里的run");
    }

    // ------------------------------------------------------------------
    // 样式级删除线 / 编号
    // ------------------------------------------------------------------

    [Fact]
    public async Task 样式里的删除线沿basedOn继承出来()
    {
        using var stream = BuildStyleAndNumberingDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        // DeprecatedChild 自己没写 strike，是从 Deprecated 继承的
        Assert.True(inlines.Single(i => i.Text == "样式级废弃内容").Strike);
        Assert.Contains("~~样式级废弃内容~~", parsed.PlainText);

        // docDefaults 里的加粗也要生效
        Assert.True(inlines.Single(i => i.Text == "样式级废弃内容").Bold);

        // 直接格式能覆盖样式：w:strike w:val="0" 关掉删除线
        Assert.False(inlines.Single(i => i.Text == "取消废弃").Strike);
    }

    [Fact]
    public async Task 编号列表按numbering判断有序还是无序()
    {
        using var stream = BuildStyleAndNumberingDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var lists = parsed.Blocks.Where(b => b.Kind == BlockKind.List).ToList();
        Assert.Equal(2, lists.Count);

        var ordered = lists.Single(b => b.Children!.Any(c => c.Inlines!.Any(i => i.Text == "有序项")));
        Assert.Equal("ordered", ordered.ListKind);
        Assert.True(ordered.Ordered);

        var bullet = lists.Single(b => b.Children!.Any(c => c.Inlines!.Any(i => i.Text == "无序项")));
        Assert.Equal("bullet", bullet.ListKind);
        Assert.False(bullet.Ordered);
    }

    // ------------------------------------------------------------------
    // 正文之外的附属内容：页眉 / 页脚 / 脚注 / 批注
    // ------------------------------------------------------------------

    [Fact]
    public async Task 页眉页脚脚注批注都进正文通道()
    {
        using var stream = BuildAncillaryDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        Assert.Contains("页眉版本号 v3", parsed.PlainText);
        Assert.Contains("页脚机密", parsed.PlainText);
        Assert.Contains("脚注里的约束", parsed.PlainText);
        Assert.Contains("尾注里的补充", parsed.PlainText);
        Assert.Contains("这里要改", parsed.PlainText);

        Assert.Contains(parsed.Blocks, b => b.Kind == BlockKind.Header);
        Assert.Contains(parsed.Blocks, b => b.Kind == BlockKind.Footer);
        Assert.Contains(parsed.Blocks, b => b.Kind == BlockKind.Footnote);
    }

    [Fact]
    public async Task 批注带作者与时间()
    {
        using var stream = BuildAncillaryDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        var comment = parsed.Blocks.Single(b => b.Kind == BlockKind.Comment);
        Assert.Equal("评审人", comment.Author);
        Assert.NotNull(comment.Date);
        Assert.Contains(comment.Inlines!, i => i.Text == "这里要改");
        Assert.Contains("[批注 评审人] 这里要改", parsed.PlainText);
    }

    [Fact]
    public async Task 脚注引用与脚注正文对得上()
    {
        using var stream = BuildAncillaryDocument();

        var parsed = await _parser.ParseAsync(stream, "prd.docx", CancellationToken.None);

        Assert.Contains(parsed.Blocks.SelectMany(b => b.Inlines ?? []), i => i.Text == "[^1]");
        Assert.Contains(parsed.Blocks, b => b.Kind == BlockKind.Footnote &&
            b.Inlines!.Any(i => i.Text == "[^1]: "));

        // 分隔符用的空脚注（id <= 0）不能被当成正文
        Assert.DoesNotContain("[^0]:", parsed.PlainText);
        Assert.DoesNotContain("[^-1]:", parsed.PlainText);
    }

    private static MemoryStream BuildTableDocument() => Build(body =>
    {
        var table = new Table();

        var headerRow = new TableRow();
        headerRow.Append(Cell("字段"), Cell("说明"));
        table.Append(headerRow);

        var dataRow = new TableRow();
        dataRow.Append(Cell("id"));

        var cellParagraph = new Paragraph();
        cellParagraph.Append(new Run(new RunProperties(new Strike()), new Text("旧说明")));

        var inserted = new InsertedRun { Author = "王五", Date = DateTime.UtcNow };
        inserted.Append(new Run(new Text("新说明")));
        cellParagraph.Append(inserted);

        var deleted = new DeletedRun { Author = "赵六", Date = DateTime.UtcNow };
        deleted.Append(new Run(new DeletedText("被删的说明")));
        cellParagraph.Append(deleted);

        dataRow.Append(new TableCell(cellParagraph));
        table.Append(dataRow);
        body.Append(table);
    });

    private static MemoryStream BuildRevisionDocument() => Build(body =>
    {
        var main = body.Ancestors<Document>().First();
        var paragraph = new Paragraph();

        var inserted = new InsertedRun { Author = "张三", Date = DateTime.UtcNow };
        inserted.Append(new Run(new RunProperties(new Strike()), new Text("插入里被划掉的字")));
        inserted.Append(new Run(new Text("插入里的正常字")));

        var relationship = main.MainDocumentPart!.AddHyperlinkRelationship(new Uri("https://example.com/ins"), true);
        var hyperlink = new Hyperlink { Id = relationship.Id };
        hyperlink.Append(new Run(new Text("修订里的链接")));
        inserted.Append(hyperlink);
        paragraph.Append(inserted);

        var deleted = new DeletedRun { Author = "李四", Date = DateTime.UtcNow };
        deleted.Append(new Run(new DeletedText("被删的字")));
        paragraph.Append(deleted);

        var moveFrom = new MoveFromRun { Author = "李四", Date = DateTime.UtcNow };
        moveFrom.Append(new Run(new Text("移走的字")));
        paragraph.Append(moveFrom);

        var moveTo = new MoveToRun { Author = "李四", Date = DateTime.UtcNow };
        moveTo.Append(new Run(new Text("移来的字")));
        paragraph.Append(moveTo);

        body.Append(paragraph);

        // Word 实际产出的嵌套方向：w:hyperlink 在外、w:ins 在内
        var outerLink = new Hyperlink { Id = relationship.Id };
        var innerInserted = new InsertedRun { Author = "张三", Date = DateTime.UtcNow };
        innerInserted.Append(new Run(new Text("链接里的修订")));
        outerLink.Append(innerInserted);
        body.Append(new Paragraph(outerLink));
    });

    private static MemoryStream BuildLinkAndControlDocument() => Build(body =>
    {
        var mainPart = body.Ancestors<Document>().First().MainDocumentPart!;

        var safe = mainPart.AddHyperlinkRelationship(new Uri("https://example.com/spec"), true);
        var unsafeRel = mainPart.AddHyperlinkRelationship(new Uri("javascript:alert(1)"), true);

        var paragraph = new Paragraph();

        var link = new Hyperlink { Id = safe.Id };
        link.Append(new Run(new RunProperties(new Strike()), new Text("规格文档")));
        paragraph.Append(link);

        var bad = new Hyperlink { Id = unsafeRel.Id };
        bad.Append(new Run(new Text("危险链接")));
        paragraph.Append(bad);

        paragraph.Append(new Hyperlink(new Run(new Text("书签链接"))) { Anchor = "章节" });
        body.Append(paragraph);

        // 块级内容控件
        var sdtBlock = new SdtBlock();
        sdtBlock.Append(new SdtContentBlock(new Paragraph(new Run(new Text("内容控件里的段落")))));
        body.Append(sdtBlock);

        // 行内内容控件
        var sdtRun = new SdtRun();
        sdtRun.Append(new SdtContentRun(new Run(new Text("控件里的run"))));
        body.Append(new Paragraph(new Run(new Text("前缀")), sdtRun));
    });

    private static MemoryStream BuildStyleAndNumberingDocument() => Build(body =>
    {
        var mainPart = body.Ancestors<Document>().First().MainDocumentPart!;

        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var deprecated = new Style { Type = StyleValues.Paragraph, StyleId = "Deprecated" };
        deprecated.Append(new StyleName { Val = "Deprecated" });
        deprecated.Append(new StyleRunProperties(new Strike()));

        var derived = new Style { Type = StyleValues.Paragraph, StyleId = "DeprecatedChild" };
        derived.Append(new StyleName { Val = "DeprecatedChild" });
        derived.Append(new BasedOn { Val = "Deprecated" });

        var styles = new Styles();
        styles.Append(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new Bold()))));
        styles.Append(deprecated);
        styles.Append(derived);
        stylesPart.Styles = styles;
        stylesPart.Styles.Save();

        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        var decimalNum = new AbstractNum { AbstractNumberId = 0 };
        decimalNum.Append(new Level(new NumberingFormat { Val = NumberFormatValues.Decimal }) { LevelIndex = 0 });
        var bulletNum = new AbstractNum { AbstractNumberId = 1 };
        bulletNum.Append(new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }) { LevelIndex = 0 });

        var numbering = new Numbering();
        numbering.Append(decimalNum);
        numbering.Append(bulletNum);
        numbering.Append(new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
        numbering.Append(new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 2 });
        numberingPart.Numbering = numbering;
        numberingPart.Numbering.Save();

        body.Append(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "DeprecatedChild" }),
            new Run(new Text("样式级废弃内容"))));

        // 直接格式覆盖样式：显式关掉删除线
        body.Append(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "DeprecatedChild" }),
            new Run(new RunProperties(new Strike { Val = false }), new Text("取消废弃"))));

        body.Append(new Paragraph(
            new ParagraphProperties(new NumberingProperties(
                new NumberingId { Val = 1 }, new NumberingLevelReference { Val = 0 })),
            new Run(new Text("有序项"))));

        body.Append(new Paragraph(
            new ParagraphProperties(new NumberingProperties(
                new NumberingId { Val = 2 }, new NumberingLevelReference { Val = 0 })),
            new Run(new Text("无序项"))));
    });

    private static MemoryStream BuildAncillaryDocument() => Build(body =>
    {
        var mainPart = body.Ancestors<Document>().First().MainDocumentPart!;

        var footnotesPart = mainPart.AddNewPart<FootnotesPart>();
        var footnote = new Footnote { Id = 1 };
        footnote.Append(new Paragraph(new Run(new Text("脚注里的约束"))));
        footnotesPart.Footnotes = new Footnotes(
            new Footnote(new Paragraph(new Run(new SeparatorMark())))
            { Id = -1, Type = FootnoteEndnoteValues.Separator },
            new Footnote(new Paragraph(new Run(new ContinuationSeparatorMark())))
            { Id = 0, Type = FootnoteEndnoteValues.ContinuationSeparator },
            footnote);
        footnotesPart.Footnotes.Save();

        var endnotesPart = mainPart.AddNewPart<EndnotesPart>();
        var endnote = new Endnote { Id = 1 };
        endnote.Append(new Paragraph(new Run(new Text("尾注里的补充"))));
        endnotesPart.Endnotes = new Endnotes(
            new Endnote(new Paragraph(new Run(new SeparatorMark())))
            { Id = -1, Type = FootnoteEndnoteValues.Separator },
            endnote);
        endnotesPart.Endnotes.Save();

        var referenceParagraph = new Paragraph();
        referenceParagraph.Append(new Run(new Text("正文带脚注")));
        referenceParagraph.Append(new Run(new FootnoteReference { Id = 1 }));
        referenceParagraph.Append(new Run(new EndnoteReference { Id = 1 }));
        body.Append(referenceParagraph);

        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph(new Run(new Text("页眉版本号 v3"))));
        headerPart.Header.Save();

        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(new Run(new Text("页脚机密"))));
        footerPart.Footer.Save();

        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) }));

        var commentsPart = mainPart.AddNewPart<WordprocessingCommentsPart>();
        commentsPart.Comments = new Comments(new Comment(new Paragraph(new Run(new Text("这里要改"))))
        {
            Author = "评审人",
            Id = "1",
            Date = DateTime.UtcNow
        });
        commentsPart.Comments.Save();
    });

    /// <summary>建一个最小 docx 包，正文交给 <paramref name="fillBody"/> 填。</summary>
    private static MemoryStream Build(Action<Body> fillBody)
    {
        var stream = new MemoryStream();

        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            fillBody(mainPart.Document.Body!);
            mainPart.Document.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static TableCell Cell(string text) => new(new Paragraph(new Run(new Text(text))));
}
