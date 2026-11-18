using Microsoft.Extensions.DependencyInjection;
using Guanfu.Application.Services;

namespace Guanfu.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddGuanfuApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IApprovalGate, ApprovalGate>();
        services.AddScoped<IToolAuditService, ToolAuditService>();
        services.AddScoped<IDbConnectionService, DbConnectionService>();
        services.AddScoped<IMilestoneService, MilestoneService>();
        services.AddScoped<IMetricsService, MetricsService>();
        services.AddScoped<IFileSystemService, FileSystemService>();

        // 工位解析：文件 / git / shell 工具按任务登记的工位走
        services.AddScoped<IWorkstationResolver, WorkstationResolver>();

        // 工位清单与放行：worktree 建了没人收 —— 得有人能看见、能放掉
        services.AddScoped<IWorkstationService, WorkstationService>();
        services.AddScoped<IReviewService, ReviewService>();
        // 评审服务同时是门禁的实现 —— 注册同一个实例，别造两份
        services.AddScoped<IReviewGate>(sp => sp.GetRequiredService<IReviewService>());
        services.AddScoped<IBranchGuard, BranchGuard>();
        services.AddScoped<IShellRunner, ShellRunner>();
        services.AddScoped<ITaskDependencyService, TaskDependencyService>();
        services.AddScoped<IExportService, ExportService>();

        // agent 会话：把「agent 这次干活」跟「项目 / 任务」接上。
        services.AddScoped<IAgentSessionService, AgentSessionService>();

        // 作用域解析：一次工具调用算在谁头上。宿主（HTTP 层）提供当前会话，
        // 这里只管解析规则。
        services.AddScoped<IProjectScopeResolver, ProjectScopeResolver>();

        // 产出文件：观复自己去 git 里数出「这个任务改了哪些文件」，
        // 不靠 agent 自报。
        services.AddScoped<IFileChangeService, FileChangeService>();

        // 数据库调用记录：把 SQL 原文、目标库、目标表摊成列，查得动。
        services.AddScoped<IDbCallService, DbCallService>();

        // 首页总览：现在什么在动、等我批什么、最近产出了什么、最近碰了什么库。
        services.AddScoped<IOverviewService, OverviewService>();

        // 项目级规约：人填、agent 读
        services.AddScoped<IProjectSpecService, ProjectSpecService>();

        // 调度视图：现在什么能干、什么卡着、谁在干
        services.AddScoped<IDispatchService, DispatchService>();

        // 用量与开销：观复自己数得出来的（工具调用次数）+ agent 自报的（token / 费用）
        services.AddScoped<ITaskUsageService, TaskUsageService>();

        // 交接包：把散在各处的事实打成一份，接手的人一次拿全
        services.AddScoped<IHandoffService, HandoffService>();

        // 通知：单例 HttpClient（HttpClient 复用是对的，每次 new 才会耗尽端口）
        // 超时压短，通知失败不能拖住审批请求。
        services.AddSingleton<INotifier>(_ => new WebhookNotifier(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        }));

        return services;
    }
}
