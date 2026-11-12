using System.Diagnostics;
using System.Text;
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
                "该项目没有配置启动命令（AgentCommand）。");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(project.RepoPath)
            ? options.Value.HomeDirectory
            : Path.GetFullPath(project.RepoPath);

        if (!Directory.Exists(workingDirectory))
        {
            return new AgentLaunchResult(false, null, string.Empty, workingDirectory,
                $"工作目录不存在: {workingDirectory}");
        }

        var command = IAgentLauncher.ExpandTemplate(project.AgentCommand, new Dictionary<string, string>
        {
            ["taskId"] = task.Id.ToString(),
            ["title"] = task.Title,
            ["project"] = project.Name,
            ["repoPath"] = workingDirectory,
            ["description"] = task.Description ?? string.Empty
        });

        var tokens = IGitWriter.Tokenize(command);
        if (tokens.Count == 0)
        {
            return new AgentLaunchResult(false, null, string.Empty, workingDirectory, "启动命令为空。");
        }

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

        // 输出落到 WORKBENCH_HOME/runs/{taskId}/，任务详情页可以回看
        var runDir = Path.Combine(options.Value.HomeDirectory, "runs", taskId.ToString("N"));
        Directory.CreateDirectory(runDir);
        var logPath = Path.Combine(runDir, $"{clock.UtcNow:yyyyMMdd-HHmmss}.log");

        var stdoutTask = PumpAsync(process.StandardOutput, logPath, "stdout");
        var stderrTask = PumpAsync(process.StandardError, logPath, "stderr");

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            FromStatus = task.Status,
            ToStatus = task.Status,
            Source = TaskEventSource.Human,
            Actor = "launcher",
            Note = $"已拉起外部 agent（PID {process.Id}）：{command}",
            CreatedAt = clock.UtcNow
        });

        task.UpdatedAt = clock.UtcNow;
        task.LastActivityAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _ = WatchAsync(process.Id, taskId, logPath, stdoutTask, stderrTask, scopeFactory);

        return new AgentLaunchResult(true, process.Id, command, workingDirectory, null);
    }

    private static async Task PumpAsync(StreamReader reader, string logPath, string tag)
    {
        try
        {
            await using var writer = new StreamWriter(logPath, append: true, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                await writer.WriteLineAsync($"[{tag}] {line}");
                await writer.FlushAsync();
            }
        }
        catch (Exception)
        {
            // 进程被杀时管道会断，忽略
        }
    }

    /// <summary>进程结束后补一条事件。用独立 scope，因为发起请求的 scope 早就结束了。</summary>
    private static async Task WatchAsync(
        int processId,
        Guid taskId,
        string logPath,
        Task stdoutTask,
        Task stderrTask,
        IServiceScopeFactory scopeFactory)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync();
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
                FromStatus = task.Status,
                ToStatus = task.Status,
                Source = TaskEventSource.System,
                Actor = "launcher",
                Note = $"外部 agent 进程 {processId} 已退出。日志: {logPath}",
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

    private static string ReadTail(string path, int maxChars = 8000)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(path);
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
