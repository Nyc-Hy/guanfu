using System.ComponentModel;
using ModelContextProtocol.Server;
using Guanfu.Application.Contracts;
using Guanfu.Domain;
using Guanfu.Application.Services;
using Guanfu.Documents;

namespace Guanfu.Mcp;

/// <summary>批量上传里的一个文件。文本用 Content，二进制用 ContentBase64。</summary>
public sealed record DocUploadItem(
    string FileName,
    string? Content = null,
    string? ContentBase64 = null,
    string? Kind = null);

/// <summary>批量上传里失败的一个文件 —— 带文件名与原因，agent 才知道该补哪一个。</summary>
public sealed record DocUploadFailure(string FileName, string Error);

/// <summary>
/// 文档上传结果。ok=false 时 error 给出原因，不抛异常（MCP 会把异常吞成泛化信息）。
///
/// 批量上传时 Document 只在恰好传进去一个文件时有值，Documents 始终是成功的清单，
/// Failures 是失败的文件（一个不行不牵连其它的）。
/// </summary>
public sealed record DocUploadToolResult(
    bool Ok,
    DocumentDto? Document,
    string? Error = null,
    IReadOnlyList<DocumentDto>? Documents = null,
    IReadOnlyList<DocUploadFailure>? Failures = null);

/// <summary>
/// 文档解析结果。
///
/// 返回裸字符串的话 agent 分不出「解析成功但文档是空的」和「解析失败了」——
/// 失败的空文档会被当成「需求里什么都没写」，然后基于空内容往下编。
/// </summary>
public sealed record DocParseToolResult(
    bool Ok,
    ParsedDocDto? Document,
    string? Error = null);

/// <summary>文档纯文本结果。</summary>
public sealed record DocTextToolResult(
    bool Ok,
    string? Text,
    string? Error = null);

/// <summary>
/// 文档解读 MCP 工具。核心价值是把「删除线」和「修订（补丁）」这类语义还原出来，
/// 让 agent 知道哪些内容已被废弃、谁在什么时候改了什么。
/// </summary>
[McpServerToolType]
public sealed class DocumentMcpTools(IDocumentService docs)
{
    /// <summary>一次批量上传的文件数上限。</summary>
    private const int MaxBatchDocuments = 50;

    [McpServerTool(Name = "doc_parse")]
    [Description(
        "解析文档并返回结构化内容（标题 / 段落 / 列表 / 表格 / 代码块 / 引用 / diff）。" +
        "结果会缓存，重复调用不重复解析。Markdown 与 DOCX 会保留删除线，DOCX 与 diff 会给出修订清单。" +
        "返回 ok=true 与解析结果；解析失败时 ok=false 且 error 给出原因（内容会是空的，别当成「文档没写东西」）。")]
    public async Task<DocParseToolResult> ParseAsync(
        Guid documentId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = await docs.ParseAsync(documentId, force, cancellationToken);

            // 解析器自己兜住的失败（损坏的 docx / pdf）算失败：
            // 那种情况下 blocks 是空的，agent 拿到空内容会以为「文档里什么都没写」。
            return parsed.Error is null
                ? new DocParseToolResult(true, parsed)
                : new DocParseToolResult(false, parsed, parsed.Error);
        }
        catch (Exception ex)
        {
            return new DocParseToolResult(false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "doc_upload")]
    [Description(
        "上传文档到任务（存进工作区，之后可用 doc_parse / doc_get_text 等解读）。" +
        "单个文件：给 fileName 与 content（文本）或 contentBase64（二进制 Base64）。" +
        "多个文件：给 documents 数组，每个元素同样是 {fileName, content?, contentBase64?, kind?}，" +
        "一次最多 50 个 —— 需求文档常是「PRD + 接口文档 + 用例」一起来的。" +
        "fileName 决定格式，扩展名要在白名单内；两者至少给一个，都给时以 contentBase64 为准。" +
        "返回 ok=true 与文档信息（Documents 是成功的清单，Failures 是失败的文件及原因）；" +
        "全部失败时 ok=false 且 error 给出原因（如扩展名不在白名单、内容为空、Base64 非法），" +
        "据此修正参数后重试，不要盲目重发。")]
    public async Task<DocUploadToolResult> UploadAsync(
        Guid taskId,
        string? fileName = null,
        string? content = null,
        string? contentBase64 = null,
        string? kind = null,
        IReadOnlyList<DocUploadItem>? documents = null,
        CancellationToken cancellationToken = default)
    {
        var items = documents is { Count: > 0 }
            ? documents
                .Select(d => (d.FileName, d.Content, d.ContentBase64, d.Kind))
                .ToList()
            : [];

        if (items.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return new DocUploadToolResult(false, null, "fileName 与 documents 至少要给一个。");
            }

            items.Add((fileName, content, contentBase64, kind));
        }

