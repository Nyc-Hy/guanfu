using System.ComponentModel;
using ModelContextProtocol.Server;
using Workbench.Application.Contracts;
using Workbench.Domain;
using Workbench.Application.Services;
using Workbench.Documents;

namespace Workbench.Mcp;

/// <summary>文档上传结果。ok=false 时 error 给出原因，不抛异常（MCP 会把异常吞成泛化信息）。</summary>
public sealed record DocUploadToolResult(
    bool Ok,
    DocumentDto? Document,
    string? Error = null);

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
public sealed class DocumentMcpTools(IDocumentService documents)
{
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
            var parsed = await documents.ParseAsync(documentId, force, cancellationToken);

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
        "上传一个文档到任务（存进工作区，之后可用 doc_parse / doc_get_text 等解读）。" +
        "文本内容用 content 传；二进制（pdf / docx / 图片 / zip 等）用 contentBase64 传 Base64。" +
        "两者至少给一个，都给时以 contentBase64 为准。fileName 决定格式，扩展名要在白名单内。" +
        "返回 ok=true 与文档信息；失败时 error 给出原因（如扩展名不在白名单、内容为空、Base64 非法），" +
        "据此修正参数后重试，不要盲目重发。")]
    public async Task<DocUploadToolResult> UploadAsync(
        Guid taskId,
        string fileName,
        string? content = null,
        string? contentBase64 = null,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] bytes;
            if (!string.IsNullOrEmpty(contentBase64))
            {
                try
                {
                    bytes = Convert.FromBase64String(contentBase64);
                }
                catch (FormatException ex)
                {
                    return new DocUploadToolResult(false, null, $"contentBase64 不是合法的 Base64: {ex.Message}");
                }
            }
            else if (content is not null)
            {
                bytes = System.Text.Encoding.UTF8.GetBytes(content);
            }
            else
            {
                return new DocUploadToolResult(false, null, "content 与 contentBase64 至少要给一个。");
            }

            using var stream = new MemoryStream(bytes);
            var doc = await documents.UploadAsync(taskId, fileName, stream, kind, cancellationToken);
            return new DocUploadToolResult(true, doc);
        }
        catch (Exception ex)
        {
            return new DocUploadToolResult(false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "doc_get_outline")]
    [Description("获取文档大纲（标题层级树）")]
    public async Task<IReadOnlyList<ParsedOutlineDto>> GetOutlineAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
        => await documents.GetOutlineAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_get_text")]
    [Description("获取文档纯文本全文。返回 ok=true 与 text；失败时 ok=false 且 error 给出原因。")]
    public async Task<DocTextToolResult> GetTextAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await documents.GetTextAsync(documentId, cancellationToken);
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
        => await documents.GetRevisionsAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_list_struck")]
    [Description("列出文档里所有被划掉（删除线）的内容——「这份文档里已经废弃的部分」")]
    public async Task<IReadOnlyList<StruckContentDto>> ListStruckAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
        => await documents.ListStruckAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_search")]
    [Description("在文档里做块级检索")]
    public async Task<IReadOnlyList<SearchHitDto>> SearchAsync(
        Guid documentId,
        string query,
        CancellationToken cancellationToken = default)
        => await documents.SearchAsync(documentId, query, cancellationToken);

    [McpServerTool(Name = "doc_list_formats")]
    [Description("列出支持的文档格式，以及各格式是否支持删除线 / 修订还原")]
    public async Task<IReadOnlyList<ParserCapability>> ListFormatsAsync(
        CancellationToken cancellationToken = default)
        => await documents.GetCapabilitiesAsync(cancellationToken);
}
