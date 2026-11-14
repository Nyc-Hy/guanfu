using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Application;
using Workbench.Infrastructure;
using Workbench.Mcp;
using Workbench.Web;
using Workbench.Web.Api;

namespace Workbench.Tests;

/// <summary>
/// 端点对账：把 WorkbenchApi 里**每一个写端点**都拉出来，
/// 逐个断言它要么被 HumanTokenPolicy 判为敏感（要人工凭证），
/// 要么落在下面这份「明确开放」清单里（agent / CI 的正常工作面）。
///
/// 为什么要有这道测试：新增一个写端点却忘了归类，就等于悄悄开了一扇
/// 不要审批的门。有了对账，任何「既不在敏感判定里、也不在开放清单里」的
/// 写端点都会让测试变红 —— 逼着加端点的人当场表态。
///
/// 反向也查：开放清单 / 敏感清单里的每一条都必须对应真实存在的端点，
/// 防止清单腐烂（端点改名 / 删除后清单还留着旧条目）。
/// </summary>
public class EndpointClassificationTests
{
    private const string SampleGuid = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// 明确开放的写端点：agent 走 MCP / CI 走 REST 的正常工作面。
    /// 键是「方法 + 路由模板（RawText，含 :guid 约束）」。
    /// 这些端点堵死了整套工具就没用了，所以刻意不要人工凭证。
    /// </summary>
    private static readonly HashSet<string> OpenWriteEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST /api/projects",
        "POST /api/projects/{projectId:guid}/tasks",
        "PATCH /api/tasks/{taskId:guid}",
        "POST /api/tasks/{taskId:guid}/archive",
        "POST /api/tasks/{taskId:guid}/assign",
        "POST /api/tasks/{taskId:guid}/dependencies",
        "DELETE /api/tasks/{taskId:guid}/dependencies/{dependsOnTaskId:guid}",
        "POST /api/tasks/{taskId:guid}/status",
        "POST /api/tasks/{taskId:guid}/claim",
        "POST /api/tasks/{taskId:guid}/plan",
        "POST /api/tasks/{taskId:guid}/artifacts",
        "POST /api/tasks/{taskId:guid}/test-result",
        "POST /api/tasks/{taskId:guid}/events",
        "POST /api/tasks/{taskId:guid}/documents",
        "POST /api/tasks/{taskId:guid}/milestone",
        "POST /api/tasks/{taskId:guid}/review",
        "POST /api/tasks/{taskId:guid}/review/{dimension}",
        "POST /api/projects/{projectId:guid}/milestones",
        "PUT /api/milestones/{milestoneId:guid}",
        "DELETE /api/milestones/{milestoneId:guid}",
    };

    /// <summary>
    /// 必须有人在场（敏感）的写端点。改的是「哪条写操作能执行 / agent 能力面 /
    /// 门禁本身」，agent 拿不到 token 就动不了。
    /// </summary>
    private static readonly HashSet<string> SensitiveWriteEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST /api/approvals/{approvalId:guid}/decide",
        "POST /api/approvals/{approvalId:guid}/revoke",
        "POST /api/approvals/batch-decide",
        "PUT /api/projects/{projectId:guid}/config",
        "POST /api/projects/{projectId:guid}/db-connections",
        "POST /api/tasks/{taskId:guid}/launch-agent",
        "POST /api/db-connections/{connectionId:guid}/rules",
        "DELETE /api/db-connections/{connectionId:guid}/rules/{ruleId:guid}",
        "POST /api/db-connections/{connectionId:guid}/test",
        "PUT /api/mcp/tools/{toolName}",
        "POST /api/mcp/tools/reset",
        "POST /api/workspace/export",
        "POST /api/workspace/import",
        "POST /api/audit/archive",
    };

    [Fact]
    public void 每个写端点要么敏感要么明确开放()
    {
        var unclassified = new List<string>();

        foreach (var (method, template, samplePath) in EnumerateWriteEndpoints())
        {
            var sensitive = HumanTokenPolicy.IsSensitiveEndpoint(method, samplePath);
            var key = $"{method} {template}";

            if (!sensitive && !OpenWriteEndpoints.Contains(key))
            {
                unclassified.Add(key);
            }
        }

        Assert.True(
            unclassified.Count == 0,
            "这些写端点既没被判为敏感、也不在开放清单里，必须当场归类：\n" + string.Join("\n", unclassified));
    }

    [Fact]
    public void 敏感写端点集合与预期完全一致()
    {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (method, template, samplePath) in EnumerateWriteEndpoints())
        {
            if (HumanTokenPolicy.IsSensitiveEndpoint(method, samplePath))
            {
                actual.Add($"{method} {template}");
            }
        }

        // 多了：本该开放的端点被误判敏感（会堵死 agent / CI）
        var extra = actual.Except(SensitiveWriteEndpoints).ToList();
        // 少了：本该敏感的端点漏判（悄悄开了不要审批的门）
        var missing = SensitiveWriteEndpoints.Except(actual).ToList();

        Assert.True(
            extra.Count == 0 && missing.Count == 0,
            $"敏感端点集合对不上。\n多出：{string.Join(", ", extra)}\n漏掉：{string.Join(", ", missing)}");
    }

    [Fact]
    public void 开放清单没有腐烂条目()
    {
        var actualKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (method, template, _) in EnumerateWriteEndpoints())
        {
            actualKeys.Add($"{method} {template}");
        }

        var stale = OpenWriteEndpoints.Except(actualKeys).ToList();

        Assert.True(
            stale.Count == 0,
            "开放清单里这些条目已经没有对应的真实端点了（改名 / 删除？）：\n" + string.Join("\n", stale));
    }

    [Fact]
    public void 开放清单里的端点确实不敏感()
    {
        // 防止有人把同一个端点既写进开放清单、又让它被判敏感 —— 口径必须自洽
        foreach (var (method, template, samplePath) in EnumerateWriteEndpoints())
        {
            var key = $"{method} {template}";
            if (OpenWriteEndpoints.Contains(key))
            {
                Assert.False(
                    HumanTokenPolicy.IsSensitiveEndpoint(method, samplePath),
                    $"开放清单里的 {key} 却被判为敏感，口径冲突。");
            }
        }
    }

    /// <summary>
    /// 枚举所有写端点（POST/PUT/DELETE/PATCH）。
    /// 真的把 WorkbenchApi 映射进一个 WebApplication，再读路由表 ——
    /// 不靠手抄清单，端点改了这里立刻跟着变。
    /// </summary>
    private static IEnumerable<(string Method, string Template, string SamplePath)> EnumerateWriteEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // 注册三层服务：路由表物化时 RequestDelegateFactory 会推断每个 handler 参数，
        // 服务没注册就会被当成「推断的 body 参数」，GET 上直接抛异常。
        // 这里只枚举路由、不执行 handler，所以工厂都是惰性的，不会真建库 / 建目录。
        builder.Services.AddWorkbenchInfrastructure(builder.Configuration);
        builder.Services.AddWorkbenchApplication();
        builder.Services.AddWorkbenchMcp();
        builder.Services.AddSingleton(new HumanTokenAccessor("test-token"));

        using var app = builder.Build();
        app.MapWorkbenchApi();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>();

        foreach (var endpoint in endpoints)
        {
            var template = endpoint.RoutePattern.RawText ?? string.Empty;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null)
            {
                continue;
            }

            var samplePath = BuildSamplePath(template);

            foreach (var method in methods)
            {
                if (IsWriteMethod(method))
                {
                    yield return (method.ToUpperInvariant(), template, samplePath);
                }
            }
        }
    }

    private static bool IsWriteMethod(string method) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 {param:constraint} 段替换成一个合法 GUID，得到能喂给策略函数的真实路径。</summary>
    private static string BuildSamplePath(string template)
    {
        var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('{') && segments[i].EndsWith('}'))
            {
                segments[i] = SampleGuid;
            }
        }

        return "/" + string.Join("/", segments);
    }
}