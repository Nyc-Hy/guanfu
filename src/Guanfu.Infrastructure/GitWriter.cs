using Guanfu.Application.Services;

namespace Guanfu.Infrastructure;

/// <summary>Git 写操作实现。命令不走 shell，参数用 ArgumentList 传。</summary>
public sealed class GitWriter : IGitWriter
{
    /// <summary>
    /// 写操作的超时上限。commit 可能要等 GPG 口令、push 可能要等凭证，
    /// 但不能无限期挂着 —— 挂着就会占着 .git/index.lock。
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public async Task<GitCommandResult> CommitAsync(string repoPath, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitCommandResult(false, string.Empty, "提交信息不能为空。");
        }

        var add = await RunAsync(repoPath, cancellationToken, "add", "-A");
        if (!add.Ok)
        {
            return add;
        }

        // message 以 - 开头也安全：它是 `-m` 这个选项的**值**，
        // ArgumentList 把它作为独立 argv 传入，git 的 parse-options 会无条件
        // 把 -m 后面的那个参数当值消费，不会再解析成选项（有回归测试钉住）。
        return await RunAsync(repoPath, cancellationToken, "commit", "-m", message);
    }

    public async Task<GitCommandResult> PushAsync(
        string repoPath,
        string? branch = null,
        string? remote = null,
        CancellationToken cancellationToken = default)
    {
        string[] args;
        try
        {
            args = BuildPushArgs(branch, remote);
        }
        catch (ArgumentException ex)
        {
            // 参数注入（--force / --delete / ext::sh -c …）在拼参数时就拒掉，
            // 返回结构化错误而不是抛异常 —— 上层 MCP 工具要把原因带回给 agent。
            return new GitCommandResult(false, string.Empty, ex.Message);
        }

        return await RunAsync(repoPath, cancellationToken, args);
    }

    /// <summary>
    /// 拼 git push 参数。
    ///
    /// git 的语法是 <c>push [&lt;remote&gt; [&lt;refspec&gt;]]</c> —— 只给一个位置参数时，
    /// git 把它当**远端名**。所以「只给分支不给远端」必须补上 origin，
    /// 否则会变成 <c>git push main</c>，git 去找一个叫 main 的远端然后失败。
    ///
    /// branch/remote 来自外部（agent 经 MCP 传入），直入 argv 就是注入面：
    /// <c>--force</c> / <c>--delete</c> 会被当选项，<c>ext::sh -c …</c> 会被当
    /// 远端 URL 触发外部传输协议（等于执行任意命令）。所以先校验再拼，
    /// 并且 refspec 前加 <c>--</c> 与选项隔开（<c>git push &lt;remote&gt; -- &lt;branch&gt;</c>
    /// 是 git 支持的合法语法）。校验不过抛 <see cref="ArgumentException"/>。
    /// </summary>
    public static string[] BuildPushArgs(string? branch, string? remote)
    {
        // 不做 Trim：首尾的空白/控制字符也是注入面（"origin\n" 这种），
        // 静默修剪等于替调用方改参数 —— 一律交给下面的校验拒掉。
        // 全空白视为「没给」，与 null 同义。
        var effectiveRemote = string.IsNullOrWhiteSpace(remote) ? null : remote;
        var effectiveBranch = string.IsNullOrWhiteSpace(branch) ? null : branch;

        if (effectiveRemote is null && effectiveBranch is not null)
        {
            effectiveRemote = "origin";
        }

        if (effectiveRemote is not null)
        {
            ValidateRemote(effectiveRemote);
        }

        if (effectiveBranch is not null)
        {
            ValidateBranch(effectiveBranch);
        }

        var args = new List<string> { "push" };

        if (effectiveRemote is not null)
        {
            args.Add(effectiveRemote);
        }

        if (effectiveBranch is not null)
        {
            args.Add("--");
            args.Add(effectiveBranch);
        }

        return args.ToArray();
    }

    /// <summary>
    /// 远端只允许**远端名**（字母数字与 . _ / -）。
    /// 冒号一律拒：URL（https://…）与 ext:: 传输协议都含冒号，
    /// 空白与控制字符也拒 —— 远端名里出现这些只可能是注入。
    /// </summary>
    private static void ValidateRemote(string remote)
    {
        if (remote.StartsWith('-'))
        {
            throw new ArgumentException($"远端名不能以 - 开头（会被 git 当成选项）: {remote}");
        }

        foreach (var c in remote)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '/' or '-'))
            {
                throw new ArgumentException(
                    $"远端名只允许字母数字与 . _ / -（请使用项目配置好的远端名，不接受 URL）: {remote}");
            }
        }
    }

    /// <summary>
    /// 分支/refspec 校验：拒选项注入（- 开头）、ext:: 协议（含 ::）、
    /// 空白与控制字符；也拒 + 开头（强制推送 refspec，等价 --force）
    /// 与 : 开头（删除 refspec，等价 --delete）—— 这两类危险动作不能藏在 refspec 里绕过审批语义。
    /// 单个冒号的正常 refspec（如 main:refs/heads/main）放行。
    /// </summary>
    private static void ValidateBranch(string branch)
    {
        if (branch.StartsWith('-'))
        {
            throw new ArgumentException($"分支名不能以 - 开头（会被 git 当成选项）: {branch}");
        }

        if (branch.StartsWith('+'))
        {
            throw new ArgumentException($"分支名不能以 + 开头（那是强制推送 refspec）: {branch}");
        }

        if (branch.StartsWith(':'))
        {
            throw new ArgumentException($"分支名不能以 : 开头（那是删除远端分支的 refspec）: {branch}");
        }

        if (branch.Contains("::", StringComparison.Ordinal))
        {
            throw new ArgumentException($"分支名不能含 ::（ext:: 等传输协议会执行外部命令）: {branch}");
        }

        foreach (var c in branch)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException($"分支名不能含空白或控制字符: {branch}");
            }
        }
    }

    private static async Task<GitCommandResult> RunAsync(
        string repoPath,
        CancellationToken cancellationToken,
        params string[] args)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitCommandResult(false, string.Empty, $"仓库路径不存在: {repoPath}");
        }

        // git 命令一律用 -C 进仓库，不依赖进程自己的工作目录
        var fullArgs = new List<string> { "-C", repoPath };
        fullArgs.AddRange(args);

        try
        {
            // 超时与杀进程树都在 GitProcessRunner 里：
            // git 挂住时必须把它杀掉，否则子进程会一直占着 .git/index.lock，
            // 之后所有 git 命令都报 "Unable to create '.git/index.lock': File exists"。
            var result = await GitProcessRunner.RunAsync(
                "git", fullArgs, repoPath, CommandTimeout, cancellationToken);

            return result.ExitCode == 0
                ? new GitCommandResult(true, result.Stdout.Trim(), null)
                : new GitCommandResult(false, result.Stdout.Trim(), result.Stderr.Trim());
        }
        catch (TimeoutException)
        {
            // 自己等的超时（调用方没取消）：进程树已经被杀掉了
            return new GitCommandResult(false, string.Empty, "git 命令超时（5 分钟）。");
        }
        catch (OperationCanceledException)
        {
            // 调用方主动取消 —— 原样抛出，不该报成超时
            throw;
        }
        catch (Exception ex)
        {
            return new GitCommandResult(false, string.Empty, ex.Message);
        }
    }
}
