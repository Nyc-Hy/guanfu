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
            options.EnableDetailedErrors();
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
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddScoped<ISqlCommandRunner, SqlServerCommandRunner>();

        return services;
    }
}
