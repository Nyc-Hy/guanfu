using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Workbench.Application;

/// <summary>
/// 工作台统一的 JSON 设置。
///
/// 两个要点：
/// 1. 枚举一律输出成字符串（"DocsReady" 而不是 1），agent / CLI / 脚本才读得懂；
/// 2. 必须显式指定 TypeInfoResolver —— `new JsonSerializerOptions(JsonSerializerDefaults.Web)`
///    的 TypeInfoResolver 是空的，一旦有人调 MakeReadOnly()（MCP SDK 就会）就直接抛异常。
/// </summary>
public static class WorkbenchJson
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(new DefaultJsonTypeInfoResolver())
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static readonly JsonSerializerOptions Options = Create();
}
