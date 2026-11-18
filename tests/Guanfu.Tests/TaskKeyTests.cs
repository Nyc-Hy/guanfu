using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

/// <summary>
/// 任务编号（人嘴里的「req001」）与版本归组（挂里程碑）。
/// </summary>
public class TaskKeyTests
{
    [Fact]
    public async Task 新建任务自动分配编号()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var first = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        var second = await host.Tasks.CreateAsync(project.Id, "写登出接口");

        Assert.Equal("REQ-001", first.Key);
        Assert.Equal("REQ-002", second.Key);
    }

    [Theory]
    [InlineData("req001")]
    [InlineData("REQ-001")]
    [InlineData("Req-001")]
    [InlineData(" req-001 ")]
    public async Task 按编号查任务不认连字符写法的差异(string spoken)
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var found = await host.Tasks.ListAsync(project.Id, key: spoken);

        Assert.Single(found);
        Assert.Equal("REQ-001", found[0].Key);
    }

    [Fact]
    public async Task 编号查不到就是查不到()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var found = await host.Tasks.ListAsync(project.Id, key: "REQ-002");

        Assert.Empty(found);
    }

    [Fact]
    public void 编号只认字母数字的归一化结果()
    {
        Assert.Equal("req001", TaskService.NormalizeKey("REQ-001"));
        Assert.Equal("req001", TaskService.NormalizeKey("req001"));
        Assert.Equal("req001", TaskService.NormalizeKey(" Req_001 "));
        Assert.NotEqual(TaskService.NormalizeKey("REQ-001"), TaskService.NormalizeKey("REQ-01"));
    }

    [Fact]
    public async Task 项目内编号不能重号()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "写登录接口", key: "REQ-001");

        // REQ001 和 REQ-001 归一化后是同一个，不该再放进去
        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Tasks.CreateAsync(project.Id, "重复编号", key: "REQ001"));
    }

    [Fact]
    public async Task 任务可以挂到里程碑下()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");

        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口", milestoneId: milestone.Id);

        Assert.Equal(milestone.Id, task.MilestoneId);

        var inMilestone = await host.Tasks.ListAsync(project.Id, milestoneId: milestone.Id);
        Assert.Single(inMilestone);
        Assert.Equal(task.Id, inMilestone[0].Id);
    }

    [Fact]
    public async Task 里程碑不属于本项目就挂不上()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var other = await host.Projects.CreateAsync("另一个项目");
        var foreignMilestone = await host.Milestones.CreateAsync(other.Id, "别人的版本");

        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => host.Tasks.CreateAsync(project.Id, "写登录接口", milestoneId: foreignMilestone.Id));

        Assert.Contains("不属于项目", ex.Message);
    }
}

/// <summary>
/// git porcelain 输出解析。
///
/// 回归点：porcelain 第一行常以空格开头（" M src/x.cs" 表示工作区已改），
/// 读输出时若把整段 Trim() 掉前导空格，第一条路径就会少一个字符（src/ 变 rc/）。
/// </summary>
public class GitPorcelainPathTests
{
    [Theory]
    [InlineData(" M src/a.cs", "src/a.cs")]
    [InlineData("?? src/b.cs", "src/b.cs")]
    [InlineData("M  src/c.cs", "src/c.cs")]
    [InlineData("A  src/d.cs", "src/d.cs")]
    [InlineData("R  old.cs -> new.cs", "new.cs")]
    [InlineData("?? \"weird name.cs\"", "weird name.cs")]
    public void 解析出路径(string line, string expected)
    {
        Assert.Equal(expected, GitInspector.ParsePorcelainPath(line));
    }

    [Fact]
    public void 太短的行当空处理()
    {
        Assert.Equal(string.Empty, GitInspector.ParsePorcelainPath(" M "));
        Assert.Equal(string.Empty, GitInspector.ParsePorcelainPath(""));
    }

    /// <summary>
    /// 端到端跑一遍真实仓库：改一个已提交的文件，让它排在 porcelain 第一行。
    /// 这正是回归点 —— 读输出时若 Trim() 掉整段的前导空格，
    /// 第一条路径就会从 "src/a.cs" 变成 "rc/a.cs"。
    /// </summary>
    [Fact]
    public void 真实仓库里第一个变更文件路径不丢字符()
    {
        var repo = Path.Combine(Path.GetTempPath(), "wb-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        try
        {
            RunGit(repo, "init");
            RunGit(repo, "config", "user.email", "test@example.com");
            RunGit(repo, "config", "user.name", "test");

            File.WriteAllText(Path.Combine(repo, "a.cs"), "// v1\n");
            RunGit(repo, "add", ".");
            RunGit(repo, "commit", "-m", "init");

            // 改动已提交的文件 —— porcelain 里是 " M a.cs"，第一行以空格开头
            File.WriteAllText(Path.Combine(repo, "a.cs"), "// v2\n");

            var state = new GitInspector().Inspect(repo);

            Assert.True(state.ChangedFilePaths.Contains("a.cs"),
                $"变更文件列表不对: {string.Join(", ", state.ChangedFilePaths)}");
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static void RunGit(string repoPath, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit(15_000);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} 失败: {process.StandardError.ReadToEnd()}");
    }
}
