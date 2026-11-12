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

public sealed class ShellRunner : IShellRunner
{
    public bool IsAllowed(string? allowedCommandsJson, string command) =>
        IsAllowed(ParseAllowed(allowedCommandsJson), command);

    public bool IsAllowed(IReadOnlyList<string> allowedCommands, string command)
    {
        if (allowedCommands.Count == 0)
        {
            return false;
        }

        var normalized = command.Trim();
        return allowedCommands.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase));
    }

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
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已经退出
            }

            throw new WorkbenchException("命令执行超时（10 分钟），已终止。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ShellResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
    }
}
