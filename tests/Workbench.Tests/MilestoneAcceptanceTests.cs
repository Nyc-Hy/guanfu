using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 里程碑验收门禁。
///
/// 评审有结项门禁，里程碑以前**没有**对应的判定 —— 「这个版本真做完了吗」
/// 没人判、也没人能判。现在验收是人的签字，条件是下面的活都做完了；
/// 签过字之后，里面的活不许悄悄回退（要改先撤销验收）。
/// </summary>
public class MilestoneAcceptanceTests
{
    [Fact]
    public async Task 活没做完不许验收()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        await host.Tasks.CreateAsync(project.Id, "没做完的活");
        await host.Milestones.AssignTaskAsync(
            (await host.Tasks.ListAsync(project.Id)).Single().Id, milestone.Id);

        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            async () => await host.Milestones.AcceptAsync(milestone.Id, "验收人"));

        Assert.Contains("还有", ex.Message);
    }

    [Fact]
    public async Task 活做完了才能验收()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "做完了的活");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        await FinishAsync(host, task.Id);

        var accepted = await host.Milestones.AcceptAsync(milestone.Id, "验收人");

        Assert.NotNull(accepted.AcceptedAt);
        Assert.Equal("验收人", accepted.AcceptedBy);
    }

    [Fact]
    public async Task 验收后里面的活不能悄悄回退()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "做完了的活");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        await FinishAsync(host, task.Id);
        await host.Milestones.AcceptAsync(milestone.Id, "验收人");

        // 验收之后这个任务的状态就冻住了 —— 连「转受阻」（言下之意是「其实没做完」）
        // 都不行：那等于悄悄推翻签字。
        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            async () => await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Blocked));

        Assert.Contains("已验收", ex.Message);
    }

    [Fact]
    public async Task 撤销验收之后可以再改()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "做完了的活");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        await FinishAsync(host, task.Id);
        await host.Milestones.AcceptAsync(milestone.Id, "验收人");
        await host.Milestones.UnacceptAsync(milestone.Id);

        // 撤销后就能回退了（Done → 受阻 → 开发中）
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Blocked);
        var reopened = await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        Assert.Equal(TaskStatus.InDev, reopened.Status);
    }

    [Fact]
    public async Task 验收后不许再往里加活()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "做完了的活");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        await FinishAsync(host, task.Id);
        await host.Milestones.AcceptAsync(milestone.Id, "验收人");

        var another = await host.Tasks.CreateAsync(project.Id, "后来加的活");

        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            async () => await host.Milestones.AssignTaskAsync(another.Id, milestone.Id));

        Assert.Contains("已经验收", ex.Message);
    }

    [Fact]
    public async Task 验收要看整棵子树()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var version = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var group = await host.Milestones.CreateAsync(
            project.Id, "需求组", parentId: version.Id, kind: MilestoneKind.Group);

        var task = await host.Tasks.CreateAsync(project.Id, "组里的活");
        await host.Milestones.AssignTaskAsync(task.Id, group.Id);

        // 组里的活没做完 → 版本不许验收
        var ex = await Assert.ThrowsAsync<WorkbenchException>(
            async () => await host.Milestones.AcceptAsync(version.Id, "验收人"));
        Assert.Contains("还有", ex.Message);

        await FinishAsync(host, task.Id);

        var accepted = await host.Milestones.AcceptAsync(version.Id, "验收人");
        Assert.NotNull(accepted.AcceptedAt);
    }

    /// <summary>把一个任务推到「已完成」。</summary>
    private static async Task FinishAsync(WorkbenchTestHost host, Guid taskId)
    {
        foreach (var status in new[]
                 {
                     TaskStatus.DocsReady, TaskStatus.Planned, TaskStatus.InDev,
                     TaskStatus.Testing, TaskStatus.Review, TaskStatus.Done
                 })
        {
            await host.Tasks.ChangeStatusAsync(taskId, status);
        }
    }
}
