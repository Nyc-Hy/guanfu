using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;

namespace Guanfu.Tests;

/// <summary>
/// 数据库调用记录：SQL 原文、目标库、目标表要能筛能查。
///
/// 这条链路的价值全在「查得动」—— 摊成列、按条件过滤、服务端分页。
/// </summary>
public sealed class DbCallRecordTests
{
    [Fact]
    public async Task 记录的字段原样落库()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(new DbCallRecordEntry(
            AuditId: null,
            ProjectId: project.Id,
            TaskId: null,
            SessionId: null,
            ConnectionId: null,
            ToolName: "db_execute",
            Server: "srv01",
            Database: "shop",
            Sql: "UPDATE dbo.Orders SET Total = 1 WHERE Id = 7",
            Kind: "Write",
            TargetTables: new[] { "dbo.Orders" },
            RowsAffected: 1,
            Outcome: "success",
            ApprovalId: Guid.NewGuid(),
            CallerId: "agent-x",
            DurationMs: 42));

        var row = await host.Db.DbCallRecords.AsNoTracking().FirstAsync();

        Assert.Equal("db_execute", row.ToolName);
        Assert.Equal("srv01", row.Server);
        Assert.Equal("shop", row.Database);
        Assert.Equal("UPDATE dbo.Orders SET Total = 1 WHERE Id = 7", row.Sql);
        Assert.Equal("Write", row.Kind);
        Assert.Equal("dbo.Orders", row.TargetTables);
        Assert.Equal(1, row.RowsAffected);
        Assert.Equal("agent-x", row.CallerId);
        Assert.Equal(42, row.DurationMs);
    }

    [Fact]
    public async Task 能按表筛出记录()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(Entry(project.Id, "db_query", "SELECT 1", "Read", new[] { "dbo.Users" }));
        await service.RecordAsync(Entry(project.Id, "db_query", "SELECT 2", "Read", new[] { "dbo.Orders" }));

        var (rows, total) = await service.ListAsync(table: "dbo.Orders");

        Assert.Equal(1, total);
        Assert.Single(rows);
        Assert.Equal("SELECT 2", rows[0].Sql);
    }

    [Fact]
    public async Task 能按读写类型筛()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(Entry(project.Id, "db_query", "SELECT 1", "Read", new[] { "dbo.Users" }));
        await service.RecordAsync(Entry(project.Id, "db_execute", "UPDATE x", "Write", new[] { "dbo.Users" }));

        var (rows, total) = await service.ListAsync(kind: "Write");

        Assert.Equal(1, total);
        Assert.Equal("db_execute", rows[0].ToolName);
    }

    [Fact]
    public async Task 分页是真的服务端分页()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        for (var i = 0; i < 7; i++)
        {
            await service.RecordAsync(
                Entry(project.Id, "db_query", $"SELECT {i}", "Read", new[] { "dbo.Users" }));
        }

        var (rows, total) = await service.ListAsync(limit: 3, offset: 0);

        Assert.Equal(7, total);
        Assert.Equal(3, rows.Count);

        var (second, _) = await service.ListAsync(limit: 3, offset: 3);

        Assert.Equal(3, second.Count);
        Assert.NotEqual(rows[0].Sql, second[0].Sql);
    }

    [Fact]
    public async Task 按项目筛不会串到别的项目()
    {
        using var host = new GuanfuTestHost();
        var first = await host.Projects.CreateAsync("甲项目");
        var second = await host.Projects.CreateAsync("乙项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(Entry(first.Id, "db_query", "SELECT 1", "Read", new[] { "dbo.Users" }));
        await service.RecordAsync(Entry(second.Id, "db_query", "SELECT 2", "Read", new[] { "dbo.Users" }));

        var (rows, total) = await service.ListAsync(projectId: first.Id);

        Assert.Equal(1, total);
        Assert.Equal("SELECT 1", rows[0].Sql);
        Assert.Equal(first.Id, rows[0].ProjectId);
    }

    [Fact]
    public async Task 目标表拆回列表()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("查库项目");
        var service = new DbCallService(host.Db, host.Clock);

        await service.RecordAsync(
            Entry(project.Id, "db_execute", "UPDATE a", "Write", new[] { "dbo.A", "dbo.B" }));

        var (rows, _) = await service.ListAsync();

        Assert.Equal(2, rows[0].TargetTables.Count);
        Assert.Contains("dbo.A", rows[0].TargetTables);
        Assert.Contains("dbo.B", rows[0].TargetTables);
    }

    private static DbCallRecordEntry Entry(
        Guid projectId,
        string tool,
        string sql,
        string kind,
        string[] tables)
        => new(
            AuditId: null,
            ProjectId: projectId,
            TaskId: null,
            SessionId: null,
            ConnectionId: null,
            ToolName: tool,
            Server: "srv",
            Database: "db",
            Sql: sql,
            Kind: kind,
            TargetTables: tables,
            RowsAffected: null,
            Outcome: "success",
            ApprovalId: null,
            CallerId: "agent",
            DurationMs: 1);
}
