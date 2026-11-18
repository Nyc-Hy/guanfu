using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Guanfu.Web;

namespace Guanfu.Tests;

/// <summary>
/// 跨站 / DNS rebinding 防护。
///
/// 两条核心承诺：
/// 1. /api 的写请求带了 Origin 头，但 Origin 与 Host 不同源 → 403（挡 CSRF）。
///    不带 Origin 的非浏览器请求（CLI / MCP / 脚本）放行。
/// 2. Host 头不在允许清单（默认回环）→ 403，连 GET 都挡（挡 DNS rebinding）。
///
/// 不能把 CLI / MCP 弄坏：它们用 localhost 连接、不带 Origin，必须照常通过。
/// </summary>
public class OriginHostGuardTests
{
    // ---- 策略层：Host 允许清单 ----

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("localhost:5000", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:8080", true)]
    [InlineData("[::1]", true)]
    [InlineData("[::1]:5000", true)]
    [InlineData("LOCALHOST:5000", true)]
    [InlineData("evil.com", false)]
    [InlineData("attacker.localhost", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 默认只认回环Host(string? host, bool expected)
    {
        Assert.Equal(expected, OriginHostPolicy.IsAllowedHost(host, OriginHostPolicy.DefaultAllowedHosts));
    }

    [Fact]
    public void 自定义允许清单放行内网主机()
    {
        var allowed = new[] { "workbench.internal", "10.0.0.9" };

        Assert.True(OriginHostPolicy.IsAllowedHost("workbench.internal:5000", allowed));
        Assert.True(OriginHostPolicy.IsAllowedHost("10.0.0.9", allowed));
        // 自定义清单不再默认含回环 —— 显式配置就是显式边界
        Assert.False(OriginHostPolicy.IsAllowedHost("localhost", allowed));
        Assert.False(OriginHostPolicy.IsAllowedHost("evil.com", allowed));
    }

    // ---- 策略层：哪些请求要做 Origin 校验 ----

    [Theory]
    [InlineData("POST", "/api/tasks", true)]
    [InlineData("PUT", "/api/projects/1/config", true)]
    [InlineData("DELETE", "/api/milestones/1", true)]
    [InlineData("PATCH", "/api/tasks/1", true)]
    [InlineData("POST", "/api/tasks/1/documents", true)]
    [InlineData("GET", "/api/tasks", false)]
    [InlineData("HEAD", "/api/tasks", false)]
    [InlineData("POST", "/mcp", false)]
    [InlineData("POST", "/", false)]
    [InlineData("POST", "/apixxx", false)]
    public void 只有api写方法才查Origin(string method, string path, bool expected)
    {
        Assert.Equal(expected, OriginHostPolicy.RequiresOriginCheck(method, path));
    }

    // ---- 策略层：Origin 同源判定 ----

    [Theory]
    [InlineData(null, "localhost:5000", "http", true)] // 无 Origin = 非浏览器，放行
    [InlineData("", "localhost:5000", "http", true)]
    [InlineData("http://localhost:5000", "localhost:5000", "http", true)]
    [InlineData("http://localhost", "localhost", "http", true)]
    [InlineData("https://localhost", "localhost:443", "https", true)] // 默认端口归一化
    [InlineData("http://localhost", "localhost:80", "http", true)]
    [InlineData("http://127.0.0.1:5000", "127.0.0.1:5000", "http", true)]
    [InlineData("http://[::1]:5000", "[::1]:5000", "http", true)] // IPv6 方括号归一化
    [InlineData("http://[::1]", "[::1]", "http", true)]
    [InlineData("http://evil.com", "localhost:5000", "http", false)] // 跨站
    [InlineData("http://localhost:5001", "localhost:5000", "http", false)] // 端口不同
    [InlineData("http://localhost", "localhost:5000", "http", false)] // Origin 默认 80 ≠ Host 5000
    [InlineData("null", "localhost:5000", "http", false)] // 沙箱 iframe / file://
    [InlineData("not-a-uri", "localhost:5000", "http", false)]
    [InlineData("ftp://localhost", "localhost", "http", false)] // 非 http(s)
    public void Origin同源判定(string? origin, string host, string scheme, bool expected)
    {
        Assert.Equal(expected, OriginHostPolicy.IsOriginAllowed(origin, host, scheme));
    }

    // ---- 中间件行为 ----

    [Fact]
    public async Task 跨站写请求被拒403()
    {
        var result = await RunAsync("POST", "/api/tasks", host: "localhost:5000", origin: "http://evil.com");

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
        Assert.Contains("\"ok\":false", result.Body);
    }

    [Fact]
    public async Task 同源写请求放行()
    {
        var result = await RunAsync("POST", "/api/tasks", host: "localhost:5000", origin: "http://localhost:5000");

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task 不带Origin的写请求放行()
    {
        // CLI / MCP / 脚本不带 Origin —— 不能把它们弄坏
        var result = await RunAsync("POST", "/api/tasks", host: "localhost:5000", origin: null);

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task 手工ReadFormAsync上传也查Origin()
    {
        // /api/tasks/{id}/documents 是手工 ReadFormAsync 的上传，同样要挡跨站
        var result = await RunAsync(
            "POST", "/api/tasks/11111111-1111-1111-1111-111111111111/documents",
            host: "localhost:5000", origin: "http://evil.com");

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task 跨站GET不查Origin但Host仍兜底()
    {
        // 读不带副作用，Origin 不查；但 Host 合法所以放行
        var result = await RunAsync("GET", "/api/tasks", host: "localhost:5000", origin: "http://evil.com");

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task 恶意Host连GET都拒()
    {
        // DNS rebinding：浏览器跑在攻击者域名下，Host 就是那个域名
        var result = await RunAsync("GET", "/api/tasks", host: "evil.com", origin: null);

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task 恶意Host的写请求也拒()
    {
        var result = await RunAsync("POST", "/api/tasks", host: "evil.com", origin: "http://evil.com");

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task 缺失Host一律拒()
    {
        var result = await RunAsync("GET", "/api/tasks", host: null, origin: null);

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
    }

    [Theory]
    [InlineData("127.0.0.1:5000")]
    [InlineData("[::1]:5000")]
    [InlineData("localhost")]
    public async Task 回环Host放行(string host)
    {
        var result = await RunAsync("GET", "/api/tasks", host: host, origin: null);

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task Origin为null字符串被拒()
    {
        var result = await RunAsync("POST", "/api/tasks", host: "localhost:5000", origin: "null");

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task 端口不一致被拒()
    {
        var result = await RunAsync("POST", "/api/tasks", host: "localhost:5000", origin: "http://localhost:5001");

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task https默认端口归一化后放行()
    {
        var result = await RunAsync("POST", "/api/tasks", host: "localhost:443", origin: "https://localhost", scheme: "https");

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task 自定义允许清单放行内网Host()
    {
        var result = await RunAsync(
            "POST", "/api/tasks", host: "workbench.internal:5000", origin: "http://workbench.internal:5000",
            allowedHosts: new[] { "workbench.internal" });

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task MCP端点不带Origin用localhost照常通过()
    {
        // /mcp 不是 /api 写，Origin 不查；Host 是回环所以放行
        var result = await RunAsync("POST", "/mcp", host: "localhost:5000", origin: null);

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    // ---- 配置读取 ----

    [Fact]
    public void 配置缺省回落到回环清单()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var hosts = OriginHostAllowedHosts.Read(config);

        Assert.Equal(OriginHostPolicy.DefaultAllowedHosts, hosts);
    }

    [Fact]
    public void 配置支持逗号分隔字符串()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OriginHostAllowedHosts.ConfigKey] = "workbench.internal, 10.0.0.9 ,localhost"
        }).Build();

        var hosts = OriginHostAllowedHosts.Read(config);

        Assert.Equal(new[] { "workbench.internal", "10.0.0.9", "localhost" }, hosts);
    }

    [Fact]
    public void 配置支持数组()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OriginHostAllowedHosts.ConfigKey}:0"] = "workbench.internal",
            [$"{OriginHostAllowedHosts.ConfigKey}:1"] = "10.0.0.9",
        }).Build();

        var hosts = OriginHostAllowedHosts.Read(config);

        Assert.Equal(new[] { "workbench.internal", "10.0.0.9" }, hosts);
    }

    private static async Task<(int Status, bool Reached, string Body)> RunAsync(
        string method,
        string path,
        string? host,
        string? origin,
        string scheme = "http",
        IReadOnlyCollection<string>? allowedHosts = null)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = scheme;

        if (host is not null)
        {
            context.Request.Host = new HostString(host);
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        var reached = false;
        var middleware = new OriginHostGuardMiddleware(
            _ =>
            {
                reached = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            allowedHosts ?? OriginHostPolicy.DefaultAllowedHosts);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        return (context.Response.StatusCode, reached, text);
    }
}