using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 规约（DoD）的逐条判定。
///
/// 七个维度是通用的，规约是这个项目特有的 —— 评审时要一条一条对着判。
/// 没判完 / 判成不满足，**不许结项**：不然规约只是一段摆设文字。
/// </summary>
public class SpecVerdictTests
{
    private static async Task<GuanfuTestHost> SetupAsync(params string[] dod)
    {
        var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");

        await new ProjectSpecService(host.Db, host.Clock).UpdateAsync(
            project.Id, new ProjectSpecInput(DefinitionOfDone: dod.ToList()));

        return host;
    }

    [Fact]
    public async Task 评审包里带上规约条目且先是待判定()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑", "文档里的用法同步更新");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");

        await host.Reviews.StartAsync(task.Id, "评审人");
        var pack = await host.Reviews.GetPackAsync(task.Id);

        Assert.NotNull(pack.SpecItems);
        Assert.Equal(2, pack.SpecItems!.Count);
        Assert.All(pack.SpecItems, i => Assert.Equal("Pending", i.Status));
    }

    [Fact]
    public async Task 判定会回写到评审包()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");

        await host.Reviews.StartAsync(task.Id, "评审人");
        await host.Reviews.SubmitSpecVerdictAsync(task.Id, "有单测覆盖新增逻辑", true, "补了 3 个用例", "评审人");

        var pack = await host.Reviews.GetPackAsync(task.Id);

        Assert.Equal("Passed", pack.SpecItems!.Single().Status);
        Assert.Equal("补了 3 个用例", pack.SpecItems.Single().Note);
    }

    [Fact]
    public async Task 规约没判完不许结项()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑", "文档同步更新");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);

        await host.Reviews.StartAsync(task.Id, "评审人");

        // 七个维度都过，但 DoD 一条没判
        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, true, "无问题", "单测通过", null, "评审人");
        }

        var blocked = await Assert.ThrowsAsync<ReviewGateException>(
            async () => await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done));

        Assert.Contains("规约待判定", blocked.Message);
    }

    [Fact]
    public async Task 规约判成不满足不许结项()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);

        await host.Reviews.StartAsync(task.Id, "评审人");
        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, true, "无问题", "单测通过", null, "评审人");
        }

        await host.Reviews.SubmitSpecVerdictAsync(task.Id, "有单测覆盖新增逻辑", false, "没补测试", "评审人");

        var blocked = await Assert.ThrowsAsync<ReviewGateException>(
            async () => await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done));

        Assert.Contains("规约不满足", blocked.Message);
    }

    [Fact]
    public async Task 规约全判完就能结项()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑", "文档同步更新");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);

        await host.Reviews.StartAsync(task.Id, "评审人");
        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(task.Id, key, true, "无问题", "单测通过", null, "评审人");
        }

        await host.Reviews.SubmitSpecVerdictAsync(task.Id, "有单测覆盖新增逻辑", true, null, "评审人");
        await host.Reviews.SubmitSpecVerdictAsync(task.Id, "文档同步更新", true, null, "评审人");

        var done = await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 不在规约里的条目要拒()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");
        await host.Reviews.StartAsync(task.Id, "评审人");

        await Assert.ThrowsAsync<GuanfuException>(
            async () => await host.Reviews.SubmitSpecVerdictAsync(task.Id, "编出来的条目", true));
    }

    [Fact]
    public async Task 判定为不适用必须写理由()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑");
        var task = await host.Tasks.CreateAsync(host.Db.Projects.First().Id, "一");
        await host.Reviews.StartAsync(task.Id, "评审人");

        await Assert.ThrowsAsync<GuanfuException>(
            async () => await host.Reviews.SubmitSpecVerdictAsync(task.Id, "有单测覆盖新增逻辑", false, null, "评审人", null, default, skipped: true));
    }

    [Fact]
    public async Task 规约改了字旧判定就不算数()
    {
        using var host = await SetupAsync("有单测覆盖新增逻辑");
        var projectId = host.Db.Projects.First().Id;
        var task = await host.Tasks.CreateAsync(projectId, "一");

        await host.Reviews.StartAsync(task.Id, "评审人");
        await host.Reviews.SubmitSpecVerdictAsync(task.Id, "有单测覆盖新增逻辑", true, null, "评审人");

        // 人改了 DoD 的措辞 → 条目键变了 → 旧判定不再算数
        await new ProjectSpecService(host.Db, host.Clock).UpdateAsync(
            projectId, new ProjectSpecInput(DefinitionOfDone: ["有单测覆盖新增逻辑（改动）"]));

        var items = await host.Reviews.GetSpecItemsAsync(task.Id);
        Assert.Equal("Pending", items.Single().Status);
    }
}
