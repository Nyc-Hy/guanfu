using Workbench.Application.Services;

namespace Workbench.Infrastructure;

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

        return await RunAsync(repoPath, cancellationToken, "commit", "-m", message);
    }

    public async Task<GitCommandResult> PushAsync(
        string repoPath,
        string? branch = null,
        string? remote = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildPushArgs(branch, remote);
        return await RunAsync(repoPath, cancellationToken, args.ToArray());
    }

    /// <summary>
    /// 拼 git push 参数。
    ///
    /// git 的语法是 <c>push [&lt;remote&gt; [&lt;refspec&gt;]]</c> —— 只给一个位置参数时，
    /// git 把它当**远端名**。所以「只给分支不给远端」必须补上 origin，
    /// 否则会变成 <c>git push main</c>，git 去找一个叫 main 的远端然后失败。
    /// </summary>
    public static string[] BuildPushArgs(string? branch, string? remote)
    {
        var args = new List<string> { "push" };

        var effectiveRemote = string.IsNullOrWhiteSpace(remote) ? null : remote.Trim();

        if (effectiveRemote is null && !string.IsNullOrWhiteSpace(branch))
        {
            effectiveRemote = "origin";
        }

        if (effectiveRemote is not null)
        {
            args.Add(effectiveRemote);
        }

        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add(branch.Trim());
        }

        return args.ToArray();
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
