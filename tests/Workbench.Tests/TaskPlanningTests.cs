using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 任务分派（负责人/优先级/截止日/权重）、任务依赖、里程碑层级、导出。
///
/// 这些是「需求缺口」那一栏里补上的能力 —— 补的时候最怕的就是看着能用、
/// 实际不拦（比如依赖建成摆设），所以每条都写了反例。
/// </summary>
public class TaskPlanningTests
{
    // ---- 分派 ----

    [Fact]
    public async Task 能给任务分派负责人优先级截止日与权重()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var due = host.Clock.UtcNow.AddDays(3);
        var updated = await host.Tasks.SetAssignmentAsync(
            task.Id,
            assignee: "张三",
            priority: TaskPriority.High,
            dueAt: due,
            weight: 2.5);

        Assert.Equal("张三", updated.Assignee);
        Assert.Equal(TaskPriority.High, updated.Priority);
        Assert.Equal(due, updated.DueAt);
        Assert.Equal(2.5, updated.Weight);
    }

    [Fact]
    public async Task 不传的字段保持原值()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.SetAssignmentAsync(task.Id, assignee: "李四");
        var again = await host.Tasks.SetAssignmentAsync(task.Id, priority: TaskPriority.Critical);

        Assert.Equal("李四", again.Assignee);
        Assert.Equal(TaskPriority.Critical, again.Priority);
    }

    [Fact]
    public async Task 权重不能是零或负数()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var updated = await host.Tasks.SetAssignmentAsync(task.Id, weight: 0);

        // 权重进项目进度的分母，0 或负数会让整个进度算不出来
        Assert.True(updated.Weight > 0);
    }

    [Fact]
    public async Task 分派会进事件账本()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.SetAssignmentAsync(task.Id, assignee: "王五");

        var events = await host.Tasks.ListEventsAsync(task.Id);
        Assert.Contains(events, e => e.EventType == TaskEventTypes.Assigned);
    }

    // ---- 依赖 ----

    [Fact]
    public async Task 能加任务依赖()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);

        var list = await host.Dependencies.ListAsync(second.Id);
        Assert.Single(list);
        Assert.Equal(first.Id, list[0].DependsOnTaskId);
        Assert.False(list[0].DependsOnDone);
    }

    [Fact]
    public async Task 依赖成环会被拒()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);

        // first 依赖 second → 形成 A→B→A
        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Dependencies.AddAsync(first.Id, second.Id));
    }

    [Fact]
    public async Task 任务不能依赖自己()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写接口");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Dependencies.AddAsync(task.Id, task.Id));
    }

    [Fact]
    public async Task 前置任务没做完就不许领取()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);

        var error = await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Tasks.ClaimAsync(second.Id, "agent-1"));

        Assert.Contains("前置", error.Message);
    }

    [Fact]
    public async Task 前置任务做完了就能领取()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);

        await host.Tasks.ChangeStatusAsync(first.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(first.Id, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(first.Id, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(first.Id, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(first.Id, TaskStatus.Review);
        await host.Tasks.ChangeStatusAsync(first.Id, TaskStatus.Done);

        var claimed = await host.Tasks.ClaimAsync(second.Id, "agent-1");
        Assert.Equal("agent-1", claimed.ClaimedBy);
    }

    [Fact]
    public async Task 前置任务没做完就不许推进到开发中()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);
        await host.Tasks.ChangeStatusAsync(second.Id, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(second.Id, TaskStatus.Planned);

        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Tasks.ChangeStatusAsync(second.Id, TaskStatus.InDev));
    }

    [Fact]
    public async Task 移除依赖之后就不再挡着了()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);
        await host.Dependencies.RemoveAsync(second.Id, first.Id);

        var claimed = await host.Tasks.ClaimAsync(second.Id, "agent-1");
        Assert.Equal("agent-1", claimed.ClaimedBy);
    }

    [Fact]
    public async Task 任务列表里能看出还有几个前置没做完()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var first = await host.Tasks.CreateAsync(project.Id, "建表");
        var second = await host.Tasks.CreateAsync(project.Id, "写接口");

        await host.Dependencies.AddAsync(second.Id, first.Id);

        var list = await host.Tasks.ListAsync(project.Id);
        var blocked = list.Single(t => t.Id == second.Id);
        var free = list.Single(t => t.Id == first.Id);

        Assert.Equal(1, blocked.PendingDependencies);
        Assert.Equal(0, free.PendingDependencies);
    }

    // ---- 里程碑层级 ----

    [Fact]
    public async Task 里程碑能建子层级()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var release = await host.Milestones.CreateAsync(
            project.Id, "12月版本", kind: MilestoneKind.Release);

        var group = await host.Milestones.CreateAsync(
            project.Id, "登录模块", parentId: release.Id, kind: MilestoneKind.Group);

        Assert.Equal(release.Id, group.ParentId);
        Assert.Equal(MilestoneKind.Group, group.Kind);
    }

    [Fact]
    public async Task 父里程碑不属于同一个项目会被拒()
    {
        using var host = new WorkbenchTestHost();
        var projectA = await host.Projects.CreateAsync("项目 A");
        var projectB = await host.Projects.CreateAsync("项目 B");

        var release = await host.Milestones.CreateAsync(projectA.Id, "12月版本");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Milestones.CreateAsync(projectB.Id, "串项目", parentId: release.Id));
    }

    [Fact]
    public async Task 里程碑不能挂到自己的后代下面()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var release = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var group = await host.Milestones.CreateAsync(project.Id, "登录模块", parentId: release.Id);

        // 把版本挂到它自己的子节点下面 → 成环
        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Milestones.UpdateAsync(release.Id, parentId: group.Id));
    }

    [Fact]
    public async Task 删掉父里程碑时子节点不跟着没()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        var release = await host.Milestones.CreateAsync(project.Id, "12月版本");
        var group = await host.Milestones.CreateAsync(project.Id, "登录模块", parentId: release.Id);

        await host.Milestones.DeleteAsync(release.Id);

        var list = await host.Milestones.ListAsync(project.Id);
        var orphan = list.Single(m => m.Id == group.Id);
        Assert.Null(orphan.ParentId);
    }

    // ---- 导出 ----

    [Fact]
    public async Task 事件流水能导出成Markdown()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady);

        var markdown = await host.Exports.ExportTaskEventsAsync(task.Id, "markdown");

        Assert.Contains("写登录接口", markdown);
        Assert.Contains("DocsReady", markdown);
    }

    [Fact]
    public async Task 事件流水能导出成CSV()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var csv = await host.Exports.ExportTaskEventsAsync(task.Id, "csv");

        Assert.StartsWith("id,createdAt,source", csv);
        Assert.Contains("status.changed", csv);
    }

    [Fact]
    public async Task 评审报告能导出()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Reviews.StartAsync(task.Id, "reviewer-1");

        var markdown = await host.Exports.ExportReviewReportAsync(task.Id);

        Assert.Contains("评审报告", markdown);
    }

    [Fact]
    public async Task 没有评审时导出评审报告会明确报错()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => host.Exports.ExportReviewReportAsync(task.Id));
    }

    // ---- SQL 目标表识别 ----

    [Theory]
    [InlineData("UPDATE Orders SET x = 1", "Orders")]
    [InlineData("UPDATE dbo.Orders SET x = 1", "dbo.Orders")]
    [InlineData("UPDATE [dbo].[Orders] SET x = 1", "dbo.Orders")]
    [InlineData("INSERT INTO Orders (a) VALUES (1)", "Orders")]
    [InlineData("INSERT INTO dbo.Orders (a) VALUES (1)", "dbo.Orders")]
    [InlineData("DELETE FROM Orders WHERE id = 1", "Orders")]
    [InlineData("TRUNCATE TABLE dbo.Orders", "dbo.Orders")]
    [InlineData("MERGE INTO dbo.Orders AS t USING src ON t.id = src.id", "dbo.Orders")]
    public void 能认出写操作的目标表(string sql, string expected)
    {
        Assert.Equal(expected, SqlTargetTable.TryExtract(sql));
    }

    [Fact]
    public void 认不出目标表时返回空而不是乱猜()
    {
        // 认错了比多问一次审批糟糕得多 —— 宁可返回 null
        Assert.Null(SqlTargetTable.TryExtract("SELECT * FROM Orders"));
        Assert.Null(SqlTargetTable.TryExtract("EXEC dbo.SomeProc"));
        Assert.Null(SqlTargetTable.TryExtract(""));
    }

    [Fact]
    public void 字符串里的表名不会被当成目标表()
    {
        // "DELETE FROM 'x'" 里的引号内容不是表名
        Assert.Null(SqlTargetTable.TryExtract("SELECT 'DELETE FROM Orders'"));
    }

    // ---- 上传类型白名单 ----

    [Theory]
    [InlineData("需求.md", true)]
    [InlineData("接口文档.docx", true)]
    [InlineData("截图.png", true)]
    [InlineData("数据.csv", true)]
    [InlineData("evil.exe", false)]
    [InlineData("脚本.sh", false)]
    [InlineData("没有扩展名", false)]
    public void 上传类型白名单按扩展名判定(string fileName, bool allowed)
    {
        Assert.Equal(allowed, DocumentService.IsExtensionAllowed(fileName));
    }
}
