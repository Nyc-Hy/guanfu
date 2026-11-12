using Workbench.Domain;

namespace Workbench.Documents;

/// <summary>文档解析器。按格式分派。</summary>
public interface IDocumentParser
{
    DocFormat Format { get; }

    /// <summary>能否处理该文件（按扩展名判断）。</summary>
    bool CanParse(string fileName);

    Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken);
}

/// <summary>
/// 解析器注册表：优先按扩展名精确匹配，没有匹配的回退到纯文本，
/// 保证任何文件都能拿到至少一份可读文本。
/// </summary>
public interface IDocumentParserRegistry
{
    IDocumentParser Resolve(string fileName);

    /// <summary>能力矩阵：哪些格式支持删除线 / 修订还原。</summary>
    IReadOnlyList<ParserCapability> Capabilities();
}

public sealed class DocumentParserRegistry : IDocumentParserRegistry
{
    private readonly IReadOnlyList<IDocumentParser> _parsers;

    public DocumentParserRegistry(IEnumerable<IDocumentParser> parsers)
    {
        _parsers = parsers.ToList();
    }

    public IDocumentParser Resolve(string fileName)
    {
        var exact = _parsers.FirstOrDefault(p => p.CanParse(fileName) && p.Format != DocFormat.PlainText);
        if (exact is not null)
        {
            return exact;
        }

        return _parsers.FirstOrDefault(p => p.Format == DocFormat.PlainText)
               ?? throw new InvalidOperationException("没有可用的纯文本兜底解析器。");
    }

    public IReadOnlyList<ParserCapability> Capabilities() =>
        _parsers
            .Select(p => new ParserCapability(
                p.Format.ToString(),
                SupportsStrike: p is MarkdownDocumentParser or DocxDocumentParser,
                SupportsRevisions: p is DocxDocumentParser or DiffDocumentParser))
            .OrderBy(c => c.Format)
            .ToList();
}

public sealed record ParserCapability(string Format, bool SupportsStrike, bool SupportsRevisions);
