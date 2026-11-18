namespace Guanfu.Application.Services;

public sealed record AgentLaunchResult(
    bool Ok,
    int? ProcessId,
    string Command,
    string WorkingDirectory,
    string? Error);

/// <summary>
/// 拉起外部 agent 干活。
///
/// 边界：观复**不提供 AI 服务**，这里只是执行用户配置好的外部命令
/// （比如 `claude -p "..."`），agent 的智能仍在外部进程里。
/// 命令模板里的占位符：{taskId} {title} {project} {repoPath} {description}。
/// </summary>
public interface IAgentLauncher
{
    Task<AgentLaunchResult> LaunchAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把模板与任务信息合并成最终命令行。
    ///
    /// **只给调试用**。真正起进程的时候不要用这个 ——
    /// 它是裸字符串替换，值里的空格会被后面的分词当成参数分隔符，
    /// 标题写成「修复登录 --dangerously-skip-permissions」就能塞进额外参数。
    /// 起进程请用 <see cref="ExpandArguments"/>。
    /// </summary>
    static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// 把命令模板展开成 argv（可执行文件 + 参数）。
    ///
    /// 顺序很关键：**先分词，再替换**。
    /// 先替换再分词的话，值里的空格会被当成参数分隔符 ——
    /// 标题「修复登录 --dangerously-skip-permissions」就会变成两个参数。
    /// 先分词的话，占位符本身是一个完整 token，替换时整体塞进去，
    /// 值再怎么怪也只占一个 argv 位置。
    /// </summary>
    static IReadOnlyList<string> ExpandArguments(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
        var tokens = IGitWriter.Tokenize(template);
        var result = new List<string>(tokens.Count);

        foreach (var token in tokens)
        {
            result.Add(ExpandTemplate(token, values));
        }

        return result;
    }
}
