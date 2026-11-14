using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Infrastructure;

/// <summary>
/// 拉起外部 agent。
///
/// 工作台不提供 AI 服务 —— 这里只是执行用户在项目配置里写好的外部命令，
/// 智能仍在外部进程里。命令模板支持占位符：
/// {taskId} {title} {project} {repoPath} {description}。
/// </summary>
public sealed class AgentLauncher(
    IAppDbContext db,
    IClock clock,
    IServiceScopeFactory scopeFactory,
    IOptions<WorkbenchOptions> options,
    IBranchGuard branchGuard) : IAgentLauncher
{
    /// <summary>
    /// 外部 agent 最长跑多久。agent 跑歪了不会自己停 ——
    /// 没有上限的话，一个卡住的进程能一直占着机器。
    /// </summary>
    private static readonly TimeSpan MaxRuntime = TimeSpan.FromHours(4);

    public async Task<AgentLaunchResult> LaunchAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
                   ?? throw new NotFoundException("任务", taskId);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken)
                      ?? throw new NotFoundException("项目", task.ProjectId);

        // 分支守卫：绑了分支的任务，只有仓库当前就在那个分支上才允许拉起 agent
        var branch = await branchGuard.CheckAsync(taskId, cancellationToken);
        if (!branch.Ok)
        {
            return new AgentLaunchResult(false, null, string.Empty, project.RepoPath ?? string.Empty,
                branch.Error ?? "当前分支与任务绑定的分支不一致，不予启动。");
        }

        if (string.IsNullOrWhiteSpace(project.AgentCommand))
        {
            return new AgentLaunchResult(false, null, string.Empty, string.Empty,
                "这个项目还没配启动命令。到项目的「设置 → 启动 agent」里填一条" +
                "（比如 claude -p \"{title}\"），再回来启动。");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(project.RepoPath)
            ? options.Value.HomeDirectory
            : Path.GetFullPath(project.RepoPath);

        if (!Directory.Exists(workingDirectory))
        {
            return new AgentLaunchResult(false, null, string.Empty, workingDirectory,
                $"工作目录不存在：{workingDirectory} —— 检查一下项目设置里的仓库路径。");
        }

        var values = new Dictionary<string, string>
        {
            ["taskId"] = task.Id.ToString(),
            ["title"] = task.Title,
            ["project"] = project.Name,
            ["repoPath"] = workingDirectory,
            ["description"] = task.Description ?? string.Empty
        };

        // 先分词再替换 —— 标题里的空格不能变成额外的 argv 参数
        var tokens = IAgentLauncher.ExpandArguments(project.AgentCommand, values);
        if (tokens.Count == 0)
        {
            return new AgentLaunchResult(false, null, string.Empty, workingDirectory, "启动命令为空。");
        }

        var command = string.Join(' ', tokens);

        var startInfo = new ProcessStartInfo
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

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"无法启动进程: {tokens[0]}");
        }
        catch (Exception ex)
        {
            return new AgentLaunchResult(false, null, command, workingDirectory, ex.Message);
        }

        // 输出落到 WORKBENCH_HOME/runs/{taskId}/，任务详情页可以回看。
        // 文件名带毫秒 —— 同秒启动两次会写同一个文件，日志交错。
        var runDir = Path.Combine(options.Value.HomeDirectory, "runs", taskId.ToString("N"));
        Directory.CreateDirectory(runDir);
        var logPath = Path.Combine(runDir, $"{clock.UtcNow:yyyyMMdd-HHmmss-fff}.log");

        // 单 writer：两个 reader 泵入 Channel，由一个消费循环写盘。
        // 以前是两路各开一个 StreamWriter(append) 写同一文件 —— Windows 上
        // 第二个 StreamWriter 直接抛 IOException 被吞掉，该路输出全丢；
        // 管道没人读，写满后子进程阻塞在 write 上，整个 agent 死锁。
        var logChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var stdoutTask = PumpAsync(process.StandardOutput, "stdout", logChannel.Writer);
        var stderrTask = PumpAsync(process.StandardError, "stderr", logChannel.Writer);
        var logTask = WriteLogAsync(logChannel.Reader, logPath);

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            // 这不是状态迁移 —— from/to 留空
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Human,
            Actor = "launcher",
            Note = $"已拉起外部 agent（PID {process.Id}）：{command}",
            EventType = TaskEventTypes.AgentLaunched,
            Payload = WorkbenchJson.Serialize(new
            {
                pid = process.Id,
                command = Truncate(command, 4000)
            }),
            CreatedAt = clock.UtcNow
        });

        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _ = WatchAsync(process, taskId, logPath, stdoutTask, stderrTask, logChannel.Writer, logTask, scopeFactory);

        return new AgentLaunchResult(true, process.Id, command, workingDirectory, null);
    }

    /// <summary>把一路输出泵入 channel（不直接写盘 —— 写盘只有 WriteLogAsync 一个 writer）。</summary>
    private static async Task PumpAsync(StreamReader reader, string tag, ChannelWriter<string> writer)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await writer.WriteAsync($"[{tag}] {line}");
            }
        }
        catch (Exception)
        {
            // 进程被杀时管道会断，忽略
        }
    }

    /// <summary>
    /// 唯一的日志 writer。进程退出、两路 reader 都收尾后，channel 被 Complete，
    /// 这里把剩余内容 drain 完才关文件 —— 保证退出事件里读到的日志尾部是完整的。
    /// </summary>
    private static async Task WriteLogAsync(ChannelReader<string> reader, string logPath)
    {
        try
        {
            await using var writer = new StreamWriter(logPath, append: true, Encoding.UTF8);
            await foreach (var line in reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        }
        catch (Exception)
        {
            // 日志写失败（磁盘满等）不能影响 agent 本身
        }
    }

    /// <summary>Payload 有列宽上限（16000），命令可能很长 —— 截断到安全长度。</summary>
    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    /// <summary>进程结束后补一条事件。用独立 scope，因为发起请求的 scope 早就结束了。</summary>
    private static async Task WatchAsync(
        Process process,
        Guid taskId,
        string logPath,
        Task stdoutTask,
        Task stderrTask,
        ChannelWriter<string> logWriter,
        Task logTask,
        IServiceScopeFactory scopeFactory)
    {
        var processId = process.Id;
        int? exitCode = null;

        try
        {
            // 兜底：agent 永远不退出的话，不能一直占着机器。
            using var timeout = new CancellationTokenSource(MaxRuntime);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        // 等它真的退出，下面才拿得到 ExitCode
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception)
                {
                    // 进程可能已经退出
                }
            }

            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                // 进程句柄失效等极端情况：退出码拿不到就留空
            }
        }
        catch (Exception)
        {
            // 进程可能已经退出或被系统回收
        }

        try
        {
            await stdoutTask;
            await stderrTask;
        }
        catch (Exception)
        {
            // 忽略
        }
        finally
        {
            // 两路 reader 都收尾了才关 channel；等唯一 writer 把剩余日志 drain 完，
            // 之后 ReadTail 读到的才是完整日志。
            logWriter.TryComplete();
            try
            {
                await logTask;
            }
            catch (Exception)
            {
                // 忽略
            }

            // 进程句柄用完就放掉
            process.Dispose();
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkbenchDbContext>();

            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
            if (task is null)
            {
                return;
            }

            var tail = ReadTail(logPath);

            db.StatusEvents.Add(new StatusEvent
            {
                TaskId = taskId,
                // 这不是状态迁移 —— from/to 留空
                FromStatus = null,
                ToStatus = null,
                Source = TaskEventSource.System,
                Actor = "launcher",
                Note = $"外部 agent 进程 {processId} 已退出。日志: {logPath}",
                EventType = TaskEventTypes.AgentExited,
                Payload = WorkbenchJson.Serialize(new { exitCode }),
                CreatedAt = DateTimeOffset.UtcNow
            });

            db.Artifacts.Add(new Artifact
            {
                TaskId = taskId,
                Type = ArtifactType.Other,
                Title = $"agent 运行日志 {processId}",
                Content = tail,
                ExternalPath = logPath,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync();
        }
        catch (Exception)
        {
            // 记录失败也不能影响主流程
        }
    }

    /// <summary>
    /// 读日志尾部。
    ///
    /// 不能用 File.ReadAllText —— agent 跑久了日志能到 GB 级，
    /// 整个读进内存就是内存炸弹。这里只 seek 到尾部读最后几 KB。
    /// </summary>
    private static string ReadTail(string path, int maxChars = 8000)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length <= maxChars)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }

            stream.Seek(-maxChars, SeekOrigin.End);
            using var tailReader = new StreamReader(stream, Encoding.UTF8);
            return tailReader.ReadToEnd();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
