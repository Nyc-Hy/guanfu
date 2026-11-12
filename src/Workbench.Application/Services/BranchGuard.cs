using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 分支守卫的结果。
///
/// 任务项绑定了分支，就只有在仓库当前分支与之一致时才会运行。
/// 守卫要把「当前在哪个分支」带回去 —— 只说「不允许」agent 无从纠正。
/// </summary>
public sealed record BranchCheckResult(
    bool Ok,
    string? BoundBranch = null,
    string? CurrentBranch = null,
    string? Error = null)
{
    public static BranchCheckResult Pass(string? bound, string? current) => new(true, bound, current);

    public static BranchCheckResult Fail(string bound, string? current, string error) =>
        new(false, bound, current, error);
}

/// <summary>分支守卫。任务项绑了分支，运行前必须确认仓库当前就在那个分支上。</summary>
public interface IBranchGuard
{
    Task<BranchCheckResult> CheckAsync(Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>当前分支与任务绑定的分支不一致。</summary>
public class BranchMismatchException(BranchCheckResult check) : WorkbenchException(check.Error ?? "分支不匹配。")
{
    public BranchCheckResult Check { get; } = check;
}

/// <summary>
/// 默认实现：读任务项的绑定分支 + 项目的仓库路径，用 <see cref="IGitInspector"/> 看当前分支。
///
/// 没绑分支 → 放行（不设约束）。
/// 绑了分支但读不到当前分支 → 拒绝（项目没配仓库路径 / git 不可用）。
/// 「不知道当前在哪个分支」不等于「在对的分支上」，这种含糊不能放行。
/// </summary>
public sealed class BranchGuard(IAppDbContext db, IGitInspector git) : IBranchGuard
{
    public async Task<BranchCheckResult> CheckAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        if (task is null)
        {
            return BranchCheckResult.Fail(string.Empty, null, $"任务未找到: {taskId}");
        }

        var bound = task.Branch?.Trim();
        if (string.IsNullOrWhiteSpace(bound))
        {
            return BranchCheckResult.Pass(null, null);
        }

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken);

        if (project is null || string.IsNullOrWhiteSpace(project.RepoPath))
        {
            return BranchCheckResult.Fail(
                bound, null,
                $"任务绑定了分支「{bound}」，但项目没有配置仓库路径，无法确认当前分支，不予运行。");
        }

        var state = git.Inspect(project.RepoPath!);

        if (string.IsNullOrWhiteSpace(state.Branch))
        {
            var reason = string.IsNullOrWhiteSpace(state.Error) ? "读不到当前分支" : state.Error;
            return BranchCheckResult.Fail(
                bound, null,
                $"任务绑定了分支「{bound}」，但{reason}，不予运行。");
        }

        var current = state.Branch!;

        if (!SameBranch(bound, current))
        {
            return BranchCheckResult.Fail(
                bound, current,
                $"任务绑定了分支「{bound}」，但仓库当前在「{current}」上，不在绑定分支上不会运行。");
        }

        return BranchCheckResult.Pass(bound, current);
    }

    /// <summary>分支名比较：去掉 refs/heads/ 前缀，大小写不敏感。</summary>
    internal static bool SameBranch(string left, string right)
    {
        static string Normalize(string value)
        {
            var trimmed = value.Trim();
            const string prefix = "refs/heads/";

            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..];
            }

            return trimmed.Trim('/').ToLowerInvariant();
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }
}
