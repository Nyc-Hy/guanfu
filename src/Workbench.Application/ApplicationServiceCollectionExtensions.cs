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
        services.AddScoped<IMilestoneService, MilestoneService>();
        services.AddScoped<IMetricsService, MetricsService>();
        services.AddScoped<IFileSystemService, FileSystemService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IReviewGate, ReviewService>();
        services.AddScoped<IBranchGuard, BranchGuard>();
        services.AddScoped<IShellRunner, ShellRunner>();

        // 通知：单例 HttpClient（HttpClient 复用是对的，每次 new 才会耗尽端口）
        // 超时压短，通知失败不能拖住审批请求。
        services.AddSingleton<INotifier>(_ => new WebhookNotifier(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        }));

        return services;
    }
}
