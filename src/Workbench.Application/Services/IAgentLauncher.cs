namespace Workbench.Application.Services;

public sealed record AgentLaunchResult(
    bool Ok,
    int? ProcessId,
    string Command,
    string WorkingDirectory,
    string? Error);

/// <summary>
/// 拉起外部 agent 干活。
///
/// 边界：工作台**不提供 AI 服务**，这里只是执行用户配置好的外部命令
/// （比如 `claude -p "..."`），agent 的智能仍在外部进程里。
/// 命令模板里的占位符：{taskId} {title} {project} {repoPath} {description}。
/// </summary>
public interface IAgentLauncher
{
    Task<AgentLaunchResult> LaunchAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>把模板与任务信息合并成最终命令行。</summary>
    static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }

        return result;
    }
}
