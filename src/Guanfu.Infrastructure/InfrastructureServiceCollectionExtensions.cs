using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Documents;
using Guanfu.Domain;

namespace Guanfu.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddGuanfuInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GuanfuOptions>(options =>
        {
            // 新键优先；旧键（改名之前配的）仍然认 —— 改名不该让人以为配置丢了
            var home = configuration["Guanfu:Home"] ?? configuration["Workbench:Home"];
            options.HomeDirectory = string.IsNullOrWhiteSpace(home)
                ? GuanfuOptions.DefaultHomeDirectory()
                : Path.GetFullPath(home);

            // 审计链头的外部归档路径。必须在本机之外（部署方自己保证），
            // 但不能落在观复的数据目录里 —— 那种路径运行时会拒掉。
            var archive = configuration["Guanfu:AuditArchivePath"]
                          ?? configuration["Workbench:AuditArchivePath"];
            options.AuditArchivePath = string.IsNullOrWhiteSpace(archive) ? null : archive;
        });

        services.AddDbContext<GuanfuDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<GuanfuOptions>>().Value;
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

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<GuanfuDbContext>());

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
        services.AddSingleton<IDeliveryPackageStore, FileDeliveryPackageStore>();
        services.AddSingleton<IGitInspector, GitInspector>();
        services.AddSingleton<IGitWriter, GitWriter>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddScoped<ISqlCommandRunner, SqlServerCommandRunner>();
        services.AddScoped<IAgentLauncher, AgentLauncher>();
        services.AddScoped<IWorkspaceArchiver, WorkspaceArchiver>();
        services.AddScoped<IAuditChainArchiveService, AuditChainArchiver>();

        return services;
    }
}
