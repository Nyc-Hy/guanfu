using System.Text.Json;
using Workbench.Cli;

namespace Workbench.Tests;

/// <summary>
/// MCP stdio 桥接的协议层。
///
/// 这里守的是「JSON-RPC 流不能被污染」：服务端一旦回一个 HTML 错误页，
/// 桥接把它原样写进 stdout，客户端解析失败 —— 表现是 agent 说"工具没反应"，
/// 但真正的原因（服务端 500）完全看不到。
/// </summary>
public class McpBridgeProtocolTests
{
    [Fact]
    public void 能摘出字符串类型的请求id()
    {
        var id = McpBridgeProtocol.TryExtractId("""{"jsonrpc":"2.0","id":"abc-1","method":"tools/list"}""");
        Assert.Equal("abc-1", Assert.IsType<string>(id));
    }

    [Fact]
    public void 能摘出数字类型的请求id()
    {
        var id = McpBridgeProtocol.TryExtractId("""{"jsonrpc":"2.0","id":42,"method":"tools/list"}""");
        Assert.Equal(42L, Assert.IsType<long>(id));
    }

    [Fact]
    public void 通知没有id时返回null()
    {
        Assert.Null(McpBridgeProtocol.TryExtractId("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public void 非法json不抛异常()
    {
        Assert.Null(McpBridgeProtocol.TryExtractId("not json at all"));
    }

    [Fact]
    public void 能认出非json的响应体()
    {
        Assert.False(McpBridgeProtocol.LooksLikeJson("<!DOCTYPE html><html>500</html>"));
        Assert.True(McpBridgeProtocol.LooksLikeJson("""{"jsonrpc":"2.0"}"""));
        Assert.True(McpBridgeProtocol.LooksLikeJson("  [1,2]"));
    }

    [Fact]
    public void error响应是合法json且沿用原请求id()
    {
        var json = McpBridgeProtocol.BuildError("req-7", -32001, "工作台 MCP 端点返回 500。", "boom");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("req-7", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32001, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("boom", doc.RootElement.GetProperty("error").GetProperty("data").GetString());
    }

    [Fact]
    public void error响应没带data时不出现data字段()
    {
        var json = McpBridgeProtocol.BuildError(null, -32603, "内部错误", null);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("error").TryGetProperty("data", out _));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public void 从html错误页里能抽出人能看懂的正文()
    {
        var detail = McpBridgeProtocol.ExtractErrorDetail(
            "<html><body><h1>500 Internal Server Error</h1></body></html>",
            "text/html");

        Assert.NotNull(detail);
        Assert.Contains("500 Internal Server Error", detail);
        Assert.DoesNotContain("<", detail);
    }

    [Fact]
    public void 从json错误体里能抽出message()
    {
        var detail = McpBridgeProtocol.ExtractErrorDetail(
            """{"error":{"message":"连接被拒"}}""",
            "application/json");

        Assert.Equal("连接被拒", detail);
    }

    [Fact]
    public void 空body没有可抽的内容()
    {
        Assert.Null(McpBridgeProtocol.ExtractErrorDetail("   ", "text/html"));
    }

    [Fact]
    public void 过长的错误正文会被截断()
    {
        var detail = McpBridgeProtocol.ExtractErrorDetail(new string('x', 900), "text/plain");

        Assert.NotNull(detail);
        Assert.True(detail!.Length <= 501);
    }
}
