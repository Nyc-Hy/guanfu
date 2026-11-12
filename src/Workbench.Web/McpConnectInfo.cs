namespace Workbench.Web;

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
                "workbench": {
                  "type": "http",
                  "url": "{{endpoint}}"
                }
              }
            }
            """;
    }

    /// <summary>stdio 客户端配置（走 wb mcp 桥接）。</summary>
    public static string StdioConfig(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');

        return $$"""
            {
              "mcpServers": {
                "workbench": {
                  "command": "wb",
                  "args": ["mcp", "--url", "{{url}}"]
                }
              }
            }
            """;
    }
}
