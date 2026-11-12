using System.Text;
using System.Text.Json;

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
            var line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
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
                if (!string.IsNullOrWhiteSpace(body))
                {
                    await Console.Out.WriteLineAsync(body);
                    await Console.Out.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                var error = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new { code = -32000, message = $"无法连接工作台 MCP 端点 {endpoint}: {ex.Message}" }
                });

                await Console.Out.WriteLineAsync(error);
                await Console.Out.FlushAsync();
            }
        }
    }
}
