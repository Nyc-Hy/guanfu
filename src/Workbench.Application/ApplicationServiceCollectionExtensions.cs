using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Services;

namespace Workbench.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IToolAuditService, ToolAuditService>();
        services.AddScoped<IDbConnectionService, DbConnectionService>();

        return services;
    }
}
