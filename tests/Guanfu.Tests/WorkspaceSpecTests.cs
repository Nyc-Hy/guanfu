using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 规约的继承：工作区标准 → 项目级 → 里程碑级。
///
/// 公司/团队的规矩（「必须有单测」「必须更新文档」）不该每个项目抄一遍 ——
/// 抄一遍就会抄漏、改一处就忘了另一处。
/// </summary>
public class WorkspaceSpecTests
{
    [Fact]
    public async Task 工作区的规矩对所有项目生效()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        var specs = new ProjectSpecService(host.Db, host.Clock);
        var workspaceId = project.WorkspaceId;

        await specs.UpdateForWorkspaceAsync(workspaceId, new ProjectSpecInput(
            DefinitionOfDone: ["有单测覆盖", "文档同步更新"],
            TestCommand: "dotnet test"));

        var effective = await specs.GetEffectiveDefinitionOfDoneAsync(task.Id);

        Assert.Equal(["有单测覆盖", "文档同步更新"], effective);
    }

    [Fact]
    public async Task 三层规约叠加()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "一");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        var specs = new ProjectSpecService(host.Db, host.Clock);

        await specs.UpdateForWorkspaceAsync(project.WorkspaceId, new ProjectSpecInput(
            DefinitionOfDone: ["工作区：必须有单测"]));

        await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["项目：接口错误码与文档一致"]));

        await specs.UpdateForMilestoneAsync(project.Id, milestone.Id, new ProjectSpecInput(
            DefinitionOfDone: ["版本：额外写迁移说明"]));

        var effective = await specs.GetEffectiveDefinitionOfDoneAsync(task.Id);

        Assert.Equal(
            ["工作区：必须有单测", "项目：接口错误码与文档一致", "版本：额外写迁移说明"],
            effective);
    }

    [Fact]
    public async Task 越具体的覆盖通用的()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        var specs = new ProjectSpecService(host.Db, host.Clock);

        await specs.UpdateForWorkspaceAsync(project.WorkspaceId, new ProjectSpecInput(
            Purpose: "工作区目标",
            TestCommand: "dotnet test",
            CommitConvention: "工作区约定"));

        await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            TestCommand: "npm test"));

        var effective = await specs.GetEffectiveAsync(task.Id);

        // 项目覆盖了测试命令
        Assert.Equal("npm test", effective.TestCommand);
        // 没覆盖的用工作区的
        Assert.Equal("工作区约定", effective.CommitConvention);
        Assert.Equal("工作区目标", effective.Purpose);
    }

    [Fact]
    public async Task 别的项目不受这个项目的规约影响()
    {
        using var host = new GuanfuTestHost();
        var first = await host.Projects.CreateAsync("一");
        var second = await host.Projects.CreateAsync("二");
        var task = await host.Tasks.CreateAsync(second.Id, "一");

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateAsync(first.Id, new ProjectSpecInput(
            DefinitionOfDone: ["只有第一个项目要"]));

        var effective = await specs.GetEffectiveDefinitionOfDoneAsync(task.Id);

        Assert.Empty(effective);
    }

    [Fact]
    public async Task 工作区不存在时报错()
    {
        using var host = new GuanfuTestHost();
        var specs = new ProjectSpecService(host.Db, host.Clock);

        await Assert.ThrowsAsync<NotFoundException>(
            async () => await specs.UpdateForWorkspaceAsync(
                Guid.NewGuid(), new ProjectSpecInput(Purpose: "x")));
    }
}
