using System.Globalization;
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

    /// <summary>
    /// 逐行分类。
    ///
    /// 文件头（--- / +++）必须结合上下文判断：hunk 内部以 "--- " 开头的删除行
    /// （例如删掉一行 Markdown 分隔线或 YAML 文档起始符）不是文件头，
    /// 把它当头部会丢掉一条删除修订，还会把后面的行号全算错。
    /// 判据：文件头出现在任何 hunk 之前，或者 "--- " 后面紧跟 "+++ "。
    /// </summary>
    public static IReadOnlyList<ParsedDiffLineDto> ParseLines(string diffText)
    {
        var lines = new List<ParsedDiffLineDto>();
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return lines;
        }

        int? oldLine = null;
        int? newLine = null;

        var rawLines = diffText.Replace("\r\n", "\n").Split('\n');
        var inHunk = false;

        for (var i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i];

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                lines.Add(new ParsedDiffLineDto(Header, raw));
                (oldLine, newLine) = ParseHunkHeader(raw);
                inHunk = true;
                continue;
            }

            // diff --git / index 行开启一个新文件段落，后面可能跟着真正的文件头
            if (raw.StartsWith("diff ", StringComparison.Ordinal) ||
                raw.StartsWith("index ", StringComparison.Ordinal))
            {
                lines.Add(new ParsedDiffLineDto(Header, raw));
                inHunk = false;
                continue;
            }

            if (raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                var nextIsNewFileHeader = i + 1 < rawLines.Length &&
                    rawLines[i + 1].StartsWith("+++ ", StringComparison.Ordinal);

                if (!inHunk || nextIsNewFileHeader)
                {
                    lines.Add(new ParsedDiffLineDto(Header, raw));
                    inHunk = false;
                    continue;
                }

                // hunk 里的删除行，落到下面的 '-' 分支
            }
            else if (raw.StartsWith("+++ ", StringComparison.Ordinal) &&
                (!inHunk || IsOldFileHeader(lines)))
            {
                // "+++ " 只有在 hunk 之前、或紧跟在 "--- " 文件头后面时才是文件头，
                // 否则它是内容以 "++ " 开头的新增行
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

    /// <summary>上一行是不是刚被判定成文件头的 "--- "。</summary>
    private static bool IsOldFileHeader(List<ParsedDiffLineDto> lines) =>
        lines.Count > 0 &&
        lines[^1].Kind == Header &&
        lines[^1].Text.StartsWith("--- ", StringComparison.Ordinal);

    /// <summary>
    /// 解析 hunk 头 <c>@@ -a,b +c,d @@</c>，同时取出旧文件与新文件的起始行号。
    ///
    /// 之前只取了 '-' 段就返回，新增行的行号恒为 null —— 补丁定位不到新文件的位置。
    /// context 行在新旧两个文件里都有，所以它的行号取 '+' 段的值。
    /// </summary>
    private static (int? Old, int? New) ParseHunkHeader(string header)
    {
        int? oldLine = null;
        int? newLine = null;

        // @@ -a,b +c,d @@ 可选上下文
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part.StartsWith("@@", StringComparison.Ordinal))
            {
                if (i > 0)
                {
                    // 收尾的 @@：后面是函数名之类的上下文，不再看
                    break;
                }

                continue;
            }

            if (oldLine is null && part.Length > 1 && part[0] == '-')
            {
                oldLine = TryParseStart(part[1..]);
                continue;
            }

            if (newLine is null && part.Length > 1 && part[0] == '+')
            {
                newLine = TryParseStart(part[1..]);
                continue;
            }
        }

        return (oldLine, newLine);
    }

    private static int? TryParseStart(string segment)
    {
        var parts = segment.Split(',');
        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            ? start
            : null;
    }

    /// <summary>
    /// 拆出 <c>git format-patch</c> 风格的邮件头：作者与时间。
    ///
    /// 补丁文件本身不带作者与时间，但 format-patch 的头里有（From: / Date:），
    /// AGENTS.md 要求「修订要带作者与时间」，所以这里解析出来。
    /// </summary>
    public static DiffFileHeader ParseFile(string text)
    {
        string? author = null;
        DateTimeOffset? date = null;
        string? subject = null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        for (; index < lines.Length; index++)
        {
            var line = lines[index].Trim();

            if (line.StartsWith("From:", StringComparison.Ordinal))
            {
                author = line["From:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Date:", StringComparison.Ordinal))
            {
                var value = line["Date:".Length..].Trim();
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
                {
                    date = parsed;
                }

                continue;
            }

            if (line.StartsWith("Subject:", StringComparison.Ordinal))
            {
                subject = line["Subject:".Length..].Trim();
                continue;
            }

            if (IsCommitLine(line))
            {
                // format-patch 的第一行 "From <sha> Mon Sep 17 00:00:00 2001"，不是作者
                continue;
            }

            break;
        }

        // 邮件头与正文之间常有空行，别把它算进补丁正文
        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }

        var body = index >= lines.Length ? string.Empty : string.Join("\n", lines.Skip(index));

        return new DiffFileHeader(author, date, subject, body);
    }

    /// <summary>判断是不是 format-patch 的 "From &lt;sha&gt; ..." 那行。</summary>
    private static bool IsCommitLine(string line)
    {
        if (!line.StartsWith("From ", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = line["From ".Length..].Trim();
        var space = rest.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        var sha = rest[..space];
        return sha.Length >= 7 && sha.All(c => char.IsAsciiHexDigit(c));
    }
}

/// <summary>diff 文件的邮件头（作者 / 时间 / 主题）与正文。</summary>
public sealed record DiffFileHeader(
    string? Author,
    DateTimeOffset? Date,
    string? Subject,
    string Body);

/// <summary>diff / patch 文件解析器。</summary>
public sealed class DiffDocumentParser : IDocumentParser
{
    public DocFormat Format => DocFormat.UnifiedDiff;

    /// <summary>v3：hunk 内以 "--- " / "+++ " 开头的增删行不再被误判成文件头。</summary>
    public int ParserVersion => 3;

    public bool CanParse(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".diff" or ".patch";
    }

    public async Task<ParsedDocDto> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // format-patch 的邮件头里带作者与时间，正文才是补丁
        var header = DiffTextParser.ParseFile(text);
        var lines = DiffTextParser.ParseLines(header.Body);

        var revisions = lines
            .Where(l => l.Kind is DiffTextParser.Added or DiffTextParser.Removed)
            .Select(l => new ParsedRevisionDto(
                l.Kind == DiffTextParser.Added ? RevisionKindName.Insert : RevisionKindName.Delete,
                Author: header.Author,
                Date: header.Date,
                Text: l.Text,
                Context: Path.GetFileName(fileName)))
            .ToList();

        var block = new ParsedBlockDto(
            BlockKind.Diff,
            Language: "diff",
            Text: header.Body,
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
