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

        Assert.NotNull(config.RepoWarning);
        Assert.Contains("甲项目", config.RepoWarning);
        Assert.Contains("分不出来", config.RepoWarning);
    }

    [Fact]
    public async Task 改配置绑到别的项目的仓库也要给提醒()
    {
        using var host = new WorkbenchTestHost();

        await host.Projects.CreateAsync("甲项目", repoPath: "/tmp/wb-唯一仓库二");
        var second = await host.Projects.CreateAsync("乙项目");

        var updated = await host.Projects.UpdateConfigAsync(second.Id, repoPath: "/tmp/wb-唯一仓库二");

        Assert.NotNull(updated.RepoWarning);
        Assert.Contains("甲项目", updated.RepoWarning);
    }

    [Fact]
    public async Task 同一个项目重绑自己的仓库不算重复()
    {
        using var host = new WorkbenchTestHost();

        // 用一个真存在的目录：目录不存在会另外给一条提醒，这里只关心「重复绑定」那件事。
        var repo = Directory.CreateTempSubdirectory("wb-唯一仓库三").FullName;
        try
        {
            var project = await host.Projects.CreateAsync("甲项目", repoPath: repo);

            var updated = await host.Projects.UpdateConfigAsync(project.Id, repoPath: repo);

            Assert.Equal(repo, updated.RepoPath);
            Assert.Null(updated.RepoWarning);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task 相对路径要被拒掉()
    {
        using var host = new WorkbenchTestHost();

        // 相对路径会被拿服务端的工作目录去补全 —— 绑出来的是另一个地方，
        // 界面还标着「已绑仓库」。这种错比报错难查，直接拒。
        var error = await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Projects.CreateAsync("甲项目", repoPath: "myrepo"));

        Assert.Contains("绝对路径", error.Message);
    }

    [Fact]
    public async Task 目录还不存在时要提醒()
    {
        using var host = new WorkbenchTestHost();

        var project = await host.Projects.CreateAsync(
            "甲项目", repoPath: "/tmp/wb-还不存在的仓库目录");

        var config = await host.Projects.GetConfigAsync(project.Id);

        Assert.NotNull(config.RepoWarning);
        Assert.Contains("还不存在", config.RepoWarning);
    }

    [Fact]
    public async Task 没绑仓库的项目没有提醒()
    {
        using var host = new WorkbenchTestHost();

        var project = await host.Projects.CreateAsync("甲项目");

        var config = await host.Projects.GetConfigAsync(project.Id);

        Assert.Null(config.RepoWarning);
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
