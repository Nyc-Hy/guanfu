using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 完成定义的来源层级。
///
/// 完成定义分三层（工作区 → 项目 → 里程碑），叠加生效。
/// 判的时候该知道**这条是谁的要求** —— 里程碑特有的只对这个版本里的活成立，
/// 项目级的那条对这个项目里所有活都成立。
///
/// 重复的按**最外层**算：项目里已经要求的，里程碑再写一遍也是项目级的要求
/// （所以里程碑那层不用把项目级重抄一遍）。
/// </summary>
public sealed class SpecSourceTests
{
    [Fact]
    public async Task 每条完成定义都带来源()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "带来源的活");

        await host.Specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["项目级的一条"]));

        var items = await host.Specs.GetEffectiveDefinitionOfDoneWithSourceAsync(task.Id);

        Assert.Single(items);
        Assert.Equal("项目级的一条", items[0].Text);
        Assert.Equal(SpecSource.Project, items[0].Source);
    }

    [Fact]
    public async Task 里程碑那条标成版本特有()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "版本里的活", milestoneId: milestone.Id);

        await host.Specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["项目级的一条"]));
        await host.Specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["这个版本特有的一条"]));

        var items = await host.Specs.GetEffectiveDefinitionOfDoneWithSourceAsync(task.Id);

        Assert.Equal(2, items.Count);
        Assert.Equal(SpecSource.Project, items.Single(i => i.Text == "项目级的一条").Source);
        Assert.Equal(SpecSource.Milestone, items.Single(i => i.Text == "这个版本特有的一条").Source);
    }

    [Fact]
    public async Task 重复的按最外层算()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "版本里的活", milestoneId: milestone.Id);

        await host.Specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["两条都要满足"]));
        await host.Specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["两条都要满足"]));

        var items = await host.Specs.GetEffectiveDefinitionOfDoneWithSourceAsync(task.Id);

        // 只出现一次，来源是最外层（项目级）—— 里程碑重抄一遍不改变它是谁的要求
        Assert.Single(items);
        Assert.Equal(SpecSource.Project, items[0].Source);
    }

    [Fact]
    public async Task 评审包里看得到每条的来源()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "版本里的活", milestoneId: milestone.Id);

        await host.Specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["项目级的一条"]));
        await host.Specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["版本特有的一条"]));

        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.Review);
        await host.Reviews.StartAsync(task.Id, "e2e");

        var items = await host.Reviews.GetSpecItemsAsync(task.Id);

        Assert.Equal(2, items.Count);
        Assert.Equal(SpecSource.Project, items.Single(i => i.Text == "项目级的一条").Source);
        Assert.Equal(SpecSource.Milestone, items.Single(i => i.Text == "版本特有的一条").Source);
    }
}
