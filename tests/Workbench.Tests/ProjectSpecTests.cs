using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 项目级规约。
///
/// 这一层存在的全部理由：**人定的规矩，agent 要读得到**。
/// 以前这些规矩只活在人的 AGENTS.md 里，agent 读不到 ——
/// 「什么算做完」各说各话，评审也只能拿通用维度判。
/// </summary>
public class ProjectSpecTests
{
    [Fact]
    public async Task 没填过规约时返回空规约()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var specs = new ProjectSpecService(host.Db, host.Clock);

        var spec = await specs.GetAsync(project.Id);

        Assert.True(spec.IsEmpty);
        Assert.Empty(spec.DefinitionOfDone);
        Assert.Null(spec.Purpose);
    }

    [Fact]
    public async Task 写进去读得回来()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var specs = new ProjectSpecService(host.Db, host.Clock);

        var saved = await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            Purpose: "让 agent 能自己判断活干完没有",
            DefinitionOfDone: ["有单测覆盖新增逻辑", "文档里的用法同步更新"],
            CommitConvention: "feat(登录): 补上会话续期；带 REQ-001 编号",
            TestCommand: "dotnet test Workbench.sln",
            BranchConvention: "feature/REQ-001-登录续期",
            Guardrails: "不许改 migrations 目录"), "验收人");

        Assert.Equal("让 agent 能自己判断活干完没有", saved.Purpose);
        Assert.Equal(["有单测覆盖新增逻辑", "文档里的用法同步更新"], saved.DefinitionOfDone);
        Assert.Equal("dotnet test Workbench.sln", saved.TestCommand);
        Assert.Equal("验收人", saved.UpdatedBy);
        Assert.NotNull(saved.UpdatedAt);

        var reread = await specs.GetAsync(project.Id);
        Assert.Equal(saved.DefinitionOfDone, reread.DefinitionOfDone);
        Assert.Equal(saved.Guardrails, reread.Guardrails);
    }

    [Fact]
    public async Task 空行与空白项不算一条()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var specs = new ProjectSpecService(host.Db, host.Clock);

        var saved = await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["  有单测  ", "", "   ", "文档更新"]));

        Assert.Equal(["有单测", "文档更新"], saved.DefinitionOfDone);
    }

    [Fact]
    public async Task 整个覆盖而不是打补丁()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var specs = new ProjectSpecService(host.Db, host.Clock);

        await specs.UpdateAsync(project.Id, new ProjectSpecInput(Purpose: "旧目标"));
        var updated = await specs.UpdateAsync(project.Id, new ProjectSpecInput(Purpose: "新目标"));

        Assert.Equal("新目标", updated.Purpose);
    }

    [Fact]
    public async Task 上下文包里带着规约()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            Purpose: "目标", DefinitionOfDone: ["有单测"]));

        var context = await host.Tasks.GetContextAsync(task.Id);

        Assert.NotNull(context.Spec);
        Assert.Equal("目标", context.Spec!.Purpose);
        Assert.Equal(["有单测"], context.Spec.DefinitionOfDone);
    }

    [Fact]
    public async Task 评审包里带着规约()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("P");
        var task = await host.Tasks.CreateAsync(project.Id, "一");

        var specs = new ProjectSpecService(host.Db, host.Clock);
        await specs.UpdateAsync(project.Id, new ProjectSpecInput(
            DefinitionOfDone: ["接口错误码与文档一致"]));

        await host.Reviews.StartAsync(task.Id, "评审人");
        var pack = await host.Reviews.GetPackAsync(task.Id);

        Assert.NotNull(pack.Spec);
        Assert.Equal(["接口错误码与文档一致"], pack.Spec!.DefinitionOfDone);
    }

    [Fact]
    public async Task 项目不存在时报错()
    {
        using var host = new WorkbenchTestHost();
        var specs = new ProjectSpecService(host.Db, host.Clock);

        await Assert.ThrowsAsync<NotFoundException>(
            async () => await specs.UpdateAsync(Guid.NewGuid(), new ProjectSpecInput(Purpose: "x")));
    }
}
