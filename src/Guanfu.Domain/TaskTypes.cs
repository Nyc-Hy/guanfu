namespace Guanfu.Domain;

/// <summary>
/// 任务类型的规则：编号前缀、中文名。
///
/// 编号按类型分开计数 —— 需求 REQ-001、缺陷 BUG-001、优化 OPT-001 各自成序列。
/// 人说「看下 bug001」时，编号本身就把类型说清楚了。
/// </summary>
public static class TaskTypes
{
    public const string RequirementPrefix = "REQ";
    public const string BugPrefix = "BUG";
    public const string OptimizationPrefix = "OPT";

    public static string Prefix(TaskType type) => type switch
    {
        TaskType.Bug => BugPrefix,
        TaskType.Optimization => OptimizationPrefix,
        _ => RequirementPrefix
    };

    public static string Name(TaskType type) => type switch
    {
        TaskType.Bug => "缺陷",
        TaskType.Optimization => "优化",
        _ => "需求"
    };

    /// <summary>类型徽章的 CSS 后缀。</summary>
    public static string CssClass(TaskType type) => type switch
    {
        TaskType.Bug => "wb-type-bug",
        TaskType.Optimization => "wb-type-opt",
        _ => "wb-type-req"
    };

    /// <summary>
    /// 解析用户/agent 给的类型名。认英文（requirement / bug / optimization）与中文（需求/缺陷/优化）。
    /// 认不出来返回 false —— 调用方要报错而不是默默当需求处理。
    /// </summary>
    public static bool TryParse(string? value, out TaskType type)
    {
        type = TaskType.Requirement;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "requirement" or "req" or "需求" or "feature":
                type = TaskType.Requirement;
                return true;
            case "bug" or "defect" or "缺陷" or "问题":
                type = TaskType.Bug;
                return true;
            case "optimization" or "opt" or "improve" or "优化" or "改进":
                type = TaskType.Optimization;
                return true;
            default:
                return false;
        }
    }

    /// <summary>合法的类型名，供报错时提示用。</summary>
    public static string[] Names => ["requirement(需求)", "bug(缺陷)", "optimization(优化)"];
}
