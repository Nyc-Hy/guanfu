using System.Text.Json;
using Guanfu.Web;

namespace Guanfu.Tests;

/// <summary>
/// 接入页给的配置片段得是**能直接用**的 —— 复制粘贴到 agent 客户端就能连上。
/// </summary>
public class McpConnectInfoTests
{
    [Fact]
    public void HTTP配置是合法JSON且指向mcp端点()
    {
        var config = McpConnectInfo.HttpConfig("http://127.0.0.1:5177/");

        var json = JsonSerializer.Deserialize<JsonElement>(config);
        var url = json.GetProperty("mcpServers").GetProperty("guanfu").GetProperty("url").GetString();

        Assert.Equal("http://127.0.0.1:5177/mcp", url);
        Assert.Equal("http", json.GetProperty("mcpServers").GetProperty("guanfu").GetProperty("type").GetString());
    }

    [Fact]
    public void stdio配置指向桥接且不带尾斜杠()
    {
        var config = McpConnectInfo.StdioConfig("http://127.0.0.1:5177/");

        var json = JsonSerializer.Deserialize<JsonElement>(config);
        var server = json.GetProperty("mcpServers").GetProperty("guanfu");

        Assert.Equal("guanfu", server.GetProperty("command").GetString());

        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(new[] { "mcp", "--url", "http://127.0.0.1:5177" }, args);
    }
}
