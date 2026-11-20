using System.Text.Json;
using Guanfu.Application;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Microsoft.EntityFrameworkCore;

namespace Guanfu.Mcp;

/// <summary>
/// 工具调用的统一留痕。
///
/// 数据库工具单独记录 SQL、审批和执行结果，
/// 只读调用不留 —— 于是「谁访问了什么资源」查不出来，
/// 而这恰恰是企业合规最关心的那一问（谁看了生产库的数据）。
///
/// 现在改成：**所有工具调用都留痕**，由这个记录器统一写。
/// 工具自己留痕的那几个要跳过 —— 它们记得更细（审批号、影响行数、结果摘要），
/// 两边都写就会变成一条调用两行记录。
/// </summary>
public sealed class ToolCallRecorder(
    IToolAuditService audit,
    IAgentSessionAccessor? sessions = null,
    IAppDbContext? db = null)
{
    /// <summary>
    /// 自己留痕的工具：它们已经写了更详细的记录，中央不再重复写。
    ///
    /// 写工具的记录里有审批号、影响行数、结果摘要、SQL 原文 ——
    /// 中央那份只有工具名与参数，补不回来。
    ///
    /// db_schema 自己记录返回的表数；中央记录器同样跳过。
    /// </summary>
    internal static readonly HashSet<string> SelfAuditedTools = new(StringComparer.Ordinal)
    {
        "db_execute", "db_query", "db_schema"
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
            if (projectId is null && taskId is Guid task && db is not null)
            {
                projectId = await db.Tasks.Where(t => t.Id == task)
                    .Select(t => (Guid?)t.ProjectId).SingleOrDefaultAsync(cancellationToken);
            }

            var credential = sessions?.CurrentCredential;
            if (projectId is null && credential is { ProjectScope.Count: 1 })
            {
                projectId = credential.ProjectScope[0];
            }

            // agent 的名字以凭证为准。owner 与兼容入口仍可显式填写调用方。
            var callerId = credential is { Kind: CredentialKind.Agent }
                ? credential.Subject
                : StringFrom(arguments, "callerId") ?? session?.AgentId ?? sessions?.CurrentAgentId;

            // 这把钥匙背后的人（成员显示名）。
            // agent 自报的名字不算数 —— 「是谁」得由签发凭证时绑定的成员说了算。
            var memberName = credential?.MemberName;

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
                DurationMs: durationMs,
                MemberName: memberName), cancellationToken);
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

        return JsonSerializer.Serialize(buffer, GuanfuJson.Options);
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
