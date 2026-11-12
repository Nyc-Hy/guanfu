using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Workbench.Mcp;

/// <summary>
/// 标记「执行前必须人工审批」的 MCP 工具。
/// 只用于展示与提示，真正的拦截在审批闸门里。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpRequiresApprovalAttribute : Attribute;

/// <summary>工具的静态描述。</summary>
public sealed record McpToolDescriptor(
    string Name,
    string Category,
    string Description,
    bool RequiresApproval);

/// <summary>
/// MCP 工具目录。
///
/// 工具清单从**实际注册的工具**反射出来，而不是手写一份 ——
/// 手写清单迟早和代码对不上（加了工具忘了同步、删了工具忘了删）。
/// </summary>
public static class McpToolCatalog
{
    /// <summary>分类定义。顺序即页面上的展示顺序。</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "工作台组",
        "文档组",
        "数据库组",
        "通用组"
    ];

    /// <summary>工具名前缀 → 分类。</summary>
    private static readonly IReadOnlyDictionary<string, string> PrefixCategories =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workbench_"] = "工作台组",
            ["doc_"] = "文档组",
            ["db_"] = "数据库组"
        };

    private const string DefaultCategory = "通用组";

    /// <summary>扫描程序集里所有带 McpServerTool 特性的方法。</summary>
    public static IReadOnlyList<McpToolDescriptor> Discover(Assembly assembly)
    {
        var descriptors = new List<McpToolDescriptor>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(attribute.Name)
                    ? ToSnakeCase(method.Name)
                    : attribute.Name.Trim();

                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

                descriptors.Add(new McpToolDescriptor(
                    name,
                    CategoryOf(name),
                    description,
                    method.GetCustomAttribute<McpRequiresApprovalAttribute>() is not null));
            }
        }

        return descriptors
            .OrderBy(d => CategoryOrder(d.Category))
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static string CategoryOf(string toolName)
    {
        foreach (var (prefix, category) in PrefixCategories)
        {
            if (toolName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return DefaultCategory;
    }

    private static int CategoryOrder(string category)
    {
        var index = Categories.ToList().IndexOf(category);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>PascalCase → snake_case（与 SDK 的默认工具名规则一致）。</summary>
    internal static string ToSnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
