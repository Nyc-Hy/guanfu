using Workbench.Domain;

namespace Workbench.Documents;

/// <summary>
/// Unified diff 解析。用于 .diff/.patch 文件，以及任何 diff 文本。
/// </summary>
public static class DiffTextParser
{
    public const string Context = "context";
    public const string Added = "added";
    public const string Removed = "removed";
    public const string Header = "header";

    public static IReadOnlyList<ParsedDiffLineDto> ParseLines(string diffText)
    {
        var lines = new List<ParsedDiffLineDto>();
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return lines;
        }

        int? oldLine = null;
        int? newLine = null;

        foreach (var raw in diffText.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                lines.Add(new ParsedDiffLineDto(Header, raw));
                (oldLine, newLine) = ParseHunkHeader(raw);
                continue;
            }

            if (raw.StartsWith("--- ", StringComparison.Ordinal) ||
                raw.StartsWith("+++ ", StringComparison.Ordinal) ||
                raw.StartsWith("diff ", StringComparison.Ordinal) ||
                raw.StartsWith("index ", StringComparison.Ordinal))
            {
                lines.Add(new ParsedDiffLineDto(Header, raw));
                continue;
            }

            if (raw.StartsWith('+'))
            {
                lines.Add(new ParsedDiffLineDto(Added, raw[1..], OldLine: null, NewLine: newLine));
                if (newLine.HasValue)
                {
                    newLine++;
                }

                continue;
            }

            if (raw.StartsWith('-'))
            {
                lines.Add(new ParsedDiffLineDto(Removed, raw[1..], OldLine: oldLine, NewLine: null));
                if (oldLine.HasValue)
                {
                    oldLine++;
                }

                continue;
            }

            if (raw.StartsWith(' '))
            {
                lines.Add(new ParsedDiffLineDto(Context, raw[1..], OldLine: oldLine, NewLine: newLine));
                if (oldLine.HasValue)
                {
                    oldLine++;
                }

                if (newLine.HasValue)
                {
                    newLine++;
                }

                continue;
            }

            lines.Add(new ParsedDiffLineDto(Context, raw));
        }

        return lines;
    }

    private static (int? Old, int? New) ParseHunkHeader(string header)
    {
        // @@ -a,b +c,d @@
        try
        {
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.StartsWith('-') && part.Length > 1)
                {
                    var seg = part[1..].Split(',');
                    var start = int.Parse(seg[0]);
                    return (start, null);
                }
            }

            foreach (var part in parts)
            {
                if (part.StartsWith('+') && part.Length > 1)
                {
                    var seg = part[1..].Split(',');
                    var start = int.Parse(seg[0]);
                    return (null, start);
                }
            }
        }
        catch (FormatException)
        {
            // 头格式异常时忽略行号，不影响内容解析
        }

        return (null, null);
    }
}

/// <summary>diff / patch 文件解析器。</summary>
public sealed class DiffDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.UnifiedDiff;

    public bool CanParse(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".diff" or ".patch";
    }

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var lines = DiffTextParser.ParseLines(text);

        var revisions = lines
            .Where(l => l.Kind is DiffTextParser.Added or DiffTextParser.Removed)
            .Select(l => new ParsedRevisionDto(
                l.Kind == DiffTextParser.Added ? RevisionKindName.Insert : RevisionKindName.Delete,
                Text: l.Text,
                Context: Path.GetFileName(fileName)))
            .ToList();

        var block = new ParsedBlockDto(
            BlockKind.Diff,
            Language: "diff",
            Text: text,
            IsDiff: true,
            Lines: lines);

        return new ParsedDocDto(
            Path.GetFileName(fileName),
            DocFormat.UnifiedDiff.ToString(),
            [block],
            revisions,
            [],
            text);
    }
}
