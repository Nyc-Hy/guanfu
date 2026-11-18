using System.Text;

namespace Guanfu.Application.Services;

/// <summary>
/// 工作区（工位）校验。
///
/// agent 时代的新问题：**干活的是 agent，不是人** —— 几个人共用一个工作区会互相踩文件。
/// 现在的口径：
/// - 项目的主工作区（仓库根）同一时刻只允许**一个** agent 占着；
/// - 要并行，各开各的 git worktree，并把它登记下来。
///
/// 这里只做**纯判断**（读文件、比较路径），不碰数据库 —— 占用查询在服务层。
/// </summary>
public static class WorkPathPolicy
{
    /// <summary>校验结果。</summary>
    public sealed record WorkPathCheck(
        bool Ok,
        /// <summary>规范化后的绝对路径（工位）。</summary>
        string? ResolvedPath,
        /// <summary>true = 独立的 worktree（可以跟别人并行）。</summary>
        bool IsIsolated,
        string? Error)
    {
        public static WorkPathCheck Invalid(string error) => new(false, null, false, error);

        public static WorkPathCheck Main(string path) => new(true, path, false, null);

        public static WorkPathCheck Isolated(string path) => new(true, path, true, null);
    }

    /// <summary>
    /// 校验 agent 声明的 worktree。
    ///
    /// 不跑 git —— 纯读文件：worktree 的标志是「&lt;path&gt;/.git 是一个**文件**」，
    /// 里面写着 `gitdir: &lt;仓库&gt;/.git/worktrees/&lt;名字&gt;`。
    /// 主 checkout 的 .git 是**目录**，嵌套仓库的 .git 也是目录 —— 两者都不是 worktree。
    ///
    /// 校验它确实属于这个仓库的 worktree（而不是随便一个目录）——
    /// 不然 agent 可以声明 `/etc` 之类的地方来绕开主工作区独占。
    /// </summary>
    public static WorkPathCheck ValidateWorktree(string repoRoot, string? declaredPath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return WorkPathCheck.Invalid("项目没有绑定仓库路径，没有 worktree 可校验。");
        }

        if (string.IsNullOrWhiteSpace(declaredPath))
        {
            return WorkPathCheck.Invalid("worktree 路径不能为空。");
        }

        var trimmed = declaredPath.Trim();

        // 相对路径直接拒：拿服务端的当前目录去补全，绑出来的多半不是它想的地方
        if (!Path.IsPathRooted(trimmed))
        {
            return WorkPathCheck.Invalid(
                $"worktree 要填绝对路径（现在填的是「{trimmed}」，是相对路径）。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex)
        {
            return WorkPathCheck.Invalid($"worktree 路径不合法: {ex.Message}");
        }

        if (!Directory.Exists(full))
        {
            return WorkPathCheck.Invalid($"worktree 目录不存在: {full}");
        }

        var gitMarker = Path.Combine(full, ".git");

        if (Directory.Exists(gitMarker))
        {
            // .git 是目录 = 主 checkout 或嵌套仓库，都不是 worktree
            return WorkPathCheck.Invalid(
                $"{full} 不是 worktree（它的 .git 是目录 —— 那是主工作区或嵌套仓库）。" +
                "要用独立工位请先 `git worktree add`.");
        }

        if (!File.Exists(gitMarker))
        {
            return WorkPathCheck.Invalid(
                $"{full} 不是 worktree（没有 .git 文件 —— 那不是 git 仓库目录）。");
        }

        string gitDir;
        try
        {
            gitDir = ReadGitDir(gitMarker);
        }
        catch (Exception ex)
        {
            return WorkPathCheck.Invalid($"读不了 {gitMarker}: {ex.Message}");
        }

        if (gitDir is null)
        {
            return WorkPathCheck.Invalid($"读不了 {gitMarker} 里的 gitdir 指向。");
        }

        // gitdir 必须落在「这个仓库的 .git/worktrees/」下面 ——
        // 否则就是别的仓库的 worktree，声明它等于换了个战场。
        //
        // 两边都先解软链再比：macOS 上 /tmp 其实是 /private/tmp，
        // git 写进 .git 文件的是**没解过的**路径，不解就比不上。
        var expectedRoot = PathBoundary.ResolveRealPath(
            Path.Combine(Path.GetFullPath(repoRoot), ".git", "worktrees"));
        var realGitDir = PathBoundary.ResolveRealPath(gitDir);

        if (!PathBoundary.IsSameOrInside(expectedRoot, realGitDir))
        {
            return WorkPathCheck.Invalid(
                $"{full} 不是本仓库的 worktree（它的 gitdir 在 {gitDir}，" +
                $"不在 {expectedRoot} 下）。");
        }

        return WorkPathCheck.Isolated(full);
    }

    /// <summary>
    /// 这个目录属于哪个仓库根。
    ///
    /// - 主 checkout（.git 是目录）→ 目录自己就是仓库根；
    /// - worktree（.git 是文件）→ 顺着 `gitdir:` 找到 `&lt;仓库&gt;/.git/worktrees/&lt;名字&gt;`，
    ///   仓库根就是它上面两级。
    ///
    /// 用它解决「agent 在 worktree 里干活，会话绑不上项目」的问题：
    /// worktree 是仓库的兄弟目录，不在仓库根里面，但**属于同一个仓库**。
    /// </summary>
    public static string? TryGetOwningRepoRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }

        var marker = Path.Combine(full, ".git");

        // worktree：.git 是文件
        if (File.Exists(marker))
        {
            string? gitDir;
            try
            {
                gitDir = ReadGitDir(marker);
            }
            catch (Exception)
            {
                return null;
            }

            if (gitDir is null)
            {
                return null;
            }

            // <仓库>/.git/worktrees/<名字> → 仓库根是上面三级
            var worktreesDir = Path.GetDirectoryName(gitDir);        // <仓库>/.git/worktrees
            var gitDirOfRepo = Path.GetDirectoryName(worktreesDir);   // <仓库>/.git
            var repoRoot = Path.GetDirectoryName(gitDirOfRepo);       // <仓库>

            return string.IsNullOrEmpty(repoRoot) ? null : Path.GetFullPath(repoRoot);
        }

        // 主 checkout：.git 是目录 → 目录自己就是仓库根
        if (Directory.Exists(marker))
        {
            return full;
        }

        return null;
    }

    /// <summary>
    /// 读 `.git` 文件里的 `gitdir: &lt;path&gt;`。
    /// 不是这个格式就返回 null。
    /// </summary>
    internal static string? ReadGitDir(string gitFilePath)
    {
        // .git 文件很短，但保险起见只读前面一小段
        var content = File.ReadAllText(gitFilePath, Encoding.UTF8);
        var line = content
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));

        if (line is null)
        {
            return null;
        }

        var value = line["gitdir:".Length..].Trim();
        if (value.Length == 0)
        {
            return null;
        }

        // gitdir 可能是相对路径（相对 .git 文件所在目录）
        if (!Path.IsPathRooted(value))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(gitFilePath));
            if (string.IsNullOrEmpty(parent))
            {
                return null;
            }

            value = Path.Combine(parent, value);
        }

        return Path.GetFullPath(value);
    }
}
