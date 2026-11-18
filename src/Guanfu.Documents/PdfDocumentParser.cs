using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Guanfu.Domain;

namespace Guanfu.Documents;

/// <summary>
/// PDF 解析器。用 PdfPig 提取带位置的词，按基线聚成行，再按行距聚成段落。
/// 注意：PDF 没有语义级的删除线/修订标记，这两项 PDF 不支持（能力矩阵里会体现）。
/// </summary>
public sealed class PdfDocumentParser : IDocumentParser
{
    private const float ParagraphGapRatio = 1.55f;

    public DocFormat Format => DocFormat.Pdf;

    public int ParserVersion => 1;

    public bool CanParse(string fileName) =>
        Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;

            using var document = PdfDocument.Open(buffer);
            var blocks = new List<ParsedBlockDto>();
            var plain = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                foreach (var paragraph in ExtractParagraphs(page))
                {
                    plain.Append(paragraph).Append('\n');
                    blocks.Add(new ParsedBlockDto(BlockKind.Paragraph, Inlines: [new ParsedInlineDto(paragraph)]));
                }
            }

            return new ParsedDocDto(
                Path.GetFileNameWithoutExtension(fileName),
                DocFormat.Pdf.ToString(),
                blocks,
                [],
                [],
                plain.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 损坏 / 不是 pdf 的文件：PdfDocumentFormatException 不在上层的异常名单里，
            // 穿出去就是页面 500。解析器自己兜住：返回空结果 + 明确错误信息。
            return ParseFailure.Create(fileName, DocFormat.Pdf, ex, "pdf 文件损坏或不是有效的 PDF");
        }
    }

    private static IEnumerable<string> ExtractParagraphs(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0)
        {
            yield break;
        }

        // 按基线（Bottom）聚成行
        var lines = words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => g.OrderBy(w => w.BoundingBox.Left).ToList())
            .ToList();

        if (lines.Count == 0)
        {
            yield break;
        }

        // 估算行距，用于判断段落间隔
        var gaps = new List<double>();
        for (var i = 1; i < lines.Count; i++)
        {
            gaps.Add(Math.Abs(lines[i - 1].First().BoundingBox.Bottom - lines[i].First().BoundingBox.Bottom));
        }

        var medianGap = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : 12d;
        var paragraphThreshold = Math.Max(medianGap * ParagraphGapRatio, medianGap + 2d);

        var current = new StringBuilder();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var text = string.Join(' ', line.Select(w => w.Text));
            if (text.Length == 0)
            {
                continue;
            }

            if (current.Length > 0)
            {
                var gap = Math.Abs(lines[i - 1].First().BoundingBox.Bottom - line.First().BoundingBox.Bottom);
                if (gap > paragraphThreshold)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }
                else
                {
                    current.Append(' ');
                }
            }

            current.Append(text);
        }

        if (current.Length > 0)
        {
            yield return current.ToString().Trim();
        }
    }
}
