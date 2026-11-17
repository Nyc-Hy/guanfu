using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 任务类型（需求/缺陷/优化）与分支绑定。
///
/// 结构是 项目 → 迭代版本 → 需求/缺陷/优化；
/// 任务项绑了分支，就只有仓库当前就在那个分支上才会运行。
/// </summary>
public class TaskTypeAndBranchTests
{
    // ── 类型与编号 ────────────────────────────────────────────────────

    [Fact]
    public async Task 各类型编号前缀不同且独立计数()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var req1 = await host.Tasks.CreateAsync(project.Id, "登录接口", type: TaskType.Requirement);
        var bug1 = await host.Tasks.CreateAsync(project.Id, "登录报错", type: TaskType.Bug);
        var opt1 = await host.Tasks.CreateAsync(project.Id, "登录提速", type: TaskType.Optimization);
        var req2 = await host.Tasks.CreateAsync(project.Id, "登出接口", type: TaskType.Requirement);
        var bug2 = await host.Tasks.CreateAsync(project.Id, "登出报错", type: TaskType.Bug);

        Assert.Equal("REQ-001", req1.Key);
        Assert.Equal("BUG-001", bug1.Key);
        Assert.Equal("OPT-001", opt1.Key);

        // 各类型独立计数：需求到 002 了，缺陷还是 001
        Assert.Equal("REQ-002", req2.Key);
        Assert.Equal("BUG-002", bug2.Key);
    }

    [Fact]
    public async Task 老数据不受类型前缀影响()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        // 手填的 REQ-001 已经在（老库里的存量数据就是这个样子）
        await host.Tasks.CreateAsync(project.Id, "老任务", key: "REQ-001");

        var next = await host.Tasks.CreateAsync(project.Id, "新需求", type: TaskType.Requirement);

        Assert.Equal("REQ-002", next.Key);
    }

    [Fact]
    public async Task 可以按类型过滤()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Tasks.CreateAsync(project.Id, "需求一", type: TaskType.Requirement);
        await host.Tasks.CreateAsync(project.Id, "缺陷一", type: TaskType.Bug);
        await host.Tasks.CreateAsync(project.Id, "优化一", type: TaskType.Optimization);

        var bugs = await host.Tasks.ListAsync(project.Id, type: TaskType.Bug);

        Assert.Single(bugs);
        Assert.Equal("缺陷一", bugs[0].Title);
        Assert.Equal(TaskType.Bug, bugs[0].Type);
    }

    [Fact]
    public async Task 改类型时编号保持不变()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "登录接口");

        var changed = await host.Tasks.SetTypeAsync(task.Id, TaskType.Optimization);

        Assert.Equal(TaskType.Optimization, changed.Type);
        Assert.Equal("REQ-001", changed.Key);
    }

    [Theory]
    [InlineData("requirement", TaskType.Requirement)]
    [InlineData("需求", TaskType.Requirement)]
    [InlineData("bug", TaskType.Bug)]
    [InlineData("缺陷", TaskType.Bug)]
    [InlineData("optimization", TaskType.Optimization)]
    [InlineData("优化", TaskType.Optimization)]
    public void 类型名认英文也认中文(string text, TaskType expected)
    {
        Assert.True(TaskTypes.TryParse(text, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void 认不出的类型名不默默当需求()
    {
        Assert.False(TaskTypes.TryParse("feature-request", out _));
        Assert.False(TaskTypes.TryParse(null, out _));
        Assert.False(TaskTypes.TryParse("  ", out _));
    }

    // ── 分支绑定 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 绑了分支且当前分支不同则不许领取()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Branch = "main";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");

        var ex = await Assert.ThrowsAsync<BranchMismatchException>(
            () => host.Tasks.ClaimAsync(task.Id, "agent-1"));

        Assert.Contains("feature/login", ex.Message);
        Assert.Contains("main", ex.Message);
        Assert.Equal("feature/login", ex.Check.BoundBranch);
        Assert.Equal("main", ex.Check.CurrentBranch);
    }

    [Fact]
    public async Task 绑了分支且当前分支相同则可以领取()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Branch = "feature/login";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");

        var claimed = await host.Tasks.ClaimAsync(task.Id, "agent-1");

        Assert.Equal("agent-1", claimed.Task.ClaimedBy);
    }

    [Fact]
    public async Task 绑了分支且当前分支不同则不许进开发中()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Branch = "main";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);

        await Assert.ThrowsAsync<BranchMismatchException>(
            () => host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev));

        var after = await host.Tasks.GetAsync(task.Id);
        Assert.Equal(TaskStatus.Planned, after!.Status);
    }

    [Fact]
    public async Task 没绑分支就不受分支约束()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Branch = "main";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口");

        var claimed = await host.Tasks.ClaimAsync(task.Id, "agent-1");
        Assert.Equal("agent-1", claimed.Task.ClaimedBy);
    }

    [Fact]
    public async Task 绑了分支但读不到当前分支则不放行()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Error = "git 挂了";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");

        var ex = await Assert.ThrowsAsync<BranchMismatchException>(
            () => host.Tasks.ClaimAsync(task.Id, "agent-1"));

        Assert.Contains("不予运行", ex.Message);
    }

    [Fact]
    public async Task 绑了分支但项目没配仓库则不放行()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");

        var ex = await Assert.ThrowsAsync<BranchMismatchException>(
            () => host.Tasks.ClaimAsync(task.Id, "agent-1"));

        Assert.Contains("仓库路径", ex.Message);
    }

    [Fact]
    public async Task 解绑分支后不再受限()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Branch = "main";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");
        await host.Tasks.BindBranchAsync(task.Id, null);

        var claimed = await host.Tasks.ClaimAsync(task.Id, "agent-1");
        Assert.Equal("agent-1", claimed.Task.ClaimedBy);
    }

    [Fact]
    public async Task 上下文带出当前分支与就绪状态()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/repo");
        host.Git.Branch = "main";

        var task = await host.Tasks.CreateAsync(project.Id, "登录接口", branch: "feature/login");

        var context = await host.Tasks.GetContextAsync(task.Id);

        Assert.Equal("main", context.CurrentBranch);
        Assert.False(context.BranchReady);
        Assert.Equal("feature/login", context.Task.Branch);
    }

    [Theory]
    [InlineData("feature/login", "feature/login", true)]
    [InlineData("refs/heads/feature/login", "feature/login", true)]
    [InlineData("Feature/Login", "feature/login", true)]
    [InlineData("feature/login", "main", false)]
    public void 分支名比较忽略前缀与大小写(string left, string right, bool expected)
    {
        Assert.Equal(expected, BranchGuard.SameBranch(left, right));
    }
}
