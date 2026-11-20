using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

internal static class ReviewReportHtml
{
    private const string FilterScript = """
        document.querySelectorAll("[data-table-filter]").forEach(input => {
          const table = input.parentElement.nextElementSibling;
          input.addEventListener("input", () => {
            const terms = input.value.toLowerCase().trim().split(/\s+/);
            for (const row of table.querySelectorAll("tbody tr")) {
              const value = row.textContent.toLowerCase();
              row.hidden = !terms.every(term => value.includes(term));
            }
          });
        });
        """;

    private static readonly string FilterHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(FilterScript)));

    internal static string Task(
        string name, ReviewRoundDto? round, TestRun? testRun, IReadOnlyList<SpecItemDto> specItems,
        ReviewRoundDto? previous = null)
    {
        var title = $"{name} · 评审报告";
        var body = new StringBuilder();
        var report = round?.ReportEvidence?.Evidence;
        var findings = (report?.Findings ?? []).OrderBy(f => SeverityOrder(f.Severity)).ToList();
        var previousKeys = previous?.ReportEvidence?.Evidence.Findings?
            .Select(f => f.Key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        body.AppendLine("""
            <nav aria-label="报告目录"><a href="#overview">总览</a><a href="#dimensions">各维度</a>
            <a href="#changes">需求 Diff</a><a href="#requirements">需求对照</a><a href="#tests">测试</a></nav>
            <section id="overview"><h2>总览</h2>
            """);
        Paragraph(body, round is null
            ? "未开始评审"
            : $"第 {round.Round} 轮 · {Verdict(round.Verdict)} · {round.PendingCount} 个维度待评 · {round.FailedCount} 个维度未通过");
        Paragraph(body, $"对比基线 {report?.Baseline ?? "未登记"} · 评审版本 {report?.Revision ?? round?.CommitHash ?? "未登记"}");
        if (round?.ReportEvidence is { } record)
        {
            Paragraph(body, $"报告证据第 {record.Version} 版 · {record.RecordedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC {record.RecordedBy}");
        }
        Paragraph(body, round?.Summary);
        if (findings.Count == 0)
        {
            Paragraph(body, report is null ? "尚未提交结构化评审问题。" : "本轮未登记结构化问题。");
        }
        else
        {
            Table(body, ["编号", "分类", "优先级", "状态", "问题", "位置", "证据", "复验"],
                findings.Select(f => new[]
                {
                    f.Key, ReviewEvidenceRules.Categories[f.Category], Severity(f.Severity),
                    previousKeys?.Contains(f.Key.Trim()) == false ? $"本轮新增 · {Status(f.Status)}" : Status(f.Status),
                    f.Title, f.Location, f.Evidence, f.Verification
                }), filter: true);
        }
        var untracked = (round?.Items ?? []).Where(i => i.Status == "Failed").Select(i => i.DimensionName).ToList();
        if (untracked.Count > 0)
        {
            Paragraph(body, $"未通过的评审维度：{string.Join("、", untracked)}。完整结论见各维度。");
        }
        if (previousKeys is not null && report is not null)
        {
            var currentKeys = findings.Select(f => f.Key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var absent = previousKeys.Where(id => !currentKeys.Contains(id)).ToList();
            if (absent.Count > 0)
            {
                Paragraph(body, $"上一轮问题未在本轮回写：{string.Join("、", absent)}。复验状态待确认。");
            }
        }

        body.AppendLine("</section><section id=\"dimensions\"><h2>各维度结论</h2>");
        foreach (var (key, label) in ReviewEvidenceRules.Categories)
        {
            body.Append("<h3>").Append(Encode(label)).AppendLine("</h3>");
            var check = report?.Checks?.FirstOrDefault(c => c.Category == key);
            if (check is not null)
            {
                Paragraph(body, $"{Status(check.Status)} · {check.Summary}");
                Paragraph(body, check.Evidence);
            }
            if (key is "code_quality" or "security" or "scope_impact" or "requirements")
            {
                var dimensions = key switch
                {
                    "code_quality" => new[] { key, "comments", "over_implementation", "over_operation" },
                    "requirements" => new[] { "missing_requirement" },
                    _ => new[] { key }
                };
                var items = (round?.Items ?? []).Where(i => dimensions.Contains(i.Dimension)).ToList();
                if (items.Count > 0)
                {
                    Table(body, ["维度", "结论", "发现", "证据", "建议", "评审人"],
                        items.Select(i => new[]
                        {
                            i.DimensionName, Verdict(i.Status), i.Findings, i.Evidence, i.Recommendation, i.Reviewer
                        }));
                }
            }

            var categoryFindings = findings.Where(f => f.Category == key || (f.RelatedCategories ?? []).Contains(key)).ToList();
            Paragraph(body, categoryFindings.Count > 0
                ? $"问题编号 {string.Join("、", categoryFindings.Select(f => f.Key))}，详见总览。"
                : "本轮未登记此分类的问题。");
        }

        body.AppendLine("</section><section id=\"changes\"><h2>按需求拆分的代码 Diff</h2>");
        var groups = (report?.Changes ?? [])
            .GroupBy(c => (Key: c.RequirementKey?.Trim().ToUpperInvariant(), c.Shared)).ToList();
        if (groups.Count == 0)
        {
            Paragraph(body, "尚未登记本机变更明细。");
        }
        foreach (var group in groups)
        {
            var nameForGroup = string.IsNullOrWhiteSpace(group.Key.Key)
                ? group.Key.Shared ? "共享变更" : "未归属变更"
                : group.First().RequirementKey;
            body.Append("<h3>").Append(Encode(nameForGroup)).AppendLine("</h3>");
            Table(body, ["文件", "变更", "补丁或提交引用"],
                group.Select(c => new[] { c.Path, c.Change, c.Reference }));
            foreach (var change in group.Where(c => !string.IsNullOrWhiteSpace(c.Patch)))
            {
                body.Append("<details><summary>").Append(Encode(change.Path)).Append(" · 补丁</summary><pre>")
                    .Append(Encode(change.Patch)).AppendLine("</pre></details>");
            }
        }

        body.AppendLine("</section><section id=\"requirements\"><h2>需求完成度</h2>");
        if (report?.Requirements is { Count: > 0 } requirements)
        {
            Table(body, ["编号", "来源及版本", "条款", "完成度", "实现", "验收证据"],
                requirements.Select(r => new[] { r.Key, r.Source, r.Requirement, Status(r.Status), r.Implementation, r.Evidence }));
        }
        else
        {
            Paragraph(body, "尚未提交 PRD 或开发文档的逐项对照。");
        }
        if (specItems.Count > 0)
        {
            body.AppendLine("<h3>完成定义</h3>");
            Table(body, ["条款", "判定", "说明", "判定人"],
                specItems.Select(s => new[] { s.Text, Verdict(s.Status), s.Note, s.Reviewer }));
        }

        body.AppendLine("</section><section id=\"tests\"><h2>测试用例与执行</h2>");
        Paragraph(body, TestSummary(testRun));
        Paragraph(body, testRun?.Failures);
        if (report?.TestCases is { Count: > 0 } testCases)
        {
            Table(body, ["用例", "覆盖需求", "状态", "环境", "执行证据"],
                testCases.Select(t => new[] { t.Name, t.RequirementKey, Status(t.Status), t.Environment, t.Evidence }));
        }
        else
        {
            Paragraph(body, "尚未提交逐用例证据。");
        }

        body.AppendLine("</section>");
        return Document(title, body);
    }

    internal static string Release(
        string title, DateTimeOffset time, DateTimeOffset? acceptedAt, string? acceptedBy,
        IReadOnlyList<ReleaseReportTask> tasks, IReadOnlyList<PackagedSqlStep> scripts)
    {
        var body = new StringBuilder();
        Paragraph(body, $"生成时间 {time:yyyy-MM-dd HH:mm} UTC · {tasks.Count} 项任务");
        Paragraph(body, acceptedAt is { } accepted
            ? $"已验收 · {accepted.ToUniversalTime():yyyy-MM-dd HH:mm} UTC · {acceptedBy}"
            : "版本尚未验收");
        body.AppendLine("""
            <nav aria-label="报告目录"><a href="#tasks">任务</a><a href="#findings">问题</a>
            <a href="#sql">SQL 清单</a><a href="发布说明.md">发布说明</a><a href="manifest.json">归档清单</a></nav>
            <section id="tasks"><h2>版本任务</h2><div class="table-scroll"><table>
            <thead><tr><th>任务</th><th>状态</th><th>需求对照</th><th>评审</th><th>最近测试</th><th>数据库变更</th></tr></thead><tbody>
            """);
        var scriptsByTask = scripts.Select(s => s.TaskId).ToHashSet();
        foreach (var task in tasks)
        {
            var question = task.Round?.ReportEvidence?.Evidence.DatabaseChange;
            var hasScript = scriptsByTask.Contains(task.Id);
            var database = (question, hasScript) switch
            {
                ("required", false) => "需要 SQL，未纳入脚本",
                ("not_required", false) => "已标记无需 SQL",
                ("not_required", true) => "已纳入 SQL，与评审声明不一致",
                (_, true) => "已纳入 SQL，见执行清单",
                _ => "是否需要 SQL 待确认"
            };
            var href = string.Join('/', task.ReportPath.Split('/').Select(Uri.EscapeDataString));
            body.Append("<tr><th scope=\"row\"><a href=\"").Append(Encode(href)).Append("\">")
                .Append(Encode(task.Name)).Append("</a></th>");
            Cells(body, [UiText.Status(task.Status),
                task.Round?.ReportEvidence?.Evidence.Requirements is { Count: > 0 } requirements
                    ? $"{requirements.Count(r => r.Status == "complete")}/{requirements.Count} 已完成"
                    : "尚无逐项证据",
                task.Round is { } round ? $"第 {round.Round} 轮 · {Verdict(round.Verdict)}" : "未评审",
                TestSummary(task.TestRun), database]);
            body.AppendLine("</tr>");
        }

        body.AppendLine("</tbody></table></div></section><section id=\"findings\"><h2>版本评审问题</h2>");
        var findings = tasks.SelectMany(t => (t.Round?.ReportEvidence?.Evidence.Findings ?? [])
                .Select(f => (Task: t, Finding: f)))
            .OrderBy(entry => SeverityOrder(entry.Finding.Severity))
            .ToList();
        if (findings.Count == 0)
        {
            Paragraph(body, "本包所引用的评审轮次没有登记结构化问题。各任务的维度结论见详细报告。");
        }
        else
        {
            Paragraph(body, $"{findings.Count} 个问题 · {findings.Count(f => f.Finding.Severity == "blocker" && f.Finding.Status != "verified")} 个阻塞项尚未通过复验");
            Table(body, ["任务", "编号", "优先级", "状态", "问题"],
                findings.Select(entry => new[]
                {
                    entry.Task.Name, entry.Finding.Key, Severity(entry.Finding.Severity),
                    Status(entry.Finding.Status), entry.Finding.Title
                }), filter: true);
        }

        body.AppendLine("</section><section id=\"sql\"><h2>SQL 执行清单</h2>");
        if (scripts.Count == 0)
        {
            Paragraph(body, "这个包没有纳入 SQL。各任务的数据库变更口径见上表。");
        }
        else
        {
            Table(body, ["阶段", "顺序", "目标数据库", "来自任务", "脚本"],
                scripts.Select(s => new[]
                {
                    Phase(s.Phase), s.Order.ToString(CultureInfo.InvariantCulture), s.Target, s.TaskKey, s.Path
                }));
        }
        body.AppendLine("</section>");

        return Document(title, body);
    }

    internal static string Phase(string phase) => phase switch
    {
        "deploy" => "上线",
        "verify" => "核验",
        "rollback" => "回滚",
        _ => phase
    };

    private static string TestSummary(TestRun? run) => run is null
        ? "尚未上报测试结果"
        : $"最近上报 {run.ReportedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC · {run.Passed}/{run.Total} 通过 · {run.Failed} 失败"
            + (run.Coverage is { } coverage ? $" · 覆盖率 {coverage:P1}" : "");

    private static string Verdict(string verdict) => verdict switch
    {
        "Passed" => "通过",
        "Failed" => "不通过",
        "Skipped" => "不适用",
        _ => "待评"
    };

    private static int SeverityOrder(string severity) => severity switch
    {
        "blocker" => 0, "high" => 1, "medium" => 2, _ => 3
    };

    private static string Severity(string severity) => severity switch
    {
        "blocker" => "阻塞", "high" => "高", "medium" => "中", _ => "低"
    };

    private static string Status(string status) => status switch
    {
        "open" => "待修复", "fixed" => "已修复待复验", "verified" => "已复验",
        "complete" => "完成", "partial" => "部分完成", "missing" => "缺失",
        "passed" => "通过", "failed" => "失败", "not_run" => "未执行",
        "not_applicable" => "不适用", _ => "待确认"
    };

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static void Paragraph(StringBuilder body, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            body.Append("<p>").Append(Encode(text)).AppendLine("</p>");
        }
    }

    private static void Cells(StringBuilder body, IEnumerable<string?> cells)
    {
        foreach (var cell in cells)
        {
            body.Append("<td>").Append(Encode(cell)).Append("</td>");
        }
    }

    private static void Table(StringBuilder body, string[] headers, IEnumerable<string?[]> rows, bool filter = false)
    {
        if (filter)
        {
            body.AppendLine("""<label>筛选问题 <input type="search" data-table-filter placeholder="如 阻塞 待修复"></label>""");
        }
        body.AppendLine("<div class=\"table-scroll\"><table><thead><tr>");
        foreach (var header in headers)
        {
            body.Append("<th scope=\"col\">").Append(Encode(header)).Append("</th>");
        }
        body.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            body.Append("<tr>");
            Cells(body, row);
            body.AppendLine("</tr>");
        }
        body.AppendLine("</tbody></table></div>");
    }

    private static string Document(string title, StringBuilder body) => $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'sha256-{{FilterHash}}'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
        <title>{{Encode(title)}}</title>
        <style>
        :root { color-scheme: light dark; --surface: #fff; --page: #f4f6fa; --text: #162237; --line: #d9e0e9; --link: #2256a3; }
        @media (prefers-color-scheme: dark) {
          :root { --surface: #202b3d; --page: #111a27; --text: #e9eff9; --line: #465770; --link: #afd1ff; }
        }
        body { margin: 0; background: var(--page); color: var(--text); font: 16px/1.65 system-ui,sans-serif; }
        main { max-width: 78rem; margin: auto; padding: 2rem clamp(1rem,4vw,3rem); }
        h1 { margin: 0 0 1rem; line-height: 1.2; }
        h2, h3 { line-height: 1.35; }
        section { margin: 1.25rem 0; padding: 1rem 1.25rem; background: var(--surface); border: 1px solid var(--line); border-radius: .5rem; }
        nav { display: flex; flex-wrap: wrap; gap: .5rem 1.25rem; }
        a { color: var(--link); }
        input { font: inherit; margin: .25rem 0 .65rem; }
        .table-scroll { overflow-x: auto; }
        table { width: 100%; border-collapse: collapse; text-align: left; }
        th, td { vertical-align: top; padding: .55rem .6rem; border-bottom: 1px solid var(--line); }
        details { margin: .6rem 0; }
        pre { overflow-x: auto; padding: .75rem; border: 1px solid var(--line); }
        th { white-space: nowrap; }
        td, p { overflow-wrap: anywhere; white-space: pre-wrap; }
        @media print { section { break-inside: avoid; } }
        </style>
        </head>
        <body><main><h1>{{Encode(title)}}</h1>
        {{body}}
        </main><script>{{FilterScript}}</script></body></html>
        """;
}

internal sealed record ReleaseReportTask(
    Guid Id, string Name, string Status, string ReportPath, ReviewRoundDto? Round, TestRun? TestRun,
    ReviewRoundDto? PreviousRound);

internal sealed record PackagedSqlStep(
    Guid ItemId, Guid TaskId, string TaskKey, string Phase, int Order, string Target, string Path);
