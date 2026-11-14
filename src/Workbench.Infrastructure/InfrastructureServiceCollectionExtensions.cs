using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Documents;
using Workbench.Domain;

namespace Workbench.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WorkbenchOptions>(options =>
        {
            var home = configuration["Workbench:Home"];
            options.HomeDirectory = string.IsNullOrWhiteSpace(home)
                ? WorkbenchOptions.DefaultHomeDirectory()
                : Path.GetFullPath(home);
        });

        services.AddDbContext<WorkbenchDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<WorkbenchOptions>>().Value;
            Directory.CreateDirectory(settings.HomeDirectory);

            options.UseSqlite($"Data Source={settings.DatabasePath}");

            // 每条连接打开时开 WAL + busy_timeout（连接级设置，必须逐连接执行）
            options.AddInterceptors(new SqliteWalInterceptor());

            // 详细错误会把参数值等敏感内容带进异常消息 —— 只在 Development 开。
            // 不引 IHostEnvironment（Infrastructure 不必依赖 Hosting 抽象），
            // 直接读 ASP.NET Core / .NET 通用的环境变量，口径与宿主一致。
            var environment =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

            if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                options.EnableDetailedErrors();
            }
        });

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<WorkbenchDbContext>());

        // 文档解析器
        services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();
        services.AddSingleton<IDocumentParser, DocxDocumentParser>();
        services.AddSingleton<IDocumentParser, PdfDocumentParser>();
        services.AddSingleton<IDocumentParser, DiffDocumentParser>();
        services.AddSingleton<IDocumentParser, JsonDocumentParser>();
        services.AddSingleton<IDocumentParser, HtmlDocumentParser>();
        services.AddSingleton<IDocumentParser, PlainTextDocumentParser>();
        services.AddSingleton<IDocumentParserRegistry, DocumentParserRegistry>();

        services.AddSingleton<IDocumentStore, FileDocumentStore>();
        services.AddSingleton<IGitInspector, GitInspector>();
        services.AddSingleton<IGitWriter, GitWriter>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddScoped<ISqlCommandRunner, SqlServerCommandRunner>();
        services.AddScoped<IAgentLauncher, AgentLauncher>();
        services.AddScoped<IWorkspaceArchiver, WorkspaceArchiver>();

        return services;
    }
}
