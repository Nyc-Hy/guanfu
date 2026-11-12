using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Workbench.Application;

/// <summary>
/// 工作台统一的 JSON 设置。
///
/// 三个要点：
/// 1. 枚举一律输出成字符串（"DocsReady" 而不是 1），agent / CLI / 脚本才读得懂；
/// 2. 必须显式指定 TypeInfoResolver —— `new JsonSerializerOptions(JsonSerializerDefaults.Web)`
///    的 TypeInfoResolver 是空的，一旦有人调 MakeReadOnly()（MCP SDK 就会）就直接抛异常；
/// 3. 关闭非 ASCII 转义 —— 默认会把中文变成 \uXXXX，agent 和人读日志都得自己解码。
/// </summary>
public static class WorkbenchJson
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(new DefaultJsonTypeInfoResolver()),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>用工作台统一设置序列化。别处不要再 new JsonSerializerOptions。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>用工作台统一设置反序列化。</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
