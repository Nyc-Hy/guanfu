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

            var lines = string.IsNullOrWhiteSpace(status)
                ? []
                : status.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var changed = lines.Length;

            // porcelain 格式：XY 路径。路径含空格时要按后两列切
            var paths = lines
                .Select(ParsePorcelainPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            return new GitState(branch, head, changed > 0, changed, null) { ChangedFilePaths = paths };
        }
        catch (Exception ex)
        {
            return new GitState(null, null, false, 0, ex.Message);
        }
    }

    /// <summary>从 porcelain 的一行里取出文件路径（处理重命名与含空格的路径）。</summary>
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

        // 带引号的路径是特殊字符转义过的，去掉引号
        rest = rest.Trim();
        if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
        {
            rest = rest[1..^1];
        }

        return rest;
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

        // 只去尾部。porcelain 的第一行常以空格开头（" M src/x.cs" 表示工作区已改），
        // Trim() 会把这个前导空格吃掉，第一条路径就少一个字符（src/ 变 rc/）。
        return stdout.TrimEnd();
    }
}
