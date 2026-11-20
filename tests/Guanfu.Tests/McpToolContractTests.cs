using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>
/// MCP 工具的契约测试。
///
/// 规矩 1：工具名一律下划线，不用点号 —— 部分 MCP 客户端不接受点号。
/// 规矩 2：工具不抛异常，要返回带 error 字段的结构化结果 ——
/// SDK 会把异常吞成一句 "An error occurred invoking ..."，agent 就看不到原因了。
///
/// 这两条以前靠人记，工具一多必然漏。这里的测试把它钉死。
/// </summary>
public sealed class McpToolContractTests
{
    private static IReadOnlyList<(string Name, MethodInfo Method)> Tools()
    {
        var assembly = typeof(McpToolCatalog).Assembly;
        var tools = new List<(string, MethodInfo)>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                tools.Add((attribute.Name ?? method.Name, method));
            }
        }

        return tools;
    }

    [Fact]
    public void 工具名一律用下划线且不重复()
    {
        var names = Tools().Select(t => t.Name).ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name =>
        {
            Assert.DoesNotContain(".", name);
            Assert.Matches("^[a-z0-9_]+$", name);
        });

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 每个工具都返回结构化结果()
    {
        foreach (var (name, method) in Tools())
        {
            var payload = Unwrap(method.ReturnType);

            // 返回列表的工具，「没有」就是空集合，错误由全局兜底过滤器接住。
            if (payload != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(payload))
            {
                continue;
            }

            Assert.True(
                payload != typeof(object),
                $"工具 {name} 返回 object —— 形状不可知，agent 没法依赖任何字段。");

            Assert.True(
                payload != typeof(string),
                $"工具 {name} 返回裸字符串 —— agent 分不出成功还是失败，要用带 Ok/Error 的结果类型。");

            Assert.True(
                !payload.IsPrimitive,
                $"工具 {name} 返回裸 {payload.Name} —— agent 分不出「结果就是 false/0」和「失败了」。");

            // 我们自己定义的结果类型必须同时给出 Ok 与 Error：
            // 失败时 agent 得能在同一个对象里读到原因。
            if (payload.Assembly == typeof(McpToolCatalog).Assembly)
            {
                Assert.True(
                    payload.GetProperty("Ok") is not null,
                    $"工具 {name} 的结果类型 {payload.Name} 没有 Ok 字段。");

                Assert.True(
                    payload.GetProperty("Error") is not null,
                    $"工具 {name} 的结果类型 {payload.Name} 没有 Error 字段 —— 失败时 agent 看不到原因。");
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type;
    }

    [Fact]
    public void 异常兜底过滤器已注册()
    {
        // 规矩 2：MCP 工具不抛异常。SDK 会把异常吞成一句
        // "An error occurred invoking ..."，agent 就看不到原因了。
        // 返回列表的工具没法在返回类型里带 error 字段，靠这个全局过滤器兜。
        var services = new ServiceCollection();
        services.AddGuanfuMcp();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.True(
            options.Filters.Request.CallToolFilters.Count > 0,
            "没有注册 CallTool 兜底过滤器 —— 工具抛异常时 agent 只会看到一句泛化提示。");
    }

    [Fact]
    public void 写工具必须声明走审批()
    {
        foreach (var name in new[] { "db_execute" })
        {
            var tool = Tools().FirstOrDefault(t => t.Name == name);

            Assert.True(tool.Method is not null, $"缺少写工具 {name}。");
            Assert.True(
                tool.Method.GetCustomAttribute<McpRequiresApprovalAttribute>() is not null,
                $"写工具 {name} 没有标 [McpRequiresApproval] —— 闸门靠这个标记认人。");
        }
    }

    /// <summary>
    /// 每个工具的入参 / 返回值都必须能生成 JSON Schema。
    ///
    /// 这条踩过坑：DTO 里给 Guid 加了个默认值（<c>Guid WorkspaceId = default</c>），
    /// SDK 的 schema 生成器把默认值写回 JSON 时转不回 Guid，
    /// 结果**整个服务起不来**（工具注册阶段抛异常）——
    /// 而单测全绿，因为谁也没在测试里真的建工具。
    ///
    /// 所以这条钉在「把每个工具都建一遍」上：
    /// 建不出来就说明这个工具会让服务起不来。
    /// </summary>
    [Fact]
    public void 每个工具的Schema都能生成()
    {
        var failures = new List<string>();

        foreach (var (name, method) in Tools())
        {
            var declaringType = method.DeclaringType!;

            try
            {
                McpServerTool.Create(
                    method,
                    target: () => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(declaringType),
                    options: null);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "这些工具生成不了 Schema —— 服务启动时就会炸：\n" + string.Join("\n", failures));
    }
}
