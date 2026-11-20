using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Guanfu.Application;
using static Guanfu.Cli.CliArgs;

namespace Guanfu.Cli;

/// <summary>
/// 从 shell 调 MCP 工具。
///
/// **为什么要这个门**：MCP 工具面是观复的**唯一**工具面 ——
/// agent 用它，本地脚本也该用它。命令行若自己抄一套工具（report status / report plan…），
/// 就是**第二套工具面**：改了 MCP 那边的字段，这边迟早对不上。
///
/// 所以：命令行走**同一套工具**，只是换个调用者。
/// 命令行独有的那点本事（读**本地**文件 —— MCP 的工具在服务端跑，够不到本地文件）
/// 留给 `guanfu report` 那两条命令。
/// </summary>
internal static class McpCallCommand
{
    public static async Task<int> RunAsync(string url, List<string> cliArgs)
    {
        if (cliArgs.Count == 0)
        {
            Console.Error.WriteLine("用法: guanfu call <工具名> [--arg 键=值 ...] [--arg 键=@文件.json]");
            return 1;
        }

        var tool = Arg(cliArgs, 0);
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);

        // 逐个读 --arg 键=值 —— 不能用 GetOption("--arg")：
        // 那只会拿到**第一个** --arg 的值，后面的参数全丢。
        for (var i = 0; i < cliArgs.Count; i++)
        {
            if (!cliArgs[i].StartsWith("--arg", StringComparison.Ordinal))
            {
                continue;
            }

            var value = i + 1 < cliArgs.Count ? cliArgs[i + 1] : null;

            if (value is null || !value.Contains('='))
            {
                Console.Error.WriteLine($"参数格式不对：--arg（要写成 --arg 键=值）");
                return 1;
            }

            i++;

            var separator = value.IndexOf('=');
            var key = value[..separator];
            var text = value[(separator + 1)..];

            if (text.StartsWith("@", StringComparison.Ordinal))
            {
                var path = text[1..];

                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"文件不存在：{path}");
                    return 1;
                }

                // 本地文件当参数：整份 JSON 读进来（工具的复杂参数走这条）
                var element = JsonSerializer.Deserialize<JsonElement>(
                    await File.ReadAllTextAsync(path), GuanfuJson.Options);

                arguments[key] = element;
                continue;
            }

            arguments[key] = text;
        }

        try
        {
            var result = await CallAsync(url, tool, arguments, McpBridge.ResolveToken(cliArgs));
            Console.WriteLine(result);
            return 0;
        }
        catch (GuanfuCliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>列观复现在暴露的工具（tools/list）。</summary>
    public static async Task<int> ListToolsAsync(string url)
    {
        try
        {
            var tools = await FetchToolsAsync(url, McpBridge.ResolveToken([]));
            Console.WriteLine($"观复现在暴露 {tools.Count} 个工具（`guanfu call <工具名> --arg 键=值` 调用）：");
            Console.WriteLine();

            foreach (var tool in tools)
            {
                var description = tool.Description.Replace("\n", " ").Trim();
                Console.WriteLine($"  {tool.Name}");
                if (description.Length > 0)
                {
                    Console.WriteLine($"    {Truncate(description, 100)}");
                }
            }

            return 0;
        }
        catch (GuanfuCliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<List<(string Name, string Description)>> FetchToolsAsync(string url, string? token)
    {
        var endpoint = url.TrimEnd('/') + "/mcp";
        using var http = new HttpClient(CliServer.NewHandler()) { Timeout = TimeSpan.FromMinutes(2) };

        string? sessionId = null;

        var initBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "guanfu-cli", version = "0.1.0" }
            }
        }, GuanfuJson.Options);

        using (var initResponse = await SendAsync(http, endpoint, initBody, sessionId, token))
        {
            sessionId = SessionIdOf(initResponse);
        }

        using (var initialized = await SendAsync(
            http, endpoint, McpBridgeProtocol.InitializedNotification, sessionId, token))
        {
            sessionId = SessionIdOf(initialized) ?? sessionId;
        }

        var listBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/list"
        }, GuanfuJson.Options);

        using var response = await SendAsync(http, endpoint, listBody, sessionId, token);
        var payload = await ReadPayloadAsync(response);

        if (payload.Length == 0)
        {
            throw new GuanfuCliException($"tools/list 没回内容（HTTP {response.StatusCode}）。");
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);

            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("tools", out var tools))
            {
                Console.Error.WriteLine($"[调试] tools/list 的回包看着不对：{payload[..Math.Min(300, payload.Length)]}");
                return [];
            }

            return tools.EnumerateArray()
                .Select(t => (
                    t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Truncate(string text, int length) =>
        string.IsNullOrEmpty(text) ? text : text.Length <= length ? text : text[..length] + "…";

    /// <summary>走 MCP 协议调一个工具，返回给人看的结果。</summary>
    internal static async Task<string> CallAsync(
        string url,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        string? token = null)
    {
        var endpoint = url.TrimEnd('/') + "/mcp";
        using var http = new HttpClient(CliServer.NewHandler()) { Timeout = TimeSpan.FromMinutes(2) };

        string? sessionId = null;

        // ① initialize
        var initBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "guanfu-cli", version = "0.1.0" }
            }
        }, GuanfuJson.Options);

        using (var initResponse = await SendAsync(http, endpoint, initBody, sessionId, token))
        {
            sessionId = SessionIdOf(initResponse);

            if (!initResponse.IsSuccessStatusCode)
            {
                throw new GuanfuCliException(
                    $"连不上观复 MCP 端点（HTTP {initResponse.StatusCode}）：{await initResponse.Content.ReadAsStringAsync()}");
            }
        }

        // ② notifications/initialized
        using (var initialized = await SendAsync(
            http, endpoint, McpBridgeProtocol.InitializedNotification, sessionId, token))
        {
            sessionId = SessionIdOf(initialized) ?? sessionId;
        }

        // ③ tools/call
        var callBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = tool,
                arguments = arguments
            }
        }, GuanfuJson.Options);

        using var response = await SendAsync(http, endpoint, callBody, sessionId, token);
        var payload = await ReadPayloadAsync(response);

        if (!response.IsSuccessStatusCode)
        {
            throw new GuanfuCliException($"调用失败（HTTP {response.StatusCode}）：{payload}");
        }

        return ReadToolResult(payload);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        string endpoint,
        string body,
        string? sessionId,
        string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Guanfu-Token", token);
        }

        return await http.SendAsync(request);
    }

    private static string? SessionIdOf(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            return values.FirstOrDefault();
        }

        return null;
    }

    /// <summary>读响应体（SSE 就取 data 行，JSON 就原样）。</summary>
    private static async Task<string> ReadPayloadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (!contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        var sb = new StringBuilder();

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                sb.Append(trimmed["data:".Length..].Trim());
            }
        }

        return sb.Length > 0 ? sb.ToString() : body;
    }

    /// <summary>从 JSON-RPC 响应里取出工具返回的内容（给人看的文字）。</summary>
    internal static string ReadToolResult(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "（工具没有返回内容）";
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();

                return $"调用失败：{message}";
            }

            if (!root.TryGetProperty("result", out var result))
            {
                return payload;
            }

            if (result.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array &&
                content.GetArrayLength() > 0)
            {
                var first = content[0];

                if (first.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }

            if (result.TryGetProperty("structuredContent", out var structured))
            {
                return structured.GetRawText();
            }

            return result.GetRawText();
        }
        catch (JsonException)
        {
            return payload;
        }
    }
}
