using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

internal static class ReviewEvidenceRules
{
    internal static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["code_quality"] = "代码质量",
        ["performance"] = "性能",
        ["security"] = "安全",
        ["scope_impact"] = "关联影响",
        ["requirements"] = "需求完成度",
        ["testing"] = "测试",
        ["compatibility"] = "兼容性与升级",
        ["deployment"] = "部署、回滚与恢复",
        ["accessibility"] = "交互与可访问性"
    };

    internal static void Validate(ReviewReportEvidence? evidence)
    {
        if (evidence is null || evidence.DatabaseChange is not (null or "required" or "not_required" or "unknown"))
        {
            throw new GuanfuException("请提供报告证据。databaseChange 可填 required、not_required 或 unknown。");
        }

        var findings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in evidence.Checks ?? [])
        {
            if (check is null || check.Category is null || !Categories.ContainsKey(check.Category) ||
                !categories.Add(check.Category) || Missing(check.Summary) ||
                check.Status is not ("passed" or "failed" or "unverified" or "not_applicable") ||
                (check.Status is "passed" or "failed" && Missing(check.Evidence)))
            {
                throw new GuanfuException("每个分类只能有一个检查结论，须填写说明。通过或不通过须填写证据。");
            }
        }

        foreach (var finding in evidence.Findings ?? [])
        {
            if (finding is null || Missing(finding.Key) || !findings.Add(finding.Key.Trim()) ||
                finding.Category is null || !Categories.ContainsKey(finding.Category) ||
                (finding.RelatedCategories ?? []).Any(c => c is null || !Categories.ContainsKey(c)) ||
                finding.Severity is not ("blocker" or "high" or "medium" or "low") ||
                finding.Status is not ("open" or "fixed" or "verified") ||
                Missing(finding.Title) || Missing(finding.Evidence) ||
                (finding.Status == "verified" && Missing(finding.Verification)))
            {
                throw new GuanfuException("问题须有唯一编号、有效分类、严重程度、状态、标题和证据。已复验须填写复验记录。");
            }
        }

        var requirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in evidence.Requirements ?? [])
        {
            if (requirement is null || Missing(requirement.Key) || !requirements.Add(requirement.Key.Trim()) ||
                Missing(requirement.Requirement) || Missing(requirement.Source) ||
                requirement.Status is not ("complete" or "partial" or "missing" or "unverified") ||
                (requirement.Status == "complete" && (Missing(requirement.Implementation) || Missing(requirement.Evidence))))
            {
                throw new GuanfuException("需求对照须有唯一编号、文档来源、条款和状态。已完成须填写实现及验收证据。");
            }
        }

        foreach (var change in evidence.Changes ?? [])
        {
            if (change is null || Missing(change.Path) || Missing(change.Change) ||
                !IsRequirementReference(change.RequirementKey, requirements) ||
                (change.Shared && !Missing(change.RequirementKey)))
            {
                throw new GuanfuException("变更须提供文件路径、变更说明和有效需求编号。未归属时留空需求编号。");
            }
        }

        foreach (var test in evidence.TestCases ?? [])
        {
            if (test is null || Missing(test.Name) || !IsRequirementReference(test.RequirementKey, requirements) ||
                test.Status is not ("passed" or "failed" or "not_run" or "missing") ||
                (test.Status is "passed" or "failed" && Missing(test.Evidence)))
            {
                throw new GuanfuException("用例须提供名称、有效需求编号和状态。已执行的用例须附执行证据。");
            }
        }
    }

    private static bool Missing(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool IsRequirementReference(string? key, HashSet<string> requirements) =>
        Missing(key) || requirements.Contains(key!.Trim());
}
