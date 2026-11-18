using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 表级规则（免审批写）。
///
/// 这张表以前在库里却没人读它 —— 语义等于不存在。
/// 现在它真的管事，所以必须有测试钉住「配了就免、没配就要审、认错表就要审」。
/// </summary>
public class DbTableRuleTests
{
    [Fact]
    public async Task 没配规则时写操作一律要审批()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var connection = await host.DbConnections.CreateAsync(project.Id, "default", "srv", "db", userId: null, password: null);

        Assert.True(await host.DbConnections.RequiresApprovalAsync(connection.Id, "Orders"));
    }

    [Fact]
    public async Task 配了免审批的表就不用审批()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var connection = await host.DbConnections.CreateAsync(project.Id, "default", "srv", "db", userId: null, password: null);

        await host.DbConnections.AddRuleAsync(connection.Id, "dbo", "AuditLog", allowRead: true, writeRequiresApproval: false);

        Assert.False(await host.DbConnections.RequiresApprovalAsync(connection.Id, "AuditLog"));
    }

    [Fact]
    public async Task 没配的那张表照样要审批()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var connection = await host.DbConnections.CreateAsync(project.Id, "default", "srv", "db", userId: null, password: null);

        await host.DbConnections.AddRuleAsync(connection.Id, "dbo", "AuditLog", writeRequiresApproval: false);

        // 免审批是**按表**给的，不是给整个连接的
        Assert.True(await host.DbConnections.RequiresApprovalAsync(connection.Id, "Orders"));
    }

    [Fact]
    public async Task 认不出目标表时一律要审批()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var connection = await host.DbConnections.CreateAsync(project.Id, "default", "srv", "db", userId: null, password: null);

        await host.DbConnections.AddRuleAsync(connection.Id, "dbo", "AuditLog", writeRequiresApproval: false);

        // 认错表比多问一次糟糕得多
        Assert.True(await host.DbConnections.RequiresApprovalAsync(connection.Id, null));
        Assert.True(await host.DbConnections.RequiresApprovalAsync(connection.Id, "  "));
    }

    [Fact]
    public async Task 表名带schema也能匹配上()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var connection = await host.DbConnections.CreateAsync(project.Id, "default", "srv", "db", userId: null, password: null);

        await host.DbConnections.AddRuleAsync(connection.Id, "dbo", "AuditLog", writeRequiresApproval: false);

        Assert.False(await host.DbConnections.RequiresApprovalAsync(connection.Id, "dbo.AuditLog"));
    }

    [Fact]
    public async Task 规则可以列出来也可以删掉()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var connection = await host.DbConnections.CreateAsync(project.Id, "default", "srv", "db", userId: null, password: null);

        var rule = await host.DbConnections.AddRuleAsync(connection.Id, "dbo", "AuditLog", writeRequiresApproval: false);

        var list = await host.DbConnections.ListRulesAsync(connection.Id);
        Assert.Single(list);
        Assert.Equal("AuditLog", list[0].TableName);
        Assert.False(list[0].WriteRequiresApproval);

        await host.DbConnections.DeleteRuleAsync(connection.Id, rule.Id);

        Assert.Empty(await host.DbConnections.ListRulesAsync(connection.Id));
        Assert.True(await host.DbConnections.RequiresApprovalAsync(connection.Id, "AuditLog"));
    }
}
