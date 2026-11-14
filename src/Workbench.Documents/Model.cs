using System.Text.Json.Serialization;

namespace Workbench.Documents;

/// <summary>
/// 文档解析产物的统一模型。刻意设计成纯数据 + JSON 友好，
/// 这样可以直接缓存进数据库、直接作为 MCP 结构化输出返回，不需要多态序列化器。
/// </summary>
public sealed record ParsedDocDto(
    string Title,
    string Format,
    IReadOnlyList<ParsedBlockDto> Blocks,
    IReadOnlyList<ParsedRevisionDto> Revisions,
    IReadOnlyList<ParsedOutlineDto> Outline,
    string PlainText)
{
    /// <summary>
    /// 解析失败时的错误说明；成功时为 null。
    ///
    /// 解析器自己兜住包格式异常（损坏的 docx / pdf）后，
    /// 错误信息靠这个字段回传上层 —— 页面要显示「为什么没内容」，
    /// 而不是让异常穿出去变成 500。
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>行内片段。Strike=true 表示原文是删除线（内容已废弃）。</summary>
public sealed record ParsedInlineDto(
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Bold = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Italic = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Strike = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Code = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Link = null);

public sealed record ParsedBlockDto(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Level = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Language = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsDiff = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ListKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Ordered = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Checked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ParsedInlineDto>? Inlines = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ParsedBlockDto>? Children = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Headers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<IReadOnlyList<IReadOnlyList<ParsedInlineDto>>>? Rows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ParsedDiffLineDto>? Lines = null,
    // Author / Date 供批注块使用（Kind = comment）。都是新增可选字段，
    // 老缓存 JSON 里没有它们就是 null，序列化往返不受影响。
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Author = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Date = null);

/// <summary>修订（补丁）。Kind ∈ insert / delete / moveFrom / moveTo。</summary>
public sealed record ParsedRevisionDto(
    string Kind,
    string? Author = null,
    DateTimeOffset? Date = null,
    string Text = "",
    string? Context = null);

public sealed record ParsedOutlineDto(int Level, string Title, int BlockIndex);

public sealed record ParsedDiffLineDto(
    string Kind,
    string Text,
    int? OldLine = null,
    int? NewLine = null);

/// <summary>块类型常量。</summary>
public static class BlockKind
{
    public const string Heading = "heading";
    public const string Paragraph = "paragraph";
    public const string List = "list";
    public const string ListItem = "listItem";
    public const string Table = "table";
    public const string Code = "code";
    public const string Quote = "quote";
    public const string Diff = "diff";

    // 文档正文之外的附属内容（DOCX 才有）。它们照样进 PlainText ——
    // agent 读的是纯文本通道，页眉里的版本号、脚注里的约束、批注里的评审意见都不能丢。
    public const string Header = "header";
    public const string Footer = "footer";
    public const string Footnote = "footnote";
    public const string Comment = "comment";
}

/// <summary>修订类型常量。</summary>
public static class RevisionKindName
{
    public const string Insert = "insert";
    public const string Delete = "delete";
    public const string MoveFrom = "moveFrom";
    public const string MoveTo = "moveTo";
}
