using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 导出。账本和评审结论都得能拿出来看 ——
/// 只在界面里能看的东西，出问题时没法给别人看。
/// </summary>
public interface IExportService
{
    /// <summary>
    /// 导出任务的事件流水。格式：markdown / csv / json。
    /// </summary>
    Task<string> ExportTaskEventsAsync(
        Guid taskId,
        string format = "markdown",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 导出评审报告（某一轮）。格式：markdown / json。
    /// </summary>
    Task<string> ExportReviewReportAsync(
        Guid taskId,
        int? round = null,
        string format = "markdown",
        CancellationToken cancellationToken = default);
}

public sealed class ExportService(IAppDbContext db, IReviewService reviews) : IExportService
{
    public async Task<string> ExportTaskEventsAsync(
        Guid taskId,
        string format = "markdown",
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundException("任务", taskId);

        var events = await db.StatusEvents
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        return format.ToLowerInvariant() switch
        {
            "json" => WorkbenchJson.Serialize(events.Select(ToEventDto)),
            "csv" => ToCsv(events),
            _ => ToMarkdown(task, events)
        };
    }

    public async Task<string> ExportReviewReportAsync(
        Guid taskId,
        int? round = null,
        string format = "markdown",
        CancellationToken cancellationToken = default)
    {
        var rounds = await reviews.ListAsync(taskId, cancellationToken);

        if (rounds.Count == 0)
        {
            throw new WorkbenchException("这个任务还没有评审记录。");
        }

        var target = round is null
            ? rounds.OrderByDescending(r => r.Round).First()
            : rounds.FirstOrDefault(r => r.Round == round.Value)
              ?? throw new NotFoundException("评审轮次", round.Value);

        return format.ToLowerInvariant() switch
        {
            "json" => WorkbenchJson.Serialize(target),
            _ => ToReviewMarkdown(target)
        };
    }

    private static StatusEventDto ToEventDto(StatusEvent e) => new(
        e.Id,
        e.FromStatus,
        e.ToStatus,
        e.Source.ToString(),
        e.Actor,
        e.Note,
        e.CreatedAt,
        e.EventType,
        e.Payload);

    private static string ToMarkdown(TaskItem task, IReadOnlyList<StatusEvent> events)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {task.Key ?? task.Id.ToString()} {task.Title}");
        sb.AppendLine();
        sb.AppendLine($"- 状态: {task.Status}");
        sb.AppendLine($"- 类型: {task.Type}");
        if (!string.IsNullOrWhiteSpace(task.Assignee))
        {
            sb.AppendLine($"- 负责人: {task.Assignee}");
        }

        sb.AppendLine($"- 事件数: {events.Count}");
        sb.AppendLine();
        sb.AppendLine("## 事件流水");
        sb.AppendLine();
        sb.AppendLine("| 时间 | 来源 | 事件 | 从 | 到 | 说明 |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (var e in events)
        {
            sb.AppendLine(
                $"| {e.CreatedAt:yyyy-MM-dd HH:mm:ss} | {e.Source} | {e.EventType ?? "-"} " +
                $"| {(e.FromStatus?.ToString() ?? "-")} | {(e.ToStatus?.ToString() ?? "-")} " +
                $"| {EscapeCell(e.Note)} |");
        }

        return sb.ToString();
    }

    private static string ToCsv(IReadOnlyList<StatusEvent> events)
    {
        var sb = new StringBuilder();

        sb.AppendLine("id,createdAt,source,actor,eventType,from,to,note");

        foreach (var e in events)
        {
            sb.AppendLine(string.Join(",",
                e.Id,
                e.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Csv(e.Source.ToString()),
                Csv(e.Actor),
                Csv(e.EventType),
                Csv(e.FromStatus?.ToString()),
                Csv(e.ToStatus?.ToString()),
                Csv(e.Note)));
        }

        return sb.ToString();
    }

    private static string ToReviewMarkdown(ReviewRoundDto round)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# 评审报告 · 第 {round.Round} 轮");
        sb.AppendLine();
        sb.AppendLine($"- 结论: {round.Verdict}");
        if (!string.IsNullOrWhiteSpace(round.Reviewer))
        {
            sb.AppendLine($"- 评审人: {round.Reviewer}");
        }

        sb.AppendLine($"- 开始: {round.StartedAt:yyyy-MM-dd HH:mm:ss}");
        if (round.FinishedAt is not null)
        {
            sb.AppendLine($"- 结束: {round.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        }

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(round.Summary))
        {
            sb.AppendLine("## 总体说明");
            sb.AppendLine();
            sb.AppendLine(round.Summary);
            sb.AppendLine();
        }

        sb.AppendLine("## 维度结论");
        sb.AppendLine();

        foreach (var item in round.Items)
        {
            sb.AppendLine($"### {item.Order}. {item.DimensionName} ({item.Dimension})");
            sb.AppendLine();
            sb.AppendLine($"- 状态: {item.Status}");
            if (!string.IsNullOrWhiteSpace(item.Findings))
            {
                sb.AppendLine($"- 发现: {item.Findings}");
            }

            if (!string.IsNullOrWhiteSpace(item.Evidence))
            {
                sb.AppendLine($"- 证据: {item.Evidence}");
            }

            if (!string.IsNullOrWhiteSpace(item.Recommendation))
            {
                sb.AppendLine($"- 建议: {item.Recommendation}");
            }

            if (!string.IsNullOrWhiteSpace(item.Reviewer))
            {
                sb.AppendLine($"- 评审人: {item.Reviewer}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeCell(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        // Markdown 表格里的竖线和换行会破坏表格结构
        return text.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// CSV 单元格转义 + **公式注入防护**。
    ///
    /// Excel / LibreOffice 会把 =、+、-、@（以及制表符、回车）开头的单元格当公式执行：
    /// 任务备注这种用户可控文本写成 `=HYPERLINK("http://evil","点我")` 甚至 DDE 命令，
    /// 导出的人一打开就中招。加引号只解决分隔符，解决不了这个 ——
    /// 必须在前面补一个单引号，让表格软件按纯文本处理（单引号本身不显示）。
    /// 代价是负数（-5）也会带上单引号变成文本，这是 OWASP 推荐的取舍。
    /// </summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var safe = IsFormulaStart(value) ? "'" + value : value;

        // 含逗号/引号/换行就得加引号；引号本身翻倍
        if (safe.Contains(',') || safe.Contains('"') || safe.Contains('\n') || safe.Contains('\r'))
        {
            return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return safe;
    }

    /// <summary>首字符是否会触发表格软件的公式解释。</summary>
    private static bool IsFormulaStart(string value) =>
        value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
}
