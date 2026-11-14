using System.Net;
using Microsoft.AspNetCore.Http;
using Workbench.Infrastructure;
using Workbench.Web;

namespace Workbench.Tests;

/// <summary>
/// 人工凭证门禁。
///
/// 核心承诺：**敏感端点没有人工 token 一律 401，普通端点不受影响**。
/// agent 走 MCP / REST 拿不到 token，就没法自己批准自己发起的写操作。
/// </summary>
public class HumanTokenTests
{
    private const string Token = "test-token-abc123";

    // ---- 哪些端点要人工在场 ----

    [Theory]
    [InlineData("POST", "/api/approvals/11111111-1111-1111-1111-111111111111/decide")]
    [InlineData("PUT", "/api/projects/11111111-1111-1111-1111-111111111111/config")]
    [InlineData("POST", "/api/projects/11111111-1111-1111-1111-111111111111/db-connections")]
    [InlineData("POST", "/api/tasks/11111111-1111-1111-1111-111111111111/launch-agent")]
    [InlineData("POST", "/api/workspace/export")]
    [InlineData("POST", "/api/workspace/import")]
    // 新增：撤销审批 / 表级规则写 / 探活连接 / MCP 工具开关
    [InlineData("POST", "/api/approvals/11111111-1111-1111-1111-111111111111/revoke")]
    [InlineData("POST", "/api/db-connections/11111111-1111-1111-1111-111111111111/rules")]
    [InlineData("DELETE", "/api/db-connections/11111111-1111-1111-1111-111111111111/rules")]
    [InlineData("DELETE", "/api/db-connections/11111111-1111-1111-1111-111111111111/rules/22222222-2222-2222-2222-222222222222")]
    [InlineData("POST", "/api/db-connections/11111111-1111-1111-1111-111111111111/test")]
    [InlineData("PUT", "/api/mcp/tools/db_execute")]
    [InlineData("POST", "/api/mcp/tools/reset")]
    public void 敏感端点要人工在场(string method, string path)
    {
        Assert.True(HumanTokenPolicy.IsSensitiveEndpoint(method, path));
    }

