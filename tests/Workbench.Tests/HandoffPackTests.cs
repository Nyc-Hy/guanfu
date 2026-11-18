using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Mcp;

namespace Workbench.Tests;

/// <summary>
/// 交接包。
///
/// 交接的难点不是把文件传过去，是**让接手的人知道从哪儿下手**：
/// 上一个 agent 做到哪一步、为什么停下、哪些坑别再踩。
///
/// 这些事实本来就散在工作台里（计划、文档、产物、测试、流水、租约、规约），
/// 交接包把它们打成一份。工作台只放结构化事实 —— 它不生成自然语言总结。
/// </summary>
public sealed class HandoffPackTests
{
    private static HandoffService Service(WorkbenchTestHost host) =>
        new(host.Tasks, host.Reviews, host.Db, host.Clock);

    [Fact]
    public async Task 带上上一个是谁与为什么()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "要交接的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", note: "先摸清楚接口");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "卡在依赖的服务没起来");

        var pack = await Service(host).GetAsync(task.Id);

        Assert.NotNull(pack.Handoff);
        Assert.Equal("agent-a", pack.Handoff!.PreviousHolder);
        Assert.Equal("卡在依赖的服务没起来", pack.Handoff.Reason);
        Assert.NotNull(pack.Handoff.At);
    }

    [Fact]
    public async Task 放手没写理由时退回接活时的上下文()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "要交接的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", note: "先摸清楚接口");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a");

        var pack = await Service(host).GetAsync(task.Id);

        // 放手时没写 —— 至少还有他接活时留的那句
        Assert.Equal("先摸清楚接口", pack.Handoff!.Reason);
    }

    [Fact]
    public async Task 没转过手就没有上一个()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "全新的活");

        var pack = await Service(host).GetAsync(task.Id);

        Assert.NotNull(pack.Handoff);
        Assert.Null(pack.Handoff!.PreviousHolder);
        Assert.Null(pack.Task.ClaimedBy);
    }

    [Fact]
    public async Task 接手的人要拿得到计划与规约()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "要交接的活");

        await host.Specs.UpdateAsync(project.Id, new ProjectSpecInput(
            Purpose: "12月版本要支持登录",
            DefinitionOfDone: ["有单测覆盖新增逻辑", "文档里的用法同步更新"],
            TestCommand: "dotnet test"));

        await host.Tasks.SetPlanAsync(
            task.Id, "背景", "新增 /api/login 端点", "1) 建 DTO 2) 写服务", "错误密码返回 401", null, "agent-a");

        var pack = await Service(host).GetAsync(task.Id);

        Assert.NotNull(pack.Plan);
        Assert.Contains("/api/login", pack.Plan!.Understanding);
        Assert.Equal("12月版本要支持登录", pack.Spec!.Purpose);
        Assert.Equal(2, pack.Spec.DefinitionOfDone.Count);
        Assert.Equal("dotnet test", pack.Spec.TestCommand);
    }

    [Fact]
    public async Task 带上最近一次测试与产出()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "要交接的活");

        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.CodeChange, "补丁", "diff --git");
        await host.Tasks.ReportTestResultAsync(task.Id, 10, 9, "用例A失败", 0.9d, "agent-a");

        var pack = await Service(host).GetAsync(task.Id);

        // 产出里除了代码补丁，还有一份测试报告（回写测试结果会留一份）
        Assert.Equal(2, pack.Artifacts.Count);
        Assert.NotNull(pack.LastTestResult);
        Assert.Equal(9, pack.LastTestResult!.Passed);
        Assert.Equal(10, pack.LastTestResult.Total);
    }

    [Fact]
    public async Task 交接链看得见每次转手()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "转手多次的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "第一段做完了");

        await host.Tasks.ClaimAsync(task.Id, "agent-b");
        await host.Tasks.ReleaseAsync(task.Id, "agent-b", "第二段也做完了");

        await host.Tasks.ClaimAsync(task.Id, "agent-c");

        var pack = await Service(host).GetAsync(task.Id);

        Assert.Equal(3, pack.HandoffChain.Count);

        // 第一次：a 接手
        Assert.Null(pack.HandoffChain[0].From);
        Assert.Equal("agent-a", pack.HandoffChain[0].To);

        // 第二次：a 交给 b，理由是 a 写的那句
        Assert.Equal("agent-a", pack.HandoffChain[1].From);
        Assert.Equal("agent-b", pack.HandoffChain[1].To);
        Assert.Equal("第一段做完了", pack.HandoffChain[1].Reason);

        // 第三次：b 交给 c
        Assert.Equal("agent-b", pack.HandoffChain[2].From);
        Assert.Equal("agent-c", pack.HandoffChain[2].To);
        Assert.Equal("第二段也做完了", pack.HandoffChain[2].Reason);
    }

    [Fact]
    public async Task 放下了还没人接手链上看得见()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "放下就没接的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "我去干别的了");

        var pack = await Service(host).GetAsync(task.Id);

        var last = pack.HandoffChain[^1];
        Assert.Equal("agent-a", last.From);
        Assert.Null(last.To);
        Assert.Equal("我去干别的了", last.Reason);
    }

    [Fact]
    public async Task MCP工具能取交接包()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "要交接的活");

        await host.Tasks.ClaimAsync(task.Id, "agent-a", note: "开工");
        await host.Tasks.ReleaseAsync(task.Id, "agent-a", "换个人试试");

        var tools = new WorkbenchMcpTools(
            host.Projects, host.Tasks, host.Approvals, host.Audit, host.Dependencies, host.Exports,
            handoff: Service(host));

        var result = await tools.GetHandoffPackAsync(task.Id);

        Assert.True(result.Ok);
        Assert.NotNull(result.Pack);
        Assert.Equal("agent-a", result.Pack!.Handoff!.PreviousHolder);
        Assert.Equal("换个人试试", result.Pack.Handoff.Reason);
    }
}
