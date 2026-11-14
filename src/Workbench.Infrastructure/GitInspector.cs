using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Workbench.Application.Services;

namespace Workbench.Infrastructure;

/// <summary>
/// 只读 Git 检视，走本地 git CLI。
/// 刻意不提供任何写操作——commit/push 必须经过审批流程，由上层服务调用。
/// </summary>
public sealed class GitInspector : IGitInspector
{
    /// <summary>
    /// git 命令的超时上限。凭证提示、坏索引、网络盘都可能让 git 挂住不退出，
    /// 请求线程不能被无限期占住。
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public GitState Inspect(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitState(null, null, false, 0, $"仓库路径不存在: {repoPath}");
        }

        try
        {
            // Inspect 是同步契约（调用方多），保留同步。这里直接阻塞等异步实现完成，
            // 不再套 Task.Run —— 底下整条链路（GitProcessRunner / InspectAsync）都用了
            // ConfigureAwait(false)，续体不会回贴到调用方的 SynchronizationContext，
            // 所以在 Blazor 渲染线程上阻塞也不会互锁。
            return InspectAsync(repoPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new GitState(null, null, false, 0, ex.Message);
        }
    }

    /// <inheritdoc />
    public string? GetPendingCommitFingerprint(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return null;
        }

        try
        {
            // 与 Inspect 同样的同步契约：整条链路 ConfigureAwait(false)，直接阻塞安全。
            return ComputeFingerprintAsync(repoPath).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 读不到仓库（不是 git 仓库 / git 挂住 / 超时）一律返回 null，
            // 调用方必须按「拒绝」处理 —— 不能给出一个「看起来稳定」的假指纹。
            return null;
        }
    }

    /// <summary>
    /// 一个 commit 改了哪些文件。
    ///
    /// 两条命令各管一半：<c>--numstat</c> 给增删行数，<c>--name-status</c> 给变更类型。
    /// 两者按路径合并 —— numstat 只有行数没有类型，name-status 只有类型没有行数。
    ///
    /// 读不到（仓库不存在 / 不是 git 仓库 / git 报错 / 超时）一律返回空列表：
    /// 采集失败不能挡住 agent 干活。
    /// </summary>
    public IReadOnlyList<GitFileChange> GetCommitFileChanges(string repoPath, string commitSha)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath)
            || string.IsNullOrWhiteSpace(commitSha))
        {
            return [];
        }

        try
        {
            return GetCommitFileChangesAsync(repoPath, commitSha).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<GitFileChange>> GetCommitFileChangesAsync(
        string repoPath,
        string commitSha)
    {
        var numstat = await RunGitAsync(
            repoPath, ["-c", "core.quotePath=false", "show", "--numstat", "--format=", commitSha])
            .ConfigureAwait(false);

        if (numstat is null)
        {
            return [];
        }

        var nameStatus = await RunGitAsync(
            repoPath, ["-c", "core.quotePath=false", "show", "--name-status", "--format=", commitSha])
            .ConfigureAwait(false);

        return MergeChanges(
            ParseNumStat(numstat ?? string.Empty),
            ParseNameStatus(nameStatus ?? string.Empty));
    }

    /// <summary>
    /// 工作区里改了哪些文件（相对 HEAD，含未跟踪文件）。
    ///
    /// 「已产出但还没提交」的那部分也得能追到 —— 否则任务在 Review 阶段
    /// 看起来像是啥也没干。
    ///
    /// 读不到时返回空列表，不抛异常。
    /// </summary>
    public IReadOnlyList<GitFileChange> GetWorkingTreeFileChanges(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return [];
        }

        try
        {
            return GetWorkingTreeFileChangesAsync(repoPath).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<GitFileChange>> GetWorkingTreeFileChangesAsync(
        string repoPath)
    {
        // 相对 HEAD 的改动。仓库还没有任何提交时 `git diff HEAD` 会失败，
        // 退回 `git diff --cached`（对空树）——跟指纹计算里的处理一致。
        var numstat = await RunGitAsync(
            repoPath, ["-c", "core.quotePath=false", "diff", "--numstat", "HEAD"])
            .ConfigureAwait(false);

        var nameStatus = await RunGitAsync(
            repoPath, ["-c", "core.quotePath=false", "diff", "--name-status", "HEAD"])
            .ConfigureAwait(false);

        if (numstat is null)
        {
            numstat = await RunGitAsync(
                repoPath, ["-c", "core.quotePath=false", "diff", "--numstat", "--cached"])
                .ConfigureAwait(false);
        }

        if (nameStatus is null)
        {
            nameStatus = await RunGitAsync(
                repoPath, ["-c", "core.quotePath=false", "diff", "--name-status", "--cached"])
                .ConfigureAwait(false);
        }

        var changes = MergeChanges(
            ParseNumStat(numstat ?? string.Empty),
            ParseNameStatus(nameStatus ?? string.Empty)).ToList();

        // 未跟踪文件 diff 看不见，得从 status 里捞。
        var status = await RunGitAsync(
            repoPath, ["-c", "core.quotePath=false", "status", "--porcelain", "-uall"])
            .ConfigureAwait(false);

        if (status is not null)
        {
            foreach (var path in ParseUntrackedPaths(status))
            {
                // 已经算进去的别重复加
                if (changes.Any(c => string.Equals(c.Path, path, StringComparison.Ordinal)))
                {
                    continue;
                }

                changes.Add(new GitFileChange(path, "Added", 0, 0));
            }
        }

        return changes;
    }

    /// <summary>当前 HEAD 的 commit sha。读不到返回 null。</summary>
    public string? GetHeadSha(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return null;
        }

        try
        {
            var result = GitProcessRunner.RunAsync(
                "git",
                ["-c", "core.quotePath=false", "rev-parse", "HEAD"],
                repoPath,
                CommandTimeout,
                CancellationToken.None).GetAwaiter().GetResult();

            if (result.ExitCode != 0)
            {
                return null;
            }

            var sha = result.Stdout.Trim();
            return string.IsNullOrWhiteSpace(sha) ? null : sha;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 跑一条只读的 git 命令。成功返回 stdout，失败返回 null。
    ///
    /// 失败一律吞掉：产出文件采集是「锦上添花」的事，
    /// 读不到就不能把 agent 的主流程带崩。
    /// </summary>
    private static async Task<string?> RunGitAsync(string repoPath, IReadOnlyList<string> args)
    {
        try
        {
            var result = await GitProcessRunner.RunAsync(
                "git", args, repoPath, CommandTimeout, CancellationToken.None).ConfigureAwait(false);

            return result.ExitCode == 0 ? result.Stdout : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>git show --numstat</c> 的输出：<c>added\tdeleted\tpath</c>。
    ///
    /// 两个坑：
    /// - 二进制文件是 <c>-\t-\tpath</c>，增删行数当 0；
    /// - 重命名时 path 是合并写法（<c>old =&gt; new</c> 或
    ///   <c>dir/{old =&gt; new}</c>），要拆出新旧两条路径。
    ///
    /// 原始的第三段一并留着（<see cref="NumStatEntry.RawPath"/>）——
    /// 合并时要靠它跟 name-status 对账：文件名里本来就有 <c>=&gt;</c> 的时候，
    /// 「猜」出来的重命名是错的，只有原文对得上才算数。
    /// </summary>
    internal static IReadOnlyList<NumStatEntry> ParseNumStat(string output)
    {
        var entries = new List<NumStatEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // 按制表符切：<c>added\tdeleted\tpath</c>。
            // 路径里的制表符会被 git 转义成 \t，所以按制表符切是安全的。
            var fields = line.Split('\t');
            if (fields.Length < 3)
            {
                continue;
            }

            var added = ParseLineCount(fields[0]);
            var deleted = ParseLineCount(fields[1]);

            // 合并提交（combined diff）可能给多组增删数（每对父提交一组）。
            // 那些组都是数字 —— 跳过它们，路径是后面那段非数字的内容。
            // 注意留一个字段：路径本身可能就是纯数字（有个文件就叫「123」）。
            var pathIndex = 2;
            while (pathIndex < fields.Length - 1 && IsLineCountField(fields[pathIndex]))
            {
                pathIndex++;
            }

            if (pathIndex >= fields.Length)
            {
                continue;
            }

            var rawPath = string.Join('\t', fields[pathIndex..]);
            var (path, oldPath) = SplitRenamePath(rawPath);

            if (path.Length == 0)
            {
                continue;
            }

            entries.Add(new NumStatEntry(
                rawPath, path, added, deleted, oldPath));
        }

        return entries;
    }

    /// <summary>
    /// 这一段是不是增删行数（数字，或者二进制文件用的 <c>-</c>）。
    /// </summary>
    private static bool IsLineCountField(string value) =>
        value == "-" || (value.Length > 0 && value.All(char.IsAsciiDigit));

    /// <summary>解析 <c>git show --name-status</c> 的输出。</summary>
    internal static IReadOnlyList<NameStatusEntry> ParseNameStatus(string output)
    {
        var entries = new List<NameStatusEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var code = line[..tab];
            if (code.Length == 0)
            {
                continue;
            }

            // 状态字母后面可能跟相似度（R100 / C075）。
            var letter = code[0];
            var kind = letter switch
            {
                'A' => "Added",
                'M' => "Modified",
                'D' => "Deleted",
                'R' => "Renamed",
                'C' => "Added",
                'T' => "Modified",
                _ => "Modified"
            };

            var rest = line[(tab + 1)..];

            // 重命名与复制：旧路径与新路径分列两段
            if (letter is 'R' or 'C')
            {
                var second = rest.IndexOf('\t');
                if (second < 0)
                {
                    // 合并提交的合并 diff（combined diff）只给一个路径：
                    // 状态是「RM」这种两字母的，路径只有新路径那一段。
                    // 旧路径给不出来 —— 留空，由 numstat 那边补。
                    var mergedPath = DecodeGitPath(rest);
                    if (mergedPath.Length == 0)
                    {
                        continue;
                    }

                    entries.Add(new NameStatusEntry(mergedPath, kind, null));
                    continue;
                }

                var oldPath = DecodeGitPath(rest[..second]);
                var newPath = DecodeGitPath(rest[(second + 1)..]);

                if (newPath.Length == 0)
                {
                    continue;
                }

                entries.Add(new NameStatusEntry(newPath, kind, oldPath));
                continue;
            }

            var path = DecodeGitPath(rest);
            if (path.Length == 0)
            {
                continue;
            }

            entries.Add(new NameStatusEntry(path, kind, null));
        }

        return entries;
    }

    /// <summary>
    /// 把 numstat（行数）与 name-status（类型）合并。
    ///
    /// **name-status 是权威**：它给的路径没有歧义（重命名的旧新路径分列两段），
    /// numstat 的重命名路径是拼出来的（<c>old =&gt; new</c> 或
    /// <c>dir/{old =&gt; new}</c>），单看它分不清「重命名」与「文件名里本来就有 =&gt;」。
    /// 所以类型与路径都取 name-status，行数从 numstat 补。
    ///
    /// 配对靠路径：重命名要把 numstat 的原文拆出几种可能，
    /// 拆出来的那一对跟 name-status 给的完全一致才算对上。
    ///
    /// name-status 里没配上的（合并提交时它可能是空的），
    /// numstat 里剩下的也补进去 —— 宁可多一条也别把文件丢了。
    /// </summary>
    internal static IReadOnlyList<GitFileChange> MergeChanges(
        IReadOnlyList<NumStatEntry> numstat,
        IReadOnlyList<NameStatusEntry> nameStatus)
    {
        var result = new List<GitFileChange>();
        var consumed = new HashSet<int>();

        foreach (var entry in nameStatus)
        {
            var added = 0;
            var deleted = 0;

            for (var i = 0; i < numstat.Count; i++)
            {
                if (consumed.Contains(i) || !NumStatMatches(numstat[i], entry))
                {
                    continue;
                }

                added = numstat[i].LinesAdded;
                deleted = numstat[i].LinesDeleted;
                consumed.Add(i);
                break;
            }

            result.Add(new GitFileChange(
                entry.Path, entry.ChangeKind, added, deleted, entry.OldPath));
        }

        for (var i = 0; i < numstat.Count; i++)
        {
            if (consumed.Contains(i))
            {
                continue;
            }

            var entry = numstat[i];

            // 有旧路径说明是重命名（git 的 numstat 把旧新路径拼在一起给了）。
            result.Add(new GitFileChange(
                entry.Path,
                entry.OldPath is null ? "Modified" : "Renamed",
                entry.LinesAdded,
                entry.LinesDeleted,
                entry.OldPath));
        }

        return result;
    }

    /// <summary>
    /// numstat 的一行是不是在说 name-status 里的这一条。
    ///
    /// - 普通文件：整段路径原样对得上就行（<c>has =&gt; arrow.txt</c> 这种文件名
    ///   在 git 眼里就是一个普通文件，不能被当成重命名）。
    /// - 重命名：把 numstat 的原文按 git 的写法拆开，拆出来的旧新路径
    ///   要跟 name-status 给的完全一致才算数。
    /// </summary>
    private static bool NumStatMatches(NumStatEntry numstat, NameStatusEntry entry)
    {
        if (entry.ChangeKind == "Renamed")
        {
            if (entry.OldPath is not null)
            {
                // 有旧路径 —— 拆出来的旧新两段都要对得上。
                return RenameCandidates(numstat.RawPath)
                    .Any(c => string.Equals(c.Old, entry.OldPath, StringComparison.Ordinal)
                              && string.Equals(c.New, entry.Path, StringComparison.Ordinal));
            }

            // 没有旧路径（合并提交的合并 diff 只给一个路径）——
            // 只要拆出来的新路径对得上就算数。
            var (path, _) = SplitRenamePath(numstat.RawPath);
            return string.Equals(path, entry.Path, StringComparison.Ordinal);
        }

        // 不是重命名 —— numstat 的第三段就是文件自己的路径，原样比。
        return string.Equals(
            DecodeGitPath(numstat.RawPath), entry.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把 numstat 的重命名原文拆成「可能的旧路径 / 新路径」候选。
    ///
    /// git 有两种写法：
    /// - <c>old =&gt; new</c>（没有公共目录前缀时）
    /// - <c>dir/{old =&gt; new}</c>（有公共目录前缀时，前缀被提到花括号外）
    ///
    /// 两种都试 —— 文件名里带 <c>{}</c> 的时候（比如 <c>a{b}.txt =&gt; a{c}.txt</c>）
    /// 花括号形式拆不出东西，得靠普通形式才拆得开。
    ///
    /// 而文件名里本来就有 <c>=&gt;</c> 时（<c>has =&gt; arrow.txt</c>），
    /// 拆点在哪全靠猜 —— 所以这里把所有可能的拆法都列出来，
    /// 由调用方拿 name-status 的权威路径去筛。
    /// </summary>
    internal static IReadOnlyList<(string Old, string New)> RenameCandidates(string raw)
    {
        var candidates = new List<(string Old, string New)>();

        // 普通形式先：old => new
        foreach (var (left, right) in SplitOnArrow(raw))
        {
            candidates.Add((DecodeGitPath(left), DecodeGitPath(right)));
        }

        // 花括号形式后：dir/{old => new}。放后面是因为「猜一个主路径」时
        // 它更准 —— git 只在两条路径有公共目录前缀时才给这种写法。
        var open = raw.IndexOf('{');
        var close = open >= 0 ? raw.IndexOf('}', open + 1) : -1;

        if (open >= 0 && close > open)
        {
            var prefix = raw[..open];
            var middle = raw[(open + 1)..close];
            var suffix = raw[(close + 1)..];

            foreach (var (left, right) in SplitOnArrow(middle))
            {
                candidates.Add((
                    DecodeGitPath(prefix + left + suffix),
                    DecodeGitPath(prefix + right + suffix)));
            }
        }

        return candidates;
    }

    /// <summary>按 <c> =&gt; </c> 切开，列出所有可能的拆法。</summary>
    private static IEnumerable<(string Left, string Right)> SplitOnArrow(string value)
    {
        var index = 0;

        while (true)
        {
            var at = value.IndexOf(" => ", index, StringComparison.Ordinal);
            if (at < 0)
            {
                yield break;
            }

            yield return (value[..at], value[(at + 4)..]);
            index = at + 1;
        }
    }

    /// <summary>
    /// 从 numstat 的路径里拆出「新路径」与「旧路径」。
    ///
    /// 两种重命名写法都要认：
    /// - <c>old =&gt; new</c>（没有公共目录前缀时 git 给这种）
    /// - <c>dir/{old =&gt; new}</c>（有公共目录前缀时 git 给花括号形式）
    ///
    /// 引号路径逐段解码 —— git 只把出问题的那一段包起来。
    ///
    /// **这个拆分是有歧义的，而且无法在 numstat 内部解决**：文件名里本来就有
    /// <c>=&gt;</c>（<c>has =&gt; arrow.txt</c>）时，git 的输出跟重命名长得一模一样。
    /// 所以这里只是「猜一个主路径」，**权威路径来自 name-status**
    /// （它的重命名是旧新两段分开的，没有歧义）。
    /// 猜的时候优先挑旧新两段都「干净」的拆法（不含花括号与箭头）——
    /// 拆剩的花括号说明这个拆点切错了。
    /// </summary>
    internal static (string Path, string? OldPath) SplitRenamePath(string raw)
    {
        var candidates = RenameCandidates(raw);
        if (candidates.Count == 0)
        {
            return (DecodeGitPath(raw), null);
        }

        // 旧新两段都干净的 —— 拆对了
        foreach (var candidate in candidates)
        {
            if (IsCleanPath(candidate.Old) && IsCleanPath(candidate.New))
            {
                return (candidate.New, candidate.Old);
            }
        }

        // 没有两段都干净的（文件名里本来就有 =&gt;）——
        // 退而求其次：挑旧路径干净且最长的那个。
        // git 的写法是「旧路径 => 新路径」，旧路径在前，所以先看旧路径。
        var best = candidates[0];
        var bestOldLength = -1;

        foreach (var candidate in candidates)
        {
            if (!IsCleanPath(candidate.Old))
            {
                continue;
            }

            if (candidate.Old.Length > bestOldLength)
            {
                best = candidate;
                bestOldLength = candidate.Old.Length;
            }
        }

        return (best.New, best.Old);
    }

    /// <summary>
    /// 路径「干净」吗 —— 不含没拆完的花括号，也不含没拆完的箭头。
    ///
    /// 拆完的旧新两条路径里不该再出现 <c>{}</c> 或 <c>=&gt;</c>：
    /// 出现就说明这个拆点切在了中间。
    /// </summary>
    private static bool IsCleanPath(string path) =>
        !path.Contains('{') && !path.Contains('}')
        && !path.Contains(" => ", StringComparison.Ordinal);

    /// <summary>
    /// 从 <c>git status --porcelain -uall</c> 里挑出未跟踪文件的路径。
    ///
    /// 只看行首的 <c>??</c>：那是未跟踪。其余状态行（已修改 / 已暂存）由 diff 负责。
    ///
    /// 路径自己解，不复用 <see cref="ParsePorcelainPath"/> ——
    /// 那个方法会把 <c>old -&gt; new</c> 当成重命名改写，
    /// 而文件名里真有个 <c>-&gt;</c> 的话就被改坏了。
    /// </summary>
    internal static IReadOnlyList<string> ParseUntrackedPaths(string porcelain)
    {
        var paths = new List<string>();

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("?? ", StringComparison.Ordinal))
            {
                continue;
            }

            // 只 TrimEnd —— 前导空格是路径的一部分。
            var path = DecodeGitPath(line[3..].TrimEnd());
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>numstat 的行数：二进制文件是 <c>-</c>，当 0。</summary>
    private static int ParseLineCount(string value)
    {
        return int.TryParse(value, out var count) && count >= 0 ? count : 0;
    }

    /// <summary>
    /// 待提交内容的指纹：SHA256(工作区 porcelain 状态 + <c>git diff HEAD</c> 全文
    /// + 每个未跟踪文件的路径与内容哈希)。
    ///
    /// git_commit 的审批要绑它 —— 人批的是「这些内容」，不是「提交这个动作」。
    /// 所以指纹必须对内容敏感：改一个字节、加一个未跟踪文件，指纹都要变。
    /// </summary>
    private static async Task<string> ComputeFingerprintAsync(string repoPath)
    {
        // 1) 工作区状态。用 v1 porcelain（路径解析复用 ParsePorcelainPath）。
        //    -uall 让未跟踪目录展开成逐个文件，否则默认只给目录名，内容哈希无从谈起。
        var status = await GitProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotePath=false", "status", "--porcelain", "-uall"],
            repoPath,
            CommandTimeout,
            CancellationToken.None).ConfigureAwait(false);

        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git status 失败 ({status.ExitCode}): {status.Stderr.Trim()}");
        }

        // 2) 相对 HEAD 的 diff 全文。还没有任何提交的仓库 `git diff HEAD` 会失败，
        //    这时退回 `git diff --cached`（对空树），至少把已暂存内容纳入指纹。
        var diff = await GitProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotePath=false", "diff", "HEAD"],
            repoPath,
            CommandTimeout,
            CancellationToken.None).ConfigureAwait(false);

        if (diff.ExitCode != 0)
        {
            diff = await GitProcessRunner.RunAsync(
                "git",
                ["-c", "core.quotePath=false", "diff", "--cached"],
                repoPath,
                CommandTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }

        // 3) 未跟踪文件逐个哈希。按路径排序保证稳定（porcelain 输出顺序不保证跨平台一致）。
        var untracked = status.Stdout
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("?? ", StringComparison.Ordinal))
            .Select(ParsePorcelainPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // 状态段：只 TrimEnd，不 Trim —— porcelain 第一行常以空格开头（规矩 13），
        // Trim 会把前导空格吃掉，第一条路径就少一个字符，指纹随之失真。
        AppendSegment(sha, "status", status.Stdout.TrimEnd());
        AppendSegment(sha, "diff", diff.Stdout);

        foreach (var path in untracked)
        {
            AppendSegment(sha, "untracked-path", path);

            var fullPath = Path.Combine(repoPath, path);
            if (!File.Exists(fullPath))
            {
                // 状态里有、磁盘上却没有（刚被删 / 是目录）：记一个标记，别整个指纹失败。
                AppendSegment(sha, "untracked-missing", path);
                continue;
            }

            var length = new FileInfo(fullPath).Length;

            // 大文件取舍：超过 10MB 只哈希「路径 + 长度」，不读内容。
            // 逐个读大文件的内容会把指纹计算拖成 I/O 灾难（几百 MB 的产物 / 数据集），
            // 而审批关心的是「哪些文件、多大」这个量级；同尺寸的内容替换确实漏检，
            // 但这类大文件通常不是评审重点，且 diff 段仍会覆盖已跟踪的大文件改动。
            if (length > MaxUntrackedContentBytes)
            {
                AppendSegment(sha, "untracked-large", $"{path}:{length}");
                continue;
            }

            byte[] contentHash;
            using (var stream = File.OpenRead(fullPath))
            {
                contentHash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            }

            AppendSegment(sha, "untracked-content", Convert.ToHexString(contentHash));
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>未跟踪文件内容哈希的尺寸上限，超过就只哈希路径 + 长度。</summary>
    private const long MaxUntrackedContentBytes = 10 * 1024 * 1024;

    /// <summary>
    /// 往指纹里追加一段。带长度前缀，避免「段边界歧义」——
    /// 不然 `a` + `bc` 与 `ab` + `c` 会哈希出同一个值。
    /// </summary>
    private static void AppendSegment(IncrementalHash sha, string label, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var header = Encoding.UTF8.GetBytes(
            $"{label}:{bytes.Length.ToString(CultureInfo.InvariantCulture)}:");
        sha.AppendData(header);
        sha.AppendData(bytes);
    }

    /// <summary>
    /// 一次 <c>git status --porcelain=v2 --branch</c> 拿全：分支、HEAD、变更文件。
    ///
    /// 以前要开三个 git 进程（rev-parse --abbrev-ref HEAD、rev-parse HEAD、status --porcelain），
    /// 合成一次之后既省了两次进程启动，也只留一处「读输出」的超时逻辑。
    /// </summary>
    private static async Task<GitState> InspectAsync(string repoPath)
    {
        var result = await GitProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotePath=false", "status", "--porcelain=v2", "--branch"],
            repoPath,
            CommandTimeout,
            CancellationToken.None).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git status 失败 ({result.ExitCode}): {result.Stderr.Trim()}");
        }

        var (branch, head, paths) = ParseStatusV2(result.Stdout);

        // 仓库还没有任何提交：老实现里 `git rev-parse HEAD` 直接失败（exit 128），
        // Inspect 整个返回错误态。这里保持同样的行为，别把分支守卫的语义带着变。
        if (head == "(initial)")
        {
            throw new InvalidOperationException("git 仓库还没有任何提交，读不到 HEAD。");
        }

        return new GitState(branch, head, paths.Count > 0, paths.Count, null)
        {
            ChangedFilePaths = paths
        };
    }

    /// <summary>
    /// 解析 <c>git status --porcelain=v2 --branch</c> 的输出。
    ///
    /// 头部：<c># branch.oid &lt;sha&gt;|(initial)</c>、<c># branch.head &lt;branch&gt;|(detached)</c>。
    /// 正文：每行一个变更条目，路径在固定数量的字段之后。
    /// </summary>
    internal static (string? Branch, string? Head, IReadOnlyList<string> Paths) ParseStatusV2(string output)
    {
        string? branch = null;
        string? head = null;
        var paths = new List<string>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                {
                    head = line["# branch.oid ".Length..].Trim();
                }
                else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    branch = line["# branch.head ".Length..].Trim();
                }

                continue;
            }

            var path = ParseStatusV2Path(line);
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        // 游离 HEAD：老实现 `git rev-parse --abbrev-ref HEAD` 给的是 "HEAD"，
        // v2 给的是 "(detached)" —— 对齐老行为，分支守卫才有稳定的比较对象。
        if (branch == "(detached)")
        {
            branch = "HEAD";
        }

        return (branch, head, paths);
    }

    /// <summary>从 porcelain=v2 的一行里取文件路径。</summary>
    internal static string ParseStatusV2Path(string line)
    {
        if (line.Length < 2)
        {
            return string.Empty;
        }

        switch (line[0])
        {
            case '1':
                // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path> —— 8 个固定字段，路径是余下的部分
                return DecodeGitPath(SkipFields(line, 8));

            case '2':
                // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <Xscore> <path><TAB><原路径>
                var renamed = SkipFields(line, 9);
                var tab = renamed.IndexOf('\t');
                if (tab >= 0)
                {
                    renamed = renamed[..tab];
                }

                return DecodeGitPath(renamed);

            case 'u':
                // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                return DecodeGitPath(SkipFields(line, 10));

            case '?':
            case '!':
                // ? <path>（未跟踪）、! <path>（被忽略）
                var rest = line[1..];
                if (rest.StartsWith(" ", StringComparison.Ordinal))
                {
                    rest = rest[1..];
                }

                return DecodeGitPath(rest);

            default:
                return string.Empty;
        }
    }

    /// <summary>从 porcelain（v1）的一行里取出文件路径（处理重命名与含空格的路径）。</summary>
    internal static string ParsePorcelainPath(string line)
    {
        if (line.Length < 4)
        {
            return string.Empty;
        }

        var rest = line[3..];

        // 重命名/复制：旧路径 -> 新路径，取新路径
        var arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            rest = rest[(arrow + 4)..];
        }

        // 只去尾部。porcelain 的第一行常以空格开头（" M src/x.cs" 表示工作区已改），
        // Trim() 会把这个前导空格吃掉，第一条路径就少一个字符（src/ 变 rc/）。
        rest = rest.TrimEnd();

        // 带引号的路径是 git 用 C 风格转义过的（\346\226\207 是 UTF-8 的三个字节），
        // 不解的话中文文件名就成了一串转义符，下游按路径读文件必然找不到。
        return DecodeGitPath(rest.TrimStart());
    }

    /// <summary>
    /// 解 git 的 C 风格引号路径。
    ///
    /// git 在路径含特殊字符（非 ASCII、引号、控制符）时会把整段用双引号包起来，
    /// 里面是 C 转义：<c>\346\226\207</c> 是 UTF-8 的三个字节，<c>\"</c> 是引号本身。
    /// </summary>
    internal static string DecodeGitPath(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        var inner = path[1..^1];
        var bytes = new List<byte>(inner.Length);

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c != '\\')
            {
                if (c < 128)
                {
                    bytes.Add((byte)c);
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                }

                continue;
            }

            if (i + 1 >= inner.Length)
            {
                break;  // 结尾的孤立反斜杠，丢掉
            }

            var escape = inner[++i];
            switch (escape)
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'n': bytes.Add((byte)'\n'); break;
                case 'v': bytes.Add(0x0B); break;
                case 'f': bytes.Add(0x0C); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                default:
                    if (escape is >= '0' and <= '7')
                    {
                        // 八进制转义，最多三位（\346 是一个 UTF-8 字节）
                        var value = 0;
                        var digits = 0;
                        while (digits < 3 && i < inner.Length && inner[i] is >= '0' and <= '7')
                        {
                            value = value * 8 + (inner[i] - '0');
                            i++;
                            digits++;
                        }

                        i--;  // for 循环还会再 i++
                        bytes.Add((byte)value);
                    }
                    else
                    {
                        // 不认识的转义：原样留着
                        bytes.Add((byte)'\\');
                        bytes.Add((byte)escape);
                    }

                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>跳过前 n 个空格分隔的字段，返回余下的部分（路径）。</summary>
    private static string SkipFields(string line, int fields)
    {
        var index = 0;
        for (var i = 0; i < fields; i++)
        {
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            while (index < line.Length && line[index] != ' ')
            {
                index++;
            }
        }

        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        return index >= line.Length ? string.Empty : line[index..];
    }
}

