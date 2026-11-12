using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Documents;
using Workbench.Domain;

namespace Workbench.Application.Services;

public sealed record StoredFile(string RelativePath, long SizeBytes);

/// <summary>文档原件的落盘位置。相对路径相对 WORKBENCH_HOME，换机可整体搬迁。</summary>
public interface IDocumentStore
{
    Task<StoredFile> SaveAsync(Guid taskId, string fileName, Stream content, CancellationToken cancellationToken = default);
    string ResolveAbsolutePath(string relativePath);
    bool Exists(string relativePath);
}

public interface IDocumentService
{
    Task<DocumentDto> UploadAsync(
        Guid taskId,
        string fileName,
        Stream content,
        string? kind = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<DocumentDto?> GetAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<ParsedDocDto> ParseAsync(Guid documentId, bool force = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ParsedOutlineDto>> GetOutlineAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ParsedRevisionDto>> GetRevisionsAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>列出文档里所有删除线内容——「这份文档里被划掉的东西」。</summary>
    Task<IReadOnlyList<StruckContentDto>> ListStruckAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHitDto>> SearchAsync(Guid documentId, string query, CancellationToken cancellationToken = default);

    Task<string> GetTextAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ParserCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

public sealed record StruckContentDto(string Text, string Context);

public sealed record SearchHitDto(int BlockIndex, string Kind, string Text);

public sealed class DocumentService(
    IAppDbContext db,
    IDocumentParserRegistry registry,
    IDocumentStore store,
    IClock clock) : IDocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = WorkbenchJson.Options;

    /// <summary>
    /// 允许上传的扩展名。
    ///
    /// 以前什么都能传 —— 上传是外部输入口，agent 传什么都照收，
    /// 白名单一收紧，存一堆没人能解析的东西就进不来了。
    ///
    /// 覆盖两类：能解析的文档（md/docx/pdf/txt/diff/patch/json/html/csv）
    /// 与需要留档但没法解析的二进制（图片、压缩包）。
    /// </summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文档
        ".md", ".markdown", ".mdown", ".mkd",
        ".docx",
        ".pdf",
        ".txt", ".log", ".text",
        ".diff", ".patch",
        ".json",
        ".html", ".htm",
        ".csv",
        // 图片（截图类产物）
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        // 其它留档
        ".zip"
    };

    /// <summary>扩展名是否在白名单里。没扩展名的文件一律拒 —— 判不出来的东西不该进来。</summary>
    internal static bool IsExtensionAllowed(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && AllowedExtensions.Contains(extension);
    }

    private static void EnsureExtensionAllowed(string fileName)
    {
        if (IsExtensionAllowed(fileName))
        {
            return;
        }

        var extension = Path.GetExtension(fileName);
        var shown = string.IsNullOrEmpty(extension) ? "(无扩展名)" : extension;

        throw new WorkbenchException(
            $"不支持的文件类型: {shown}。允许的类型: {string.Join("、", AllowedExtensions.OrderBy(e => e))}");
    }

    public async Task<DocumentDto> UploadAsync(
        Guid taskId,
        string fileName,
        Stream content,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var taskExists = await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!taskExists)
        {
            throw new NotFoundException("任务", taskId);
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new WorkbenchException("文件名不能为空。");
        }

        EnsureExtensionAllowed(safeName);

        var stored = await store.SaveAsync(taskId, safeName, content, cancellationToken);
        var format = DetectFormat(safeName);

        var document = new Document
        {
            TaskId = taskId,
            FileName = safeName,
            StoredPath = stored.RelativePath,
            SizeBytes = stored.SizeBytes,
            ContentType = GuessContentType(safeName),
            Format = format,
            Kind = kind,
            UploadedAt = clock.UtcNow
        };

        db.Documents.Add(document);
        await db.SaveChangesAsync(cancellationToken);

        return TaskService.ToDocumentDto(document);
    }

