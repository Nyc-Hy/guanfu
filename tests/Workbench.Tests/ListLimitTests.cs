using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;

namespace Workbench.Tests;

/// <summary>
/// 列表限量与筛选的边界。
///
/// 审批页与看板都靠「拉取有上限 + 客户端翻页」才撑得住用久了的库 ——
/// 这两条一旦被改回去（比如去掉 limit），页面会把几千行塞进浏览器。
/// </summary>
public sealed class ListLimitTests
{
    [Fact]
    public async Task 审批列表有上限()
    {
        using var host = new WorkbenchTestHost();

        for (var i = 0; i < 12; i++)
        {
            await host.Approvals.RequestAsync(
                "shell_run", "shell.write", $"echo {i}", $"跑 echo {i}", "agent", null, null);
        }

        var all = await host.Approvals.ListAsync(limit: 5);
        Assert.Equal(5, all.Count);

        var many = await host.Approvals.ListAsync(limit: 100);
        Assert.Equal(12, many.Count);
    }

    [Fact]
    public async Task 审批列表默认限量不是无限()
    {
        using var host = new WorkbenchTestHost();
        var approvals = await host.Approvals.ListAsync();

        // 默认值封顶 —— 不是「全量返回」
        Assert.True(approvals.Count <= 500);
    }

    [Fact]
    public async Task 数据库调用能按库名前缀筛()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(Entry(project.Id, "shop_db"));
        await service.RecordAsync(Entry(project.Id, "warehouse_db"));
        await service.RecordAsync(Entry(project.Id, "other"));

        var (rows, total) = await service.ListAsync(database: "shop");

        Assert.Equal(1, total);
        Assert.Equal("shop_db", rows[0].Database);
    }

    [Fact]
    public async Task 数据库调用按库名前缀筛不会串到别的库()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(Entry(project.Id, "warehouse_db"));

        var (_, total) = await service.ListAsync(database: "shop");

        Assert.Equal(0, total);
    }

    private static DbCallRecordEntry Entry(Guid projectId, string database)
        => new(
            AuditId: null,
            ProjectId: projectId,
            TaskId: null,
            SessionId: null,
            ConnectionId: null,
            ToolName: "db_query",
            Server: "srv",
            Database: database,
            Sql: "SELECT 1",
            Kind: "Read",
            TargetTables: new[] { "dbo.Users" },
            RowsAffected: null,
            Outcome: "success",
            ApprovalId: null,
            CallerId: "agent",
            DurationMs: 1);
}