/// <summary>
/// numstat 的一行：路径 + 增删行数（重命名时带旧路径）。
///
/// <paramref name="RawPath"/> 是 git 原样给的第三段 —— 合并时要靠它跟
/// name-status 对账（「文件名里本来就有 =&gt;」的时候猜出来的重命名是错的）。
/// </summary>
internal sealed record NumStatEntry(
    string RawPath,
    string Path,
    int LinesAdded,
    int LinesDeleted,
    string? OldPath);

/// <summary>name-status 的一行：路径 + 变更类型（重命名时带旧路径）。</summary>
internal sealed record NameStatusEntry(string Path, string ChangeKind, string? OldPath);

/// <summary>外部进程的退出码与输出。</summary>
internal sealed record GitProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// 跑外部进程（git）并取回输出。<see cref="GitWriter"/> 也用这一个实现。
///
/// 两条不能破的规矩：
/// - 不走 shell：参数用 ArgumentList 逐个传，绝不拼字符串（AGENTS.md #6/#7）；
/// - 超时是真超时：两个输出流并发读 + 到点杀整棵进程树。
///   老写法把 <c>WaitForExit(10s)</c> 放在两次同步 <c>ReadToEnd</c> 之后，那个超时根本轮不到，
///   git 一挂住（凭证提示 / 坏索引 / 网络盘）就把请求线程永久占住；
///   顺序同步读两个流还会互等死锁（git 写满 stderr 管道缓冲、而 stdout 又没关闭）。
/// - 所有 await 都 ConfigureAwait(false)：GitInspector 的同步契约要在调用方线程上
///   直接阻塞等结果，续体若回贴 SynchronizationContext（Blazor）就会互锁。
/// </summary>
internal static class GitProcessRunner
{
    internal static async Task<GitProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
                           ?? throw new InvalidOperationException($"无法启动 {fileName} 进程。");

