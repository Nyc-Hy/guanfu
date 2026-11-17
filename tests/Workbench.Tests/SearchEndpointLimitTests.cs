using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Infrastructure;
using Workbench.Mcp;
using Workbench.Web;
using Workbench.Web.Api;

namespace Workbench.Tests;

/// <summary>
/// 搜索端点的限量。
///
/// 文档搜索要真去解析文档内容 —— 不限量就是「全库解析」：
/// 任务一多，这个接口会把服务拖垮。界面的搜索页早就有限量了，
/// REST 这条也必须有（两边是同一个能力，不能一条有界一条无界）。
/// </summary>
public class SearchEndpointLimitTests
{
    /// <summary>
    /// 记下任务服务每次 ListAsync 拿到的 limit —— 用 DispatchProxy 包一层，
    /// 转发给真实实现，这样既能观测、又不用手写整个接口。
    /// </summary>
    public class RecordingTaskServiceProxy : DispatchProxy
    {
        public ITaskService? Real { get; set; }

        public Action<int?>? OnList { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is { Name: "ListAsync" })
            {
                var parameters = targetMethod.GetParameters();
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].Name == "limit")
                    {
                        OnList?.Invoke(args is not null && i < args.Length ? (int?)args[i] : null);
                    }
                }
            }

            return targetMethod!.Invoke(Real, args);
        }
    }

    [Fact]
    public async Task 搜索接口的每一条路都带上限()
    {
        var home = Directory.CreateTempSubdirectory("wb-search-limit-").FullName;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["Workbench:Home"] = home;
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");

        builder.Services.AddWorkbenchInfrastructure(builder.Configuration);
        builder.Services.AddWorkbenchApplication();

        // 人工凭证访问器 —— 宿主程序里注册，测试里不注册的话
        // 路由表物化会失败（/human-token 端点的参数认不出来）。
        builder.Services.AddSingleton(new HumanTokenAccessor("test-token"));

        // MCP 工具设置服务 —— /api/mcp/tools 端点要它（路由表物化时所有端点的
        // 参数都得认得出来，少注册一个就整张路由表起不来）。
        builder.Services.AddScoped<IMcpToolService, McpToolSettingsService>();

        var limits = new List<int?>();

        // 代理包住真实任务服务：记下每次 ListAsync 的 limit。
        // 注册在最后 —— DI 取最后一个，所以代理生效。
        builder.Services.AddScoped<ITaskService>(sp =>
        {
            var real = new TaskService(
                sp.GetRequiredService<IAppDbContext>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IGitInspector>());

            var proxy = DispatchProxy.Create<ITaskService, RecordingTaskServiceProxy>();
            var recorder = (RecordingTaskServiceProxy)(object)proxy;
            recorder.Real = real;
            recorder.OnList = limit => limits.Add(limit);

            return proxy;
        });

        var app = builder.Build();

        // 建库（宿主程序里的初始化器负责，测试里自己来一遍）
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkbenchDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        app.MapWorkbenchApi();

        // 测试里把异常抓出来看（生产是错误页，测试里要看到真因）
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync(ex.ToString(), ctx.RequestAborted);
            }
        });

        await app.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            var baseUrl = addresses!.Addresses.First();

            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            using var response = await client.GetAsync("/api/search?q=REQ");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

            Assert.NotEmpty(limits);
            Assert.All(limits, limit => Assert.NotNull(limit));

            // 关键词与编号两路都受 take 约束（take 上限 100）
            Assert.All(limits.Take(2), limit => Assert.InRange(limit!.Value, 1, 100));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(home, recursive: true);
        }
    }
}
