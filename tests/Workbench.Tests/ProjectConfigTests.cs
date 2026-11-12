using Workbench.Domain;

namespace Workbench.Tests;

public class ProjectConfigTests
{
    [Fact]
    public async Task 更新配置后能读回来()
    {
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
