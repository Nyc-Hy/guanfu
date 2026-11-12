using System.Text;
using System.Text.Json;
using Workbench.Application;

namespace Workbench.Cli;

/// <summary>
/// MCP stdio ↔ HTTP 桥接。
///
/// MCP 客户端（Claude Code / Codex 等）多以 stdio 方式启动 MCP 进程；
/// 而工作台的数据与服务都在常驻的 Web 进程里。这里把 stdio 的 JSON-RPC
/// 逐条转发到 Web 的 /mcp 端点（Streamable HTTP），并处理会话头与 SSE 响应。
///
/// 已知限制：只转发客户端 → 服务端的方向，服务端主动发起的请求会被忽略。
/// </summary>
public static class McpBridge
{
    public static async Task<int> RunAsync(string baseUrl, List<string> args)
    {
        var endpoint = baseUrl.TrimEnd('/') + "/mcp";
        using var handler = new HttpClientHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        string? sessionId = null;

        while (true)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync();
            }
            catch (IOException)
            {
                // stdin 被关掉了（客户端先退出了）。当作正常结束，别把异常甩到 stderr。
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (line is null)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // 先把请求里的 id 摘出来：后面出错时要拿它回一条 error，
            // 否则客户端对不上自己的请求，只能干等。
            var requestId = McpBridgeProtocol.TryExtractId(line);
            if (requestId is null && !McpBridgeProtocol.LooksLikeJson(line))
            {
                await WriteErrorAsync(requestId, -32700, "请求不是合法 JSON。", null);
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(line, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");

            if (sessionId is not null)
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            }

            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
                {
                    sessionId = values.FirstOrDefault();
                }

                // 202 = 通知已接受，无响应体
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    while (await reader.ReadLineAsync() is { } sseLine)
                    {
                        if (!sseLine.StartsWith("data:", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var payload = sseLine["data:".Length..].Trim();
                        if (payload.Length == 0)
                        {
                            continue;
                        }

                        await Console.Out.WriteLineAsync(payload);
                        await Console.Out.FlushAsync();
                    }

                    continue;
                }

                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // 服务端回的不是 JSON（400/500 的 HTML 错误页）时，
                    // 绝不能把它原样写进 stdout —— 那会污染 JSON-RPC 流，
                    // 客户端解析失败，连真正的错误原因都看不到。
                    await WriteErrorAsync(
                        requestId,
                        -32001,
                        $"工作台 MCP 端点返回 {response.StatusCode}。",
                        McpBridgeProtocol.ExtractErrorDetail(body, contentType));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    continue;
                }

                if (!McpBridgeProtocol.LooksLikeJson(body))
                {
                    await WriteErrorAsync(
                        requestId,
                        -32603,
                        "工作台 MCP 端点返回了非 JSON 内容。",
                        McpBridgeProtocol.Truncate(body, 500));
                    continue;
                }

                await Console.Out.WriteLineAsync(body);
                await Console.Out.FlushAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await WriteErrorAsync(
                    requestId,
                    -32000,
                    $"无法连接工作台 MCP 端点 {endpoint}: {ex.Message}",
                    null);
            }
            catch (Exception ex)
            {
                // 兜底：任何别的异常都不能让桥接进程死掉。
                // 桥接一死，客户端会认为整个 MCP 服务不可用。
                await WriteErrorAsync(requestId, -32603, $"MCP 桥接内部错误: {ex.Message}", null);
            }
        }
    }

    private static async Task WriteErrorAsync(object? id, int code, string message, string? data)
    {
        var error = McpBridgeProtocol.BuildError(id, code, message, data);
        await Console.Out.WriteLineAsync(error);
        await Console.Out.FlushAsync();
    }
}

/// <summary>
/// 桥接的纯逻辑：摘 id、判 JSON、造 error 响应。
/// 单独拎出来是为了能脱离进程和网络做单元测试 —— 这几条全错了的表现是
/// "客户端莫名其妙收不到响应"，不写测试基本发现不了。
/// </summary>
public static class McpBridgeProtocol
{
    /// <summary>
    /// 摘出 JSON-RPC 请求的 id。解析不了就返回 null（通知类请求本来就没有 id）。
    /// </summary>
    public static object? TryExtractId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("id", out var id))
            {
                return null;
            }

            return id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetInt64(),
                JsonValueKind.Null => null,
                _ => id.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>粗判是不是 JSON（对象或数组）。用来挡住 HTML 错误页。</summary>
    public static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    /// <summary>造一条 JSON-RPC error 响应。id 沿用原请求的。</summary>
    public static string BuildError(object? id, int code, string message, string? data)
    {
        // 两个分支的匿名类型不一样，条件表达式推不出类型 —— 直接分两次序列化。
        var payload = data is null
            ? JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message }
            }, WorkbenchJson.Options)
            : JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message, data }
            }, WorkbenchJson.Options);

        return payload;
    }

    /// <summary>
    /// 从错误响应里抽出能给人看的一句。
    /// 服务端回 HTML 时把标签剥掉留正文，别把一整页标签糊到 agent 脸上。
    /// </summary>
    public static string? ExtractErrorDetail(string body, string contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString();
                    }

                    if (error.TryGetProperty("message", out var message))
                    {
                        return message.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // 不是合法 JSON 就按纯文本处理
            }
        }

        var text = StripTags(body);
        return Truncate(text, 500);
    }

    private static string StripTags(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        var insideTag = false;

        foreach (var ch in html)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                sb.Append(ch);
            }
        }

        return System.Net.WebUtility.HtmlDecode(sb.ToString()).Trim();
    }

    public static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text.Length <= max ? text : text[..max] + "…";
    }
}