        if (items.Count > MaxBatchDocuments)
        {
            return new DocUploadToolResult(false, null, $"一次最多上传 {MaxBatchDocuments} 个文件，收到 {items.Count} 个。");
        }

        var uploaded = new List<DocumentDto>();
        var failures = new List<DocUploadFailure>();

        foreach (var (name, text, base64, itemKind) in items)
        {
            byte[] bytes;
            if (!string.IsNullOrEmpty(base64))
            {
                try
                {
                    bytes = Convert.FromBase64String(base64);
                }
                catch (FormatException ex)
                {
                    failures.Add(new DocUploadFailure(name, $"contentBase64 不是合法的 Base64: {ex.Message}"));
                    continue;
                }
            }
            else if (text is not null)
            {
                bytes = System.Text.Encoding.UTF8.GetBytes(text);
            }
            else
            {
                failures.Add(new DocUploadFailure(name, "content 与 contentBase64 至少要给一个。"));
                continue;
            }

            try
            {
                using var stream = new MemoryStream(bytes);
                uploaded.Add(await docs.UploadAsync(taskId, name, stream, itemKind, cancellationToken));
            }
            catch (Exception ex)
            {
                // 一个文件不行不牵连其它的 —— 批量导入的价值就在这
                failures.Add(new DocUploadFailure(name, ex.Message));
            }
        }

        if (uploaded.Count == 0)
        {
            var reason = failures.Count == 0
                ? "没有可上传的文件。"
                : string.Join("；", failures.Select(f => $"{f.FileName}: {f.Error}"));
            return new DocUploadToolResult(false, null, reason, [], failures);
        }

        var single = uploaded.Count == 1 ? uploaded[0] : null;
        var error = failures.Count == 0
            ? null
            : string.Join("；", failures.Select(f => $"{f.FileName}: {f.Error}"));

        return new DocUploadToolResult(failures.Count == 0, single, error, uploaded, failures);
    }

    [McpServerTool(Name = "doc_get_outline")]
    [Description("获取文档大纲（标题层级树）")]
    public async Task<IReadOnlyList<ParsedOutlineDto>> GetOutlineAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
        => await docs.GetOutlineAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_get_text")]
    [Description("获取文档纯文本全文。返回 ok=true 与 text；失败时 ok=false 且 error 给出原因。")]
    public async Task<DocTextToolResult> GetTextAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await docs.GetTextAsync(documentId, cancellationToken);
            return new DocTextToolResult(true, text);
        }
        catch (Exception ex)
        {
            return new DocTextToolResult(false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "doc_get_revisions")]
    [Description(
        "获取文档的修订（补丁）清单：谁、什么时候、删除还是插入、改了什么内容。" +
        "DOCX 的修订标记与 diff 文件的 +/- 行都会列在这里。")]
    public async Task<IReadOnlyList<ParsedRevisionDto>> GetRevisionsAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
        => await docs.GetRevisionsAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_list_struck")]
    [Description("列出文档里所有被划掉（删除线）的内容——「这份文档里已经废弃的部分」")]
    public async Task<IReadOnlyList<StruckContentDto>> ListStruckAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
        => await docs.ListStruckAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_search")]
    [Description("在文档里做块级检索")]
    public async Task<IReadOnlyList<SearchHitDto>> SearchAsync(
        Guid documentId,
        string query,
        CancellationToken cancellationToken = default)
        => await docs.SearchAsync(documentId, query, cancellationToken);

    [McpServerTool(Name = "doc_list_formats")]
    [Description("列出支持的文档格式，以及各格式是否支持删除线 / 修订还原")]
    public async Task<IReadOnlyList<ParserCapability>> ListFormatsAsync(
        CancellationToken cancellationToken = default)
        => await docs.GetCapabilitiesAsync(cancellationToken);
}
