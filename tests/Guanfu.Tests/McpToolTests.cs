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

        // 四个分组都得有东西
        Assert.Contains(catalog, t => t.Category == "观复组");
        Assert.Contains(catalog, t => t.Category == "文档组");
        Assert.Contains(catalog, t => t.Category == "数据库组");
        Assert.Contains(catalog, t => t.Category == "通用组");

        Assert.Contains(catalog, t => t.Name == "guanfu_get_context");
        Assert.Contains(catalog, t => t.Name == "doc_list_struck");
        Assert.Contains(catalog, t => t.Name == "db_execute");
        Assert.Contains(catalog, t => t.Name == "shell_run");
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

        foreach (var name in new[] { "db_execute", "git_commit", "git_push", "shell_run" })
        {
            Assert.True(catalog.Single(t => t.Name == name).RequiresApproval, name);
        }

        // 读类工具不该被标
        Assert.False(catalog.Single(t => t.Name == "db_query").RequiresApproval);
        Assert.False(catalog.Single(t => t.Name == "fs_read").RequiresApproval);
    }

    [Fact]
    public void 工具名必须带组前缀()
    {
        // 工具名按前缀分组（guanfu_ / doc_ / db_ / 通用组）。
        // 通用组这几个没有组前缀，但它们是**固定清单** ——
        // 以前 `project_create`、`milestone_create`、`approval_revoke` 三个
        // 没带前缀，落到「通用组」，跟「跑命令」的工具混在一堆，
        // 还让 agent 按前缀分组时找不到它们。
        var generalGroup = new HashSet<string>(StringComparer.Ordinal)
        {
            "fs_list", "fs_read", "git_commit", "git_push", "shell_run"
        };

        var prefixes = new[] { "guanfu_", "doc_", "db_" };

        foreach (var tool in Catalog())
        {
            var hasPrefix = prefixes.Any(p => tool.Name.StartsWith(p, StringComparison.Ordinal));
            Assert.True(
                hasPrefix || generalGroup.Contains(tool.Name),
                $"工具 {tool.Name} 既没有组前缀，也不在通用组清单里 —— " +
                "要么给它加 guanfu_ 前缀，要么把它加进清单，别留成野生工具。");
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

        var updated = await service.SetEnabledAsync("shell_run", false);

        Assert.False(updated.Enabled);

        var after = await service.ListAsync();
        Assert.False(after.Single(t => t.Name == "shell_run").Enabled);
        Assert.True(after.Single(t => t.Name == "db_query").Enabled);
    }

    [Fact]
    public async Task 开关会落库并影响内存态()
    {
        using var host = new GuanfuTestHost();
        var state = host.ToolState;
        var service = new McpToolSettingsService(host.Db, host.Clock, state, Catalog());

        Assert.True(state.IsEnabled("shell_run"));

        await service.SetEnabledAsync("shell_run", false);

        Assert.False(state.IsEnabled("shell_run"));

        var rows = await host.Db.McpToolSettings.ToListAsync();
        Assert.Single(rows);
        Assert.Equal("shell_run", rows[0].ToolName);
        Assert.False(rows[0].Enabled);
    }

    [Fact]
    public async Task 恢复默认会清掉所有改动()
    {
        using var host = new GuanfuTestHost();
        var state = host.ToolState;
        var service = new McpToolSettingsService(host.Db, host.Clock, state, Catalog());

        await service.SetEnabledAsync("shell_run", false);
        await service.SetEnabledAsync("db_query", false);

        var after = await service.ResetAsync();

        Assert.All(after, t => Assert.True(t.Enabled));
        Assert.Empty(await host.Db.McpToolSettings.ToListAsync());
        Assert.True(state.IsEnabled("shell_run"));
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
