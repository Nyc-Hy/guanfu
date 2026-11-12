using System.Diagnostics;
using System.Text.Json;
using Workbench.Domain;

namespace Workbench.Application.Services;

public sealed record ShellResult(bool Ok, int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// 执行白名单内的命令。
///
/// 两条硬约束：
/// 1. 必须命中项目白名单（可执行文件 + 前缀）；
/// 2. 不走 shell —— 命令被拆成可执行文件 + 参数直接 exec，杜绝 `;`、`&`、`|` 之类的拼接注入。
/// </summary>
public interface IShellRunner
{
    /// <summary>判断命令是否命中项目白名单（白名单以 JSON 数组存储在项目配置里）。</summary>
    bool IsAllowed(string? allowedCommandsJson, string command);

    /// <summary>判断命令是否命中项目白名单。</summary>
    bool IsAllowed(IReadOnlyList<string> allowedCommands, string command);

    Task<ShellResult> RunAsync(
        string workingDirectory,
        string command,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// shell 白名单的判定规则。
///
/// 早先的实现是**命令串前缀匹配**（`normalized.StartsWith(prefix + " ")`），
/// 粒度太粗：白名单里写一个 "git" 就等于放行 `git push --force`、`git reset --hard`，
/// 写一个 "npm" 就等于放行 `npm publish`。
///
/// 现在的口径是**按词比较的前缀**：
/// - 条目 "git"        → 放行所有 git 子命令（用户明确勾了整个 git，语义不变）；
/// - 条目 "git status" → 只放行 `git status` 与 `git status --porcelain`；
///   `git status-tree` 不放行（词边界不同）；
/// - 条目 "npm run"    → 只放行 `npm run ...`，`npm publish` 不放行。
///
/// 想要更细的权限，就在白名单里写子命令级别的前缀，而不是靠前缀匹配去猜。
/// </summary>
public static class ShellCommandPolicy
{
    /// <summary>命令是否命中白名单。</summary>
    public static bool Matches(IReadOnlyList<string>? allowedCommands, string command)
    {
        if (allowedCommands is null || allowedCommands.Count == 0)
        {
            return false;
        }

        var tokens = IGitWriter.Tokenize(command.Trim());
        if (tokens.Count == 0)
        {
            return false;
        }

        return allowedCommands.Any(entry => MatchesEntry(entry, tokens));
    }

    private static bool MatchesEntry(string entry, IReadOnlyList<string> tokens)
    {
        var entryTokens = IGitWriter.Tokenize(entry.Trim());
        if (entryTokens.Count == 0 || entryTokens.Count > tokens.Count)
        {
            return false;
        }

        for (var i = 0; i < entryTokens.Count; i++)
        {
            if (!string.Equals(entryTokens[i], tokens[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class ShellRunner : IShellRunner
{
    public bool IsAllowed(string? allowedCommandsJson, string command) =>
        IsAllowed(ParseAllowed(allowedCommandsJson), command);

    public bool IsAllowed(IReadOnlyList<string> allowedCommands, string command) =>
        ShellCommandPolicy.Matches(allowedCommands, command);

    public static IReadOnlyList<string> ParseAllowed(string? allowedCommandsJson)
    {
        if (string.IsNullOrWhiteSpace(allowedCommandsJson))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(allowedCommandsJson);
            return parsed?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<ShellResult> RunAsync(
        string workingDirectory,
        string command,
        CancellationToken cancellationToken = default)
    {
        var tokens = IGitWriter.Tokenize(command);
        if (tokens.Count == 0)
        {
            throw new WorkbenchException("命令为空。");
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = tokens[0],
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in tokens.Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
                           ?? throw new WorkbenchException($"无法启动进程: {tokens[0]}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消 —— 不是超时，别把取消说成超时
            KillQuietly(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw new WorkbenchException("命令执行超时（10 分钟），已终止。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ShellResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
    }

    /// <summary>杀掉进程树。进程可能已经退出，失败就算了。</summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能已经退出
        }
    }
}
