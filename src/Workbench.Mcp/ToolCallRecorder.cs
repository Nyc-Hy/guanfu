using System.Text.Json;
using Workbench.Application;
using Workbench.Application.Contracts;
using Workbench.Application.Services;

namespace Workbench.Mcp;

/// <summary>
/// 工具调用的统一留痕。
///
/// 以前只有写工具留痕（db_execute / git_commit / git_push / shell_run / db_query），
/// 只读调用不留 —— 于是「谁访问了什么资源」查不出来，
/// 而这恰恰是企业合规最关心的那一问（谁看了生产库的数据）。
///
/// 现在改成：**所有工具调用都留痕**，由这个记录器统一写。
/// 工具自己留痕的那几个要跳过 —— 它们记得更细（审批号、影响行数、结果摘要），
/// 两边都写就会变成一条调用两行记录。
/// </summary>
public sealed class ToolCallRecorder(
    IToolAuditService audit,
    IAgentSessionAccessor? sessions = null)
{
    /// <summary>
    /// 自己留痕的工具：它们已经写了更详细的记录，中央不再重复写。
    ///
    /// 写工具的记录里有审批号、影响行数、结果摘要、SQL 原文 ——
    /// 中央那份只有工具名与参数，补不回来。
    ///
    /// fs_* 与 db_schema 也在名单里：它们各自留痕（fs 记了结果条数、db_schema 记了表数），
    /// 不跳过的话一次调用会写两行 —— 规矩 28 说的「别写成一行两条」。
    /// </summary>
    internal static readonly HashSet<string> SelfAuditedTools = new(StringComparer.Ordinal)
    {
        "db_execute", "db_query", "git_commit", "git_push", "shell_run",
        "fs_list", "fs_read", "db_schema"
    };

    /// <summary>参数里这些名字的值要抹掉 —— 免得明文落进审计表。</summary>
    internal static readonly HashSet<string> SensitiveArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "token", "apikey", "api_key", "accesstoken", "access_token"
    };

    /// <summary>留一条调用记录。失败也不影响调用本身 —— 审计写不进去不该把工具搞挂。</summary>
    public async Task RecordAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        string outcome,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = sessions is null ? null : await sessions.CurrentAsync(cancellationToken);

            var projectId = GuidFrom(arguments, "projectId") ?? session?.ProjectId;
            var taskId = GuidFrom(arguments, "taskId") ?? session?.TaskId;
            // 调用方：工具显式传的 callerId → 会话里的 agent → 请求头里的 X-Workbench-Agent。
            // 最后那一条不能少：Claude Code 这类客户端不会给工具显式传 callerId，
            // 少了它审计里就只剩一列空白。
            var callerId = StringFrom(arguments, "callerId")
                ?? session?.AgentId
                ?? sessions?.CurrentAgentId;

            await audit.RecordAsync(new ToolAuditEntry(
                toolName,
                callerId,
                projectId,
                taskId,
                Redact(arguments),
                RequiredApproval: false,
                ApprovalId: null,
                Outcome: outcome,
                ResultSummary: null,
                RowsAffected: null,
                DurationMs: durationMs), cancellationToken);
        }
        catch (Exception)
        {
            // 审计写不进去不该把调用搞挂 —— 调用已经发生了，
            // 这次留痕失败就当没留上，不能反过来让 agent 以为调用失败了。
        }
    }

    /// <summary>
    /// 参数拼成 JSON 存进审计表，敏感字段的值抹成 ***。
    ///
    /// 现在还没有带密码的工具，但这条规则得先立着 ——
    /// 下一个带密钥的工具上来时，审计表里不会已经躺着明文。
    /// </summary>
    internal static string Redact(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var buffer = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);

        foreach (var (key, value) in arguments)
        {
            buffer[key] = SensitiveArgumentNames.Contains(key)
                ? "***"
                : (JsonValueKind.String == value.ValueKind ? value.GetString() : value.ToString());
        }

        return JsonSerializer.Serialize(buffer, WorkbenchJson.Options);
    }

    private static Guid? GuidFrom(IDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? StringFrom(IDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
