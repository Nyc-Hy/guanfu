using System.Diagnostics;
using Workbench.Application.Services;

namespace Workbench.Infrastructure;

/// <summary>Git 写操作实现。命令不走 shell，参数用 ArgumentList 传。</summary>
public sealed class GitWriter : IGitWriter
{
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

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repoPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo)
                               ?? throw new InvalidOperationException("无法启动 git 进程。");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            await process.WaitForExitAsync(timeout.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return process.ExitCode == 0
                ? new GitCommandResult(true, stdout.Trim(), null)
                : new GitCommandResult(false, stdout.Trim(), stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            return new GitCommandResult(false, string.Empty, "git 命令超时（5 分钟）。");
        }
        catch (Exception ex)
        {
            return new GitCommandResult(false, string.Empty, ex.Message);
        }
    }
}
