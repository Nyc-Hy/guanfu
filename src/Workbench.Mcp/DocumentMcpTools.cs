using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Workbench.Application.Contracts;
using Workbench.Domain;
using Workbench.Application.Services;
using Workbench.Documents;

namespace Workbench.Mcp;

/// <summary>
/// 文档解读 MCP 工具。核心价值是把「删除线」和「修订（补丁）」这类语义还原出来，
/// 让 agent 知道哪些内容已被废弃、谁在什么时候改了什么。
/// </summary>
[McpServerToolType]
public sealed class DocumentMcpTools(IDocumentService documents)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "doc_parse")]
    [Description(
        "解析文档并返回结构化内容（标题 / 段落 / 列表 / 表格 / 代码块 / 引用 / diff）。" +
        "结果会缓存，重复调用不重复解析。Markdown 与 DOCX 会保留删除线，DOCX 与 diff 会给出修订清单。")]
    public async Task<string> ParseAsync(
        Guid documentId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var parsed = await documents.ParseAsync(documentId, force, cancellationToken);
        return JsonSerializer.Serialize(parsed, JsonOptions);
    }

    [McpServerTool(Name = "doc_get_outline")]
    [Description("获取文档大纲（标题层级树）")]
    public async Task<IReadOnlyList<ParsedOutlineDto>> GetOutlineAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
        => await documents.GetOutlineAsync(documentId, cancellationToken);

    [McpServerTool(Name = "doc_get_text")]
    [Description("获取文档纯文本全文")]
    public async Task<string> GetTextAsync(Guid documentId, CancellationToken cancellationToken = default)
        => await documents.GetTextAsync(documentId, cancellationToken);

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
