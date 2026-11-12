using System.Diagnostics;
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
            // Inspect 是同步契约，底下是异步的（两个输出流必须并发读，见 GitProcessRunner）。
            // 丢到线程池上跑再取结果：直接在当前线程上阻塞等待，
            // 在带 SynchronizationContext 的线程（Blazor 渲染线程）上有互锁风险。
            return Task.Run(() => InspectAsync(repoPath)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new GitState(null, null, false, 0, ex.Message);
        }
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
            CancellationToken.None);

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
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时或调用方取消：都得把进程树杀掉。
            // git 卡住时（比如等 GPG 口令）会继续占着 .git/index.lock，
            // 之后所有 git 命令都报 "Unable to create '.git/index.lock': File exists"。
            await KillAsync(process);
            await DrainAsync(stdoutTask, stderrTask);

            if (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消 —— 原样抛出，别报成超时
                throw;
            }

            throw new TimeoutException(
                $"{fileName} {string.Join(' ', args)} 超时（{timeout.TotalSeconds:0.#} 秒），已终止进程。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

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
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 读任务可能以异常收尾（管道被掐断）：这里忽略，真正该报的问题在别处。
        }
    }
}
