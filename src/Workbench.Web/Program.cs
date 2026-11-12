using Workbench.Application;
using Workbench.Infrastructure;
using Workbench.Mcp;
using Workbench.Web;
using Workbench.Web.Api;
using Workbench.Web.Components;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// REST API：给 CI / webhook / 脚本
app.MapWorkbenchApi();

// MCP 端点：给外部 agent（Streamable HTTP）
app.MapMcp("/mcp");

// 初始化数据库（建表 + 默认工作区）
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<WorkbenchInitializer>().InitializeAsync();
}

app.Run();

/// <summary>供集成测试引用的入口标记。</summary>
public partial class Program;
