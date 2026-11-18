namespace Guanfu.Application.Services;

/// <summary>
/// 工位建议：agent 该去哪个目录干活、怎么建。
///
/// **观复不替 agent 建 worktree**（规矩 35）—— 那是它自己仓库里的事，
/// 观复只做两件事：告诉它「该去哪儿」，以及校验它报上来的工位是不是真的。
///
/// 所以这里给的是**路径与命令**，不是已经建好的目录。
/// </summary>
public static class WorkstationAdvice
{
    /// <summary>
    /// 建议的独立工位路径：仓库**旁边**的一个专门目录，按任务编号分目录。
    ///
    /// 为什么放旁边而不是仓库里：仓库里的目录会变成主工作区的未跟踪文件，
    /// 每次 `git status` 都脏一遍，agent 提交时还可能误把它们加进去。
    /// 放旁边的话，worktree 本身就是仓库的兄弟目录 —— git 本来就这么设计的。
    /// </summary>
    public static string? SuggestWorktree(string? repoPath, string? taskKey)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(repoPath);
        }
        catch (Exception)
        {
            return null;
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var folder = Path.Combine(parent, $"{name}-wb-worktrees");

        return Path.Combine(folder, string.IsNullOrWhiteSpace(taskKey) ? "task" : Sanitize(taskKey));
    }

    /// <summary>
    /// 建议的分支名：任务绑了分支就用那个，没绑就用 `guanfu/&lt;编号&gt;`。
    /// </summary>
    public static string? SuggestBranch(string? taskKey, string? taskBranch)
    {
        if (!string.IsNullOrWhiteSpace(taskBranch))
        {
            return taskBranch.Trim();
        }

        if (string.IsNullOrWhiteSpace(taskKey))
        {
            return null;
        }

        return $"guanfu/{taskKey.Trim().ToLowerInvariant()}";
    }

    /// <summary>
    /// 建这个工位的命令。给 agent 照着抄就行 ——
    /// agent 不知道该敲什么是很常见的卡点（尤其是 worktree 的路径规则）。
    /// </summary>
    public static IReadOnlyList<string> SetupCommands(string? repoRoot, string? worktreePath, string? branch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(worktreePath))
        {
            return [];
        }

        var commands = new List<string>();

        if (!string.IsNullOrWhiteSpace(branch))
        {
            commands.Add($"git -C {repoRoot} worktree add {worktreePath} -b {branch}");
            commands.Add($"# 分支 {branch} 已经存在时：git -C {repoRoot} worktree add {worktreePath} {branch}");
        }
        else
        {
            commands.Add($"git -C {repoRoot} worktree add {worktreePath}");
        }

        commands.Add(
            $"# 建好之后领活时把工位报上来：guanfu_claim_task(taskId, agentId, workPath: \"{worktreePath}\")");

        return commands;
    }

    /// <summary>路径片段只留字母数字与连字符（任务编号 REQ-001 就是这个形状）。</summary>
    private static string Sanitize(string value)
    {
        var chars = value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-')
            .ToArray();

        var text = new string(chars).Trim('-');

        return string.IsNullOrEmpty(text) ? "task" : text;
    }
}
