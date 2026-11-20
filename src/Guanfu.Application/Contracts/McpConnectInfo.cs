namespace Guanfu.Application.Contracts;

/// <summary>
/// 接入 agent 的配置片段。
///
/// 放在 .cs 而不是 .razor 里：Razor 的代码块解析器不吃 C# 原始字符串字面量。
/// </summary>
public static class McpConnectInfo
{
    /// <summary>HTTP 客户端配置（Claude Code / Codex 等）。</summary>
    public static string HttpConfig(string baseUrl)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/mcp";

        return $$"""
            {
              "mcpServers": {
                "guanfu": {
                  "type": "http",
                  "url": "{{endpoint}}",
                  "headers": {
                    "X-Guanfu-Token": "<凭证>"
                  }
                }
              }
            }
            """;
    }

    /// <summary>stdio 客户端配置（走 guanfu mcp 桥接）。</summary>
    public static string StdioConfig(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');

        return $$"""
            {
              "mcpServers": {
                "guanfu": {
                  "command": "guanfu",
                  "args": ["mcp", "--url", "{{url}}"],
                  "env": { "GUANFU_TOKEN": "<凭证>" }
                }
              }
            }
            """;
    }
}
