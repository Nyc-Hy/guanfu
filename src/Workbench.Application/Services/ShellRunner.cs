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
    /// <summary>
    /// stdout / stderr 各自最多保留的字节数（1 MB）。
    ///
    /// 以前是 ReadToEndAsync：一条 `yes` 或刷屏的构建日志就能把宿主内存吃光。
    /// 现在超过上限的部分**照样读、但丢弃** —— 不读的话管道缓冲会被写满，
    /// 子进程阻塞在 write 上永远退不出来，最后只能等 10 分钟超时。
    /// </summary>
    internal const int OutputCapBytes = 1024 * 1024;

    /// <summary>截断标记。调用方（和看审计的人）必须能看出输出不全。</summary>
    internal const string TruncationMarker = "\n…[输出超过 1 MB，已截断，后续内容丢弃]…\n";

    /// <summary>命令执行上限。</summary>
    internal static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

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

        // 两个流必须**并发**读，而且各自带上限：顺序读会互等死锁，无上限读会吃光内存。
        var stdoutTask = ReadCappedAsync(process.StandardOutput, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时或调用方取消：都得杀进程树，并把两个读任务收尾。
            // 不收尾的话，被掐断的管道会让读任务以异常结束，留下没人观察的 Task 异常。
            await KillQuietlyAsync(process);
            await DrainAsync(stdoutTask, stderrTask);

            if (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消 —— 不是超时，别把取消说成超时
                throw;
            }

            throw new WorkbenchException("命令执行超时（10 分钟），已终止。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ShellResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// 读一个流，最多保留 <see cref="OutputCapBytes"/> 字节，超出部分丢弃并打上截断标记。
    /// 用 BaseStream 按字节读，解码沿用进程自己的输出编码，避免把多字节字符切成乱码。
    /// </summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var stream = reader.BaseStream;
        var encoding = reader.CurrentEncoding;
        var buffer = new byte[8192];
        using var kept = new MemoryStream();
        var truncated = false;

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            }
            catch (IOException)
            {
                // 进程被杀 / 管道被掐断：拿到多少算多少
                break;
            }

            if (read == 0)
            {
                break;
            }

            if (kept.Length < OutputCapBytes)
            {
                var take = (int)Math.Min(read, OutputCapBytes - kept.Length);
                kept.Write(buffer, 0, take);
                truncated |= take < read;
            }
            else
            {
                // 已经满了：继续读并丢弃，只为让子进程能写完退出
                truncated = true;
            }
        }

        var text = encoding.GetString(kept.ToArray());
        return truncated ? text + TruncationMarker : text;
    }

    /// <summary>把读任务收尾（最多等一会儿），免得留下没人看的任务异常。</summary>
    private static async Task DrainAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 读任务可能以异常收尾（管道被掐断）：这里忽略，真正该报的问题在别处。
        }
    }

    /// <summary>杀掉进程树并回收。进程可能已经退出，失败就算了。</summary>
    private static async Task KillQuietlyAsync(Process process)
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

        try
        {
            // 不回收的话 Unix 下会留下僵尸进程
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 收不回来就算了
        }
    }
}