        // 两个流必须**并发**读：顺序同步读会互等死锁
        // （git 往 stderr 写满管道缓冲、而 stdout 还没关闭时，父子互等）。
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时或调用方取消：都得把进程树杀掉。
            // git 卡住时（比如等 GPG 口令）会继续占着 .git/index.lock，
            // 之后所有 git 命令都报 "Unable to create '.git/index.lock': File exists"。
            await KillAsync(process).ConfigureAwait(false);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消 —— 原样抛出，别报成超时
                throw;
            }

            throw new TimeoutException(
                $"{fileName} {string.Join(' ', args)} 超时（{timeout.TotalSeconds:0.#} 秒），已终止进程。");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new GitProcessResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// 杀掉进程及其子进程，并回收它。
    /// 不回收的话，Unix 下会留下一堆僵尸进程。
    /// </summary>
    internal static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能刚好自己退了；杀不掉也不该把原本的异常盖掉。
        }

        try
        {
            // 回收子进程：不等它退出就等于留了个僵尸。
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // 收不回来就算了。
        }
    }

    /// <summary>把读任务收尾（最多等一会儿），免得留下没人看的任务异常。</summary>
    private static async Task DrainAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // 读任务可能以异常收尾（管道被掐断）：这里忽略，真正该报的问题在别处。
        }
    }
}
