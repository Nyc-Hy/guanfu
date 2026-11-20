using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;
using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>
/// MCP 工具目录与开关。
/// </summary>
public class McpToolTests
{
    private static IReadOnlyList<McpToolDescriptor> Catalog() =>
        McpToolCatalog.Discover(typeof(GuanfuMcpTools).Assembly);

    [Fact]
    public void 目录能扫出全部工具()
    {
        var catalog = Catalog();

        // 三个分组都得有东西
        Assert.Contains(catalog, t => t.Category == "观复组");
        Assert.Contains(catalog, t => t.Category == "文档组");
        Assert.Contains(catalog, t => t.Category == "数据库组");

        Assert.Contains(catalog, t => t.Name == "guanfu_get_context");
        Assert.Contains(catalog, t => t.Name == "doc_list_struck");
        Assert.Contains(catalog, t => t.Name == "db_execute");
        var retired = new[] { "fs_list", "fs_read", "git_commit", "git_push", "shell_run" };
        Assert.DoesNotContain(catalog, t => retired.Contains(t.Name));
    }

    [Fact]
    public void 工具名不重复()
    {
        var names = Catalog().Select(t => t.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 写类工具被标记为需要审批()
    {
        var catalog = Catalog();

        Assert.True(catalog.Single(t => t.Name == "db_execute").RequiresApproval);
        Assert.False(catalog.Single(t => t.Name == "db_query").RequiresApproval);
    }

    [Fact]
    public void 工具名必须带组前缀()
    {
        var prefixes = new[] { "guanfu_", "doc_", "db_" };

        foreach (var tool in Catalog())
        {
            Assert.True(prefixes.Any(p => tool.Name.StartsWith(p, StringComparison.Ordinal)),
                $"工具 {tool.Name} 缺少组前缀。");
        }
    }

    [Fact]
    public void 工具按分类顺序排列()
    {
        var catalog = Catalog();
        var orders = catalog.Select(t => McpToolCatalog.Categories.ToList().IndexOf(t.Category)).ToList();

        Assert.Equal(orders.OrderBy(o => o).ToList(), orders);
    }

    [Fact]
    public async Task 关掉工具后列表里显示为关闭()
    {
        using var host = new GuanfuTestHost();
        var service = new McpToolSettingsService(host.Db, host.Clock, host.ToolState, Catalog());

        var before = await service.ListAsync();
        Assert.All(before, t => Assert.True(t.Enabled));

        var updated = await service.SetEnabledAsync("db_execute", false);

        Assert.False(updated.Enabled);

        var after = await service.ListAsync();
        Assert.False(after.Single(t => t.Name == "db_execute").Enabled);
        Assert.True(after.Single(t => t.Name == "db_query").Enabled);
    }

    [Fact]
    public async Task 开关会落库并影响内存态()
    {
        using var host = new GuanfuTestHost();
        var state = host.ToolState;
        var service = new McpToolSettingsService(host.Db, host.Clock, state, Catalog());

        Assert.True(state.IsEnabled("db_execute"));

        await service.SetEnabledAsync("db_execute", false);

        Assert.False(state.IsEnabled("db_execute"));

        var rows = await host.Db.McpToolSettings.ToListAsync();
        Assert.Single(rows);
        Assert.Equal("db_execute", rows[0].ToolName);
        Assert.False(rows[0].Enabled);
    }

    [Fact]
    public async Task 恢复默认会清掉所有改动()
    {
        using var host = new GuanfuTestHost();
        var state = host.ToolState;
        var service = new McpToolSettingsService(host.Db, host.Clock, state, Catalog());

        await service.SetEnabledAsync("db_execute", false);
        await service.SetEnabledAsync("db_query", false);

        var after = await service.ResetAsync();

        Assert.All(after, t => Assert.True(t.Enabled));
        Assert.Empty(await host.Db.McpToolSettings.ToListAsync());
        Assert.True(state.IsEnabled("db_execute"));
    }

    [Fact]
    public async Task 不存在的工具名报未找到()
    {
        using var host = new GuanfuTestHost();
        var service = new McpToolSettingsService(host.Db, host.Clock, host.ToolState, Catalog());

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.SetEnabledAsync("no_such_tool", false));
    }
}
