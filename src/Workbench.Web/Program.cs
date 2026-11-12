using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Infrastructure;
using Workbench.Mcp;
using Workbench.Web;
using Workbench.Web.Api;
using Workbench.Web.Components;
using IpNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

var builder = WebApplication.CreateBuilder(args);

// 限流参数：宽松但存在。300 次/分钟/IP —— agent 的正常用量远低于这个数，
// 但足够挡住「脚本无限打审批裁决接口」这种噪声。
const int ApiPermitPerMinute = 300;
const int ApiRateWindowMinutes = 1;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 枚举一律输出成字符串（"DocsReady" 而不是 1），
// agent 与 CLI 读到的字段才有意义
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// 工作台三层：基础设施（数据）→ 应用（用例）→ MCP 工具
builder.Services.AddWorkbenchInfrastructure(builder.Configuration);
builder.Services.AddWorkbenchApplication();
builder.Services.AddWorkbenchMcp();
builder.Services.AddScoped<WorkbenchInitializer>();

// 定时备份 SQLite 库。放在 Web 层是因为 BackgroundService 属于 Hosting ——
// 应用层刻意不依赖 Hosting（它只该有业务用例）。
builder.Services.AddHostedService<DatabaseBackupService>();

// 人工凭证：token 落在 WORKBENCH_HOME/.human-token（0600），重启不变。
// 用工厂延迟到第一次解析时才读 —— 那时 IOptions<WorkbenchOptions> 已经有值了，
// 免得在这里另造一套 WORKBENCH_HOME 的路径规则。
builder.Services.AddSingleton(provider =>
{
    var options = provider.GetRequiredService<IOptions<WorkbenchOptions>>().Value;
    return new HumanTokenAccessor(new HumanTokenStore(options.HomeDirectory).LoadOrCreate());
});

// 转发头：只信回环上的代理。内网直连时对端不是回环，X-Forwarded-* 一律不认 ——
// 否则谁都能伪造 X-Forwarded-For 把自己的 IP 洗成 127.0.0.1。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownNetworks.Add(new IpNetwork(IPAddress.Loopback, 8));
    options.KnownNetworks.Add(new IpNetwork(IPAddress.IPv6Loopback, 128));
    options.KnownProxies.Clear();
});

// 限流：只压 /api/*。UI 页面、MCP 端点、健康检查都不限 ——
// agent 高频调 MCP 是正常用法，限死了整套工具就废了。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/json; charset=utf-8";

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        await response.WriteAsync(
            "{\"ok\":false,\"error\":\"请求过于频繁，请稍后再试。\"}",
            cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("no-limit");
        }

        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ApiPermitPerMinute,
            Window = TimeSpan.FromMinutes(ApiRateWindowMinutes),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

var app = builder.Build();

// 先记下转发之前的真实对端地址：人工 token 的「本机引导」靠它判断请求是不是
// 真的从本机发出来的（X-Forwarded-For 能把回环地址伪造出来）。
app.UseMiddleware<OriginalRemoteAddressMiddleware>();

// 转发头必须排在最前面：后面所有的 RemoteIpAddress / IsHttps 都得是真的
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();

    // HTTPS 重定向：只有真绑了 HTTPS 端口时才挂。
    // 没绑的话每次请求都会打一条 "Failed to determine the https port" 警告，
    // 而重定向本身也无处可去 —— 那种部署（0.0.0.0 + 反代终结 TLS）
    // 靠 X-Forwarded-Proto 把协议还原成 https 就够了。
    app.UseWhen(HasHttpsAddress, branch => branch.UseHttpsRedirection());
}

app.UseRateLimiter();
app.UseStaticFiles();
app.UseAntiforgery();

// 人工凭证门禁：敏感端点（裁决审批 / 改项目配置 / 启动 agent /
// 工作区搬迁 / 数据库连接）没凭证直接 401，其余端点保持开放。
app.UseMiddleware<HumanTokenMiddleware>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// REST API：给 CI / webhook / 脚本
app.MapWorkbenchApi();

// MCP 端点：给外部 agent（Streamable HTTP）
app.MapMcp("/mcp");

// 健康检查：不泄露任何敏感信息
app.MapGet("/health", () => Results.Ok(new { ok = true, status = "ok" }));

// 初始化数据库（建表 + 默认工作区）
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<WorkbenchInitializer>().InitializeAsync();
}

// 启动横幅：人工 token 必须让人看得见 —— 敏感端点的钥匙不能只躺在文件里。
// 监听地址从 IServer.Addresses 取（绑定之后的真实地址），不硬编码。
var humanTokens = app.Services.GetRequiredService<HumanTokenAccessor>();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var server = app.Services.GetRequiredService<IServer>();
    var lines = new List<string>
    {
        string.Empty,
        "===================== 工作台已启动 ====================="
    };

    // 绑定之后的真实监听地址（不是配置里写的），从 IServer 的地址特性取
    var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses?.ToArray()
        ?? Array.Empty<string>();
    if (addresses.Length == 0)
    {
        lines.Add("  监听地址：（未报告）");
    }

    foreach (var address in addresses)
    {
        lines.Add($"  监听地址：{address}");

        if (!IsLoopbackAddress(address))
        {
            lines.Add($"  ⚠ 监听地址不是本机（{address}）—— 内网可达。");
            lines.Add("    敏感端点（审批裁决 / 改项目配置 / 启动 agent / 工作区搬迁 / 数据库连接）");
            lines.Add("    需要人工 token 才能调用。");
            app.Logger.LogWarning(
                "工作台监听在非本机地址 {Address}，内网可达；敏感端点需要人工 token 保护。",
                address);
        }
    }

    lines.Add($"  人工 token：{humanTokens.Token}");
    lines.Add($"    请求头：{HumanTokenPolicy.HeaderName}: {humanTokens.Token}");
    lines.Add($"    或 Cookie：{HumanTokenPolicy.CookieName}={humanTokens.Token}");
    lines.Add("======================================================");
    lines.Add(string.Empty);

    Console.WriteLine(string.Join(Environment.NewLine, lines));
});

app.Run();

// 服务器上真的绑了 HTTPS 地址才做 HTTP→HTTPS 重定向
static bool HasHttpsAddress(HttpContext context)
{
    var addresses = context.Features.Get<IServerAddressesFeature>()?.Addresses;
    if (addresses is null)
    {
        return false;
    }

    foreach (var address in addresses)
    {
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

// 监听地址是不是只在本机可达
static bool IsLoopbackAddress(string address)
{
    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    // IPv6 的 Host 带方括号：[::1] → ::1
    var host = uri.Host.Trim('[', ']');
    return IPAddress.TryParse(host, out var ip) && HumanTokenPolicy.IsLoopback(ip);
}

/// <summary>供集成测试引用的入口标记。</summary>
public partial class Program;
