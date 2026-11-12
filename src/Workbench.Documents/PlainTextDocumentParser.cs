using System.Text;
using System.Text.Json;
using Workbench.Domain;

namespace Workbench.Documents;

/// <summary>纯文本兜底解析器：按空行分段。</summary>
public sealed class PlainTextDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.PlainText;

    /// <summary>v2：按内容探测编码（UTF-8 / GB18030），不再一律按 UTF-8 解码。</summary>
    public int ParserVersion => 2;

    public bool CanParse(string fileName) => true;

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        // 先取字节再自己判断编码：StreamReader 只会按 UTF-8 解，GBK 文件会变乱码
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var text = TextEncodingDetector.Decode(buffer.ToArray());
        return Parse(text, fileName);
    }

    public static ParsedDocDto Parse(string text, string fileName = "")
    {
        var blocks = new List<ParsedBlockDto>();
        var plain = new StringBuilder();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            plain.Append(trimmed).Append('\n');
            blocks.Add(new ParsedBlockDto(
                BlockKind.Paragraph,
                Inlines: [new ParsedInlineDto(trimmed)]));
        }

        return new ParsedDocDto(
            string.IsNullOrEmpty(fileName) ? "未命名文档" : Path.GetFileNameWithoutExtension(fileName),
            DocFormat.PlainText.ToString(),
            blocks,
            [],
            [],
            plain.ToString());
    }
}

/// <summary>JSON 文档：取顶层字段做大纲，值转成段落。</summary>
public sealed class JsonDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.Json;

    public int ParserVersion => 1;

    public bool CanParse(string fileName) =>
        Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return Parse(text, fileName);
    }

    public static ParsedDocDto Parse(string text, string fileName = "")
    {
        var blocks = new List<ParsedBlockDto>();
        var outline = new List<ParsedOutlineDto>();

        try
        {
            using var doc = JsonDocument.Parse(text);
            Flatten(doc.RootElement, blocks, outline, depth: 0, path: string.Empty);
        }
        catch (JsonException)
        {
            // 不是合法 JSON 就当纯文本
            return PlainTextDocumentParser.Parse(text, fileName);
        }

        return new ParsedDocDto(
            string.IsNullOrEmpty(fileName) ? "未命名文档" : Path.GetFileNameWithoutExtension(fileName),
            DocFormat.Json.ToString(),
            blocks,
            [],
            outline,
            text);
    }

    private static void Flatten(
        JsonElement element,
        List<ParsedBlockDto> blocks,
        List<ParsedOutlineDto> outline,
        int depth,
        string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var index = blocks.Count;
                        blocks.Add(new ParsedBlockDto(
                            BlockKind.Heading,
                            Level: Math.Min(depth + 1, 6),
                            Inlines: [new ParsedInlineDto(property.Name)]));
                        outline.Add(new ParsedOutlineDto(Math.Min(depth + 1, 6), childPath, index));
                        Flatten(property.Value, blocks, outline, depth + 1, childPath);
                    }
                    else
                    {
                        var index = blocks.Count;
                        var value = property.Value.ToString();
                        blocks.Add(new ParsedBlockDto(
                            BlockKind.Paragraph,
                            Inlines: [new ParsedInlineDto($"{property.Name}: {value}")]));
                        outline.Add(new ParsedOutlineDto(Math.Min(depth + 1, 6), childPath, index));
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, blocks, outline, depth + 1, path);
                }

                break;

            default:
                blocks.Add(new ParsedBlockDto(
                    BlockKind.Paragraph,
                    Inlines: [new ParsedInlineDto(element.ToString())]));
                break;
        }
    }
}

/// <summary>HTML 解析器：抽正文 + 解实体 + 分段。</summary>
public sealed class HtmlDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.Html;

    /// <summary>v2：脚本 / 样式整块剔除，标题不再混进正文。</summary>
    public int ParserVersion => 2;

    public bool CanParse(string fileName) =>
        Path.GetExtension(fileName).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(fileName).Equals(".htm", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return Parse(text, fileName);
    }

    public static ParsedDocDto Parse(string html, string fileName = "")
    {
        var blocks = new List<ParsedBlockDto>();
        var plain = new StringBuilder();

        // 脚本 / 样式整块剔除，标题单独抽走 —— 它们的源码不是正文
        var (title, body) = HtmlTextExtractor.Extract(html);

        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            plain.Append(trimmed).Append('\n');
            blocks.Add(new ParsedBlockDto(BlockKind.Paragraph, Inlines: [new ParsedInlineDto(trimmed)]));
        }

        return new ParsedDocDto(
            string.IsNullOrEmpty(title)
                ? (string.IsNullOrEmpty(fileName) ? "未命名文档" : Path.GetFileNameWithoutExtension(fileName))
                : title,
            DocFormat.Html.ToString(),
            blocks,
            [],
            [],
            plain.ToString());
    }
}
