using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Infrastructure;
using Workbench.Mcp;

namespace Workbench.Web;

/// <summary>启动时初始化数据库（建表 + 默认工作区），并加载 MCP 工具开关。</summary>
public sealed class WorkbenchInitializer(
    WorkbenchDbContext db,
    McpToolState toolState,
    IApprovalService approvals)
{
    /// <summary>
    /// <paramref name="processStartedAt"/> 是**本次进程**启动的时刻。
    ///
    /// 拿它跟审批的 ExecutingStartedAt 比，是为了找出「上一个进程崩在执行中间」
    /// 的那些审批：那次写操作可能已经生效了，状态却停在 Executing。
    /// 这种要标成「结果未知」交人工确认，不能悄悄重跑一遍。
    /// </summary>
    public async Task InitializeAsync(
        DateTimeOffset processStartedAt,
        CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        // EnsureCreated 不会给已存在的表补列，老库得补一次 schema 补丁
        await SchemaUpgrader.UpgradeAsync(db, cancellationToken);

        if (!await db.Workspaces.AnyAsync(cancellationToken))
        {
            db.Workspaces.Add(new Workbench.Domain.Workspace
            {
                Name = "默认工作区",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        // 工具开关要先加载好：不然服务刚起来的那一下，工具清单是「全开」的
        await toolState.RefreshAsync(cancellationToken);

        // 重启对账：把上次进程崩在执行中间的审批标出来（结果未知）
        var interrupted = await approvals.ReconcileInterruptedAsync(processStartedAt, cancellationToken);
        if (interrupted > 0)
        {
            Console.WriteLine(
                $"⚠ 有 {interrupted} 条审批在服务重启时被中断（结果未知），" +
                "请到「审批」页人工确认后再让 agent 重新申请。");
        }
    }
}