    public async Task<IReadOnlyList<DocumentDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var documents = await db.Documents
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .OrderBy(d => d.UploadedAt)
            .ToListAsync(cancellationToken);
        return documents.Select(TaskService.ToDocumentDto).ToList();
    }

    public async Task<DocumentDto?> GetAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        return document is null ? null : TaskService.ToDocumentDto(document);
    }

    public async Task<ParsedDocDto> ParseAsync(
        Guid documentId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken)
                       ?? throw new NotFoundException("文档", documentId);

        var parser = registry.Resolve(document.FileName);

        if (!force)
        {
            // 缓存按 (文档, 解析器版本) 命中：解析语义一改，版本号一涨，旧缓存自然失效
            var cached = await db.DocumentParses
                .AsNoTracking()
                .Where(p => p.DocumentId == documentId && p.ParserVersion == parser.ParserVersion)
                .OrderByDescending(p => p.ParsedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (cached is not null)
            {
                var deserialized = JsonSerializer.Deserialize<ParsedDocDto>(cached.Json, JsonOptions);
                if (deserialized is not null)
                {
                    return deserialized;
                }
            }
        }

        var absolutePath = store.ResolveAbsolutePath(document.StoredPath);

        ParsedDocDto parsed;
        string? error = null;
        try
        {
            await using var stream = File.OpenRead(absolutePath);
            parsed = await parser.ParseAsync(stream, document.FileName, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = ex.Message;
            parsed = new ParsedDocDto(
                document.FileName,
                document.Format.ToString(),
                [],
                [],
                [],
                string.Empty);
        }

        db.DocumentParses.Add(new DocumentParse
        {
            DocumentId = documentId,
            Format = document.Format,
            ParserVersion = parser.ParserVersion,
            Json = JsonSerializer.Serialize(parsed, JsonOptions),
            ParsedAt = clock.UtcNow
        });

        // 解析器自己兜住的失败（损坏的 docx / pdf）通过 parsed.Error 报上来
        document.Parsed = error is null && parsed.Error is null;
        document.ParseError = error ?? parsed.Error;
        await db.SaveChangesAsync(cancellationToken);

        return parsed;
    }

    public async Task<IReadOnlyList<ParsedOutlineDto>> GetOutlineAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var parsed = await ParseAsync(documentId, false, cancellationToken);
        return parsed.Outline;
    }

    public async Task<IReadOnlyList<ParsedRevisionDto>> GetRevisionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var parsed = await ParseAsync(documentId, false, cancellationToken);
        return parsed.Revisions;
    }

    public async Task<IReadOnlyList<StruckContentDto>> ListStruckAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var parsed = await ParseAsync(documentId, false, cancellationToken);
        var results = new List<StruckContentDto>();

        void Walk(IReadOnlyList<ParsedBlockDto> blocks)
        {
            foreach (var block in blocks)
            {
                foreach (var inline in block.Inlines ?? [])
                {
                    if (inline.Strike && !string.IsNullOrWhiteSpace(inline.Text))
                    {
                        results.Add(new StruckContentDto(inline.Text, block.Kind));
                    }
                }

                if (block.Children is not null)
                {
                    Walk(block.Children);
                }
            }
        }

        Walk(parsed.Blocks);
        return results;
    }

    public async Task<IReadOnlyList<SearchHitDto>> SearchAsync(
        Guid documentId,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var parsed = await ParseAsync(documentId, false, cancellationToken);
        var hits = new List<SearchHitDto>();
        var keyword = query.Trim();

        for (var i = 0; i < parsed.Blocks.Count; i++)
        {
            var block = parsed.Blocks[i];
            var text = Flatten(block);
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new SearchHitDto(i, block.Kind, Truncate(text, 400)));
            }
        }

        return hits;
    }

    public async Task<string> GetTextAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var parsed = await ParseAsync(documentId, false, cancellationToken);
        return parsed.PlainText;
    }

    public Task<IReadOnlyList<ParserCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(registry.Capabilities());

    private static string Flatten(ParsedBlockDto block)
    {
        var parts = new List<string>();
        if (block.Text is not null)
        {
            parts.Add(block.Text);
        }

        if (block.Inlines is not null)
        {
            parts.AddRange(block.Inlines.Select(i => i.Text));
        }

        if (block.Children is not null)
        {
            foreach (var child in block.Children)
            {
                parts.Add(Flatten(child));
            }
        }

        return string.Join(" ", parts);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    internal static DocFormat DetectFormat(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".md" or ".markdown" or ".mdown" or ".mkd" => DocFormat.Markdown,
        ".docx" => DocFormat.Docx,
        ".pdf" => DocFormat.Pdf,
        ".diff" or ".patch" => DocFormat.UnifiedDiff,
        ".json" => DocFormat.Json,
        ".html" or ".htm" => DocFormat.Html,
        ".txt" or ".text" or ".log" => DocFormat.PlainText,
        _ => DocFormat.Unknown
    };

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "text/markdown",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        ".txt" or ".text" => "text/plain",
        ".diff" or ".patch" => "text/x-diff",
        _ => "application/octet-stream"
    };
}
