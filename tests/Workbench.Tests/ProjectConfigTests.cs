using Workbench.Domain;

namespace Workbench.Tests;

public class ProjectConfigTests
{
    [Fact]
    public async Task 同一个仓库绑两个项目时要给出提醒()
    {
        using var host = new WorkbenchTestHost();

        await host.Projects.CreateAsync("甲项目", repoPath: "/tmp/wb-唯一仓库");
        var second = await host.Projects.CreateAsync("乙项目", repoPath: "/tmp/wb-唯一仓库");

        // 允许绑（前后端同仓就绑各的子目录），但要把「agent 按目录认项目会分不出来」
        // 这件事在绑定的那一刻讲清楚，别等 agent 撞墙才发现。
        var config = await host.Projects.GetConfigAsync(second.Id);

        Assert.NotNull(config.RepoBindingWarning);
        Assert.Contains("甲项目", config.RepoBindingWarning);
        Assert.Contains("分不出来", config.RepoBindingWarning);
    }

    [Fact]
    public async Task 改配置绑到别的项目的仓库也要给提醒()
    {
        using var host = new WorkbenchTestHost();

        await host.Projects.CreateAsync("甲项目", repoPath: "/tmp/wb-唯一仓库二");
        var second = await host.Projects.CreateAsync("乙项目");

        var updated = await host.Projects.UpdateConfigAsync(second.Id, repoPath: "/tmp/wb-唯一仓库二");

        Assert.NotNull(updated.RepoBindingWarning);
        Assert.Contains("甲项目", updated.RepoBindingWarning);
    }

    [Fact]
    public async Task 同一个项目重绑自己的仓库不算重复()
    {
        using var host = new WorkbenchTestHost();

        var project = await host.Projects.CreateAsync("甲项目", repoPath: "/tmp/wb-唯一仓库三");

        var updated = await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/wb-唯一仓库三");

        Assert.Equal("/tmp/wb-唯一仓库三", updated.RepoPath);
        Assert.Null(updated.RepoBindingWarning);
    }

    [Fact]
    public async Task 没绑仓库的项目没有提醒()
    {
        using var host = new WorkbenchTestHost();

        var project = await host.Projects.CreateAsync("甲项目");

        var config = await host.Projects.GetConfigAsync(project.Id);

        Assert.Null(config.RepoBindingWarning);
    }

    [Fact]
    public async Task 不同仓库可以各绑各的项目()
    {
        using var host = new WorkbenchTestHost();

        await host.Projects.CreateAsync("甲项目", repoPath: "/tmp/wb-仓库甲");
        await host.Projects.CreateAsync("乙项目", repoPath: "/tmp/wb-仓库乙");

        var projects = await host.Projects.ListAsync();
        Assert.Equal(2, projects.Count);
    }

    [Fact]
    public async Task 更新配置后能读回来()    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var updated = await host.Projects.UpdateConfigAsync(
            project.Id,
            repoPath: "/tmp/repo",
            agentCommand: "agent -p \"{title}\"",
            allowedCommands: new[] { "dotnet test", "npm run build" },
            notificationWebhookUrl: "https://example.com/hook",
            notificationFormat: "dingtalk");

        Assert.Equal("/tmp/repo", updated.RepoPath);
        Assert.Equal("agent -p \"{title}\"", updated.AgentCommand);
        Assert.Equal(new[] { "dotnet test", "npm run build" }, updated.AllowedCommands);
        Assert.Equal("dingtalk", updated.NotificationFormat);

        var reloaded = await host.Projects.GetConfigAsync(project.Id);
        Assert.Equal(updated.AllowedCommands, reloaded.AllowedCommands);
        Assert.Equal("https://example.com/hook", reloaded.NotificationWebhookUrl);
    }

    [Fact]
    public async Task 空字符串清空字段()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, agentCommand: "agent run");

        var cleared = await host.Projects.UpdateConfigAsync(project.Id, agentCommand: string.Empty);

        Assert.Null(cleared.AgentCommand);
    }

    [Fact]
    public async Task 空白名单数组等于清空白名单()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, allowedCommands: new[] { "dotnet test" });

        var cleared = await host.Projects.UpdateConfigAsync(project.Id, allowedCommands: Array.Empty<string>());

        Assert.Empty(cleared.AllowedCommands);
    }

    [Fact]
    public async Task 未知通知格式被拒绝()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Projects.UpdateConfigAsync(project.Id, notificationFormat: "wechat"));
    }

    [Fact]
    public async Task 不存在的项目报未找到()
    {
        using var host = new WorkbenchTestHost();

        await Assert.ThrowsAsync<NotFoundException>(
            () => host.Projects.GetConfigAsync(Guid.NewGuid()));
    }
}
