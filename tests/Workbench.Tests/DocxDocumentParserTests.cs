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
}
