using System.Diagnostics;
using Workbench.Application.Services;

namespace Workbench.Infrastructure;

/// <summary>
/// 只读 Git 检视，走本地 git CLI。
/// 刻意不提供任何写操作——commit/push 必须经过审批流程，由上层服务调用。
/// </summary>
public sealed class GitInspector : IGitInspector
{
    public GitState Inspect(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitState(null, null, false, 0, $"仓库路径不存在: {repoPath}");
        }

        try
        {
            var branch = RunGit(repoPath, "rev-parse", "--abbrev-ref", "HEAD");
            var head = RunGit(repoPath, "rev-parse", "HEAD");
            var status = RunGit(repoPath, "status", "--porcelain");

            var changed = string.IsNullOrWhiteSpace(status)
                ? 0
                : status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            return new GitState(branch, head, changed > 0, changed, null);
        }
        catch (Exception ex)
        {
            return new GitState(null, null, false, 0, ex.Message);
        }
    }

    private static string RunGit(string repoPath, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
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
                           ?? throw new InvalidOperationException("无法启动 git 进程。");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} 失败 ({process.ExitCode}): {stderr.Trim()}");
        }

        return stdout.Trim();
    }
}