    [Theory]
    [InlineData("GET", "/api/tasks")]
    [InlineData("GET", "/api/approvals")]
    [InlineData("POST", "/api/tasks/11111111-1111-1111-1111-111111111111/status")]
    [InlineData("POST", "/api/tasks/11111111-1111-1111-1111-111111111111/plan")]
    [InlineData("GET", "/api/projects/11111111-1111-1111-1111-111111111111/config")]
    [InlineData("GET", "/api/projects/11111111-1111-1111-1111-111111111111/db-connections")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/")]
    // 读规则 / 读工具清单 / 搜索是普通读，保持开放
    [InlineData("GET", "/api/db-connections/11111111-1111-1111-1111-111111111111/rules")]
    [InlineData("GET", "/api/mcp/tools")]
    [InlineData("GET", "/api/search")]
    // 撤销只保护 POST；GET 同名路径不是写操作
    [InlineData("GET", "/api/approvals/11111111-1111-1111-1111-111111111111/revoke")]
    public void 普通端点保持开放(string method, string path)
    {
        Assert.False(HumanTokenPolicy.IsSensitiveEndpoint(method, path));
    }

    // ---- 中间件行为 ----

    [Fact]
    public async Task 敏感端点不带凭证一律401()
    {
        var result = await RunAsync("POST", "/api/approvals/11111111-1111-1111-1111-111111111111/decide");

        Assert.Equal(StatusCodes.Status401Unauthorized, result.Status);
        Assert.False(result.Reached);
        Assert.Contains("\"ok\":false", result.Body);
        Assert.Contains("error", result.Body);
    }

    [Fact]
    public async Task 敏感端点凭证错了也401()
    {
        var result = await RunAsync(
            "POST",
            "/api/approvals/11111111-1111-1111-1111-111111111111/decide",
            headerToken: "wrong-token");

        Assert.Equal(StatusCodes.Status401Unauthorized, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task 正确的请求头凭证放行()
    {
        var result = await RunAsync(
            "POST",
            "/api/approvals/11111111-1111-1111-1111-111111111111/decide",
            headerToken: Token);

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task 正确的Cookie凭证放行()
    {
        var result = await RunAsync(
            "POST",
            "/api/approvals/11111111-1111-1111-1111-111111111111/decide",
            cookieToken: Token);

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task 普通端点不带凭证照常放行()
    {
        var result = await RunAsync("GET", "/api/tasks");

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.True(result.Reached);
    }

    [Theory]
    [InlineData("POST", "/api/approvals/11111111-1111-1111-1111-111111111111/revoke")]
    [InlineData("POST", "/api/db-connections/11111111-1111-1111-1111-111111111111/test")]
    [InlineData("PUT", "/api/mcp/tools/db_execute")]
    [InlineData("POST", "/api/mcp/tools/reset")]
    [InlineData("DELETE", "/api/db-connections/11111111-1111-1111-1111-111111111111/rules/22222222-2222-2222-2222-222222222222")]
    public async Task 新增敏感端点不带凭证也401(string method, string path)
    {
        var result = await RunAsync(method, path);

        Assert.Equal(StatusCodes.Status401Unauthorized, result.Status);
        Assert.False(result.Reached);
    }

    [Fact]
    public async Task 错误的凭证不能蒙混过关()
    {
        // 大小写不同 / 少一位 / 多一位 —— 都是不同的 token
        Assert.False((await RunAsync("POST", "/api/workspace/export", headerToken: Token.ToUpperInvariant())).Reached);
        Assert.False((await RunAsync("POST", "/api/workspace/export", headerToken: Token[..^1])).Reached);
        Assert.False((await RunAsync("POST", "/api/workspace/export", headerToken: Token + "x")).Reached);
        Assert.False((await RunAsync("POST", "/api/workspace/export", cookieToken: "wrong")).Reached);
    }

    // ---- token 比对 ----

    [Fact]
    public void token比对区分大小写且长度必须一致()
    {
        Assert.True(HumanTokenPolicy.TokenMatches(Token, Token));
        Assert.False(HumanTokenPolicy.TokenMatches(Token.ToUpperInvariant(), Token));
        Assert.False(HumanTokenPolicy.TokenMatches(Token[..^1], Token));
        Assert.False(HumanTokenPolicy.TokenMatches(null, Token));
        Assert.False(HumanTokenPolicy.TokenMatches("", Token));
        Assert.False(HumanTokenPolicy.TokenMatches(Token, ""));
    }

    [Fact]
    public void header或cookie任一正确即放行()
    {
        Assert.True(HumanTokenPolicy.IsAuthorized(Token, null, Token));
        Assert.True(HumanTokenPolicy.IsAuthorized(null, Token, Token));
        Assert.True(HumanTokenPolicy.IsAuthorized("wrong", Token, Token));
        Assert.False(HumanTokenPolicy.IsAuthorized("wrong", "also-wrong", Token));
        Assert.False(HumanTokenPolicy.IsAuthorized(null, null, Token));
    }

    // ---- token 文件 ----

    [Fact]
    public void token文件权限是0600且重启不变()
    {
        var home = Path.Combine(Path.GetTempPath(), "wb-token-" + Guid.NewGuid().ToString("N"));

        try
        {
            var store = new HumanTokenStore(home);
            var token = store.LoadOrCreate();

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(File.Exists(store.FilePath));

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(store.FilePath);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }

            // 第二次读还是同一个 token —— UI 里种下的 Cookie 不能因为服务重启就失效
            Assert.Equal(token, new HumanTokenStore(home).LoadOrCreate());
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    [Fact]
    public void 不同工作台的token不一样()
    {
        var first = new HumanTokenStore(Path.Combine(Path.GetTempPath(), "wb-a-" + Guid.NewGuid().ToString("N")));
        var second = new HumanTokenStore(Path.Combine(Path.GetTempPath(), "wb-b-" + Guid.NewGuid().ToString("N")));

        try
        {
            Assert.NotEqual(first.LoadOrCreate(), second.LoadOrCreate());
        }
        finally
        {
            foreach (var store in new[] { first, second })
            {
                var home = Path.GetDirectoryName(store.FilePath)!;
                if (Directory.Exists(home))
                {
                    Directory.Delete(home, recursive: true);
                }
            }
        }
    }

    // ---- 回环判定 ----

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("0.0.0.0", false)]
    public void 回环地址判定(string address, bool expected)
    {
        Assert.Equal(expected, HumanTokenPolicy.IsLoopback(IPAddress.Parse(address)));
    }

    [Fact]
    public void 拿不到地址就不算本机()
    {
        Assert.False(HumanTokenPolicy.IsLoopback(null));
    }

    private static async Task<(int Status, bool Reached, string Body)> RunAsync(
        string method,
        string path,
        string? headerToken = null,
        string? cookieToken = null)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = method;
        context.Request.Path = path;

        if (headerToken is not null)
        {
            context.Request.Headers[HumanTokenPolicy.HeaderName] = headerToken;
        }

        if (cookieToken is not null)
        {
            context.Request.Headers["Cookie"] = $"{HumanTokenPolicy.CookieName}={cookieToken}";
        }

        var reached = false;
        var middleware = new HumanTokenMiddleware(
            _ =>
            {
                reached = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            new HumanTokenAccessor(Token));

        await middleware.InvokeAsync(context);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        return (context.Response.StatusCode, reached, text);
    }
}

/// <summary>
/// 工作区搬迁的入口侧路径校验。
/// </summary>
public class WorkspacePathGuardTests
{
    [Fact]
    public void 导出目录等于工作台目录要拒绝()
    {
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        var error = WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home", options);

        Assert.NotNull(error);
        Assert.Contains("WORKBENCH_HOME", error);
    }

    [Fact]
    public void 导出目录落在文档目录里要拒绝()
    {
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        Assert.NotNull(WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home/documents", options));
        Assert.NotNull(WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home/documents/sub", options));
    }

    [Fact]
    public void 只是前缀相同的兄弟目录不算在文档目录里()
    {
        // 归档器那边的前缀比较没带目录分隔符，会把 documents-backup 误判成在文档目录里
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        Assert.Null(WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home/documents-backup", options));
    }

    [Fact]
    public void 正常的导出目录放行()
    {
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        Assert.Null(WorkspacePathGuard.ValidateExportTarget("/tmp/backup", options));
        Assert.Null(WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home/export", options));
    }

    [Fact]
    public void 导入到正在运行的工作台目录要拒绝()
    {
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        Assert.NotNull(WorkspacePathGuard.ValidateImportTarget("/tmp/wb-home", options));
        Assert.Null(WorkspacePathGuard.ValidateImportTarget("/tmp/wb-other", options));
    }

    [Fact]
    public void 带结尾斜杠的工作台目录也要拒绝()
    {
        // Path.GetFullPath 在 Unix 上会保留结尾的 '/' —— 不归一化就能绕过相等判断
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        Assert.NotNull(WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home/", options));
        Assert.NotNull(WorkspacePathGuard.ValidateImportTarget("/tmp/wb-home/", options));
        Assert.NotNull(WorkspacePathGuard.ValidateExportTarget("/tmp/wb-home/documents/", options));
    }

    [Fact]
    public void 空目录要拒绝()
    {
        var options = new WorkbenchOptions { HomeDirectory = "/tmp/wb-home" };

        Assert.NotNull(WorkspacePathGuard.ValidateExportTarget("  ", options));
        Assert.NotNull(WorkspacePathGuard.ValidateImportTarget(null, options));
    }
}
