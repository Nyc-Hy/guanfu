using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 事件账本的契约测试（规矩 20）。
///
/// <c>StatusEvent.EventType</c> 是固定标识，<c>Payload</c> 是结构化 JSON ——
/// 消费方按 EventType 判定，不去猜 Note 的自由文本。
///
/// 以前写事件只写 Note，EventType 留 null，机器没法消费。
/// 这里把所有会写事件的服务都跑一遍，逐条断言 EventType 非空、Payload 是合法 JSON。
/// </summary>
public sealed class EventLedgerContractTests
{
    [Fact]
    public async Task 所有事件都有事件类型与结构化载荷()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        // 领任务要走分支守卫：项目得配了仓库路径，假 git 还得报同一个分支。
        await host.Projects.UpdateConfigAsync(project.Id, repoPath: "/tmp/wb-test-repo");
        host.Git.Branch = "feature/login";

        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        await host.Tasks.BindBranchAsync(task.Id, "feature/login");
        await host.Tasks.SetTypeAsync(task.Id, TaskType.Bug);
        await host.Tasks.ClaimAsync(task.Id, "agent-1");
        await host.Tasks.SetPlanAsync(task.Id, "背景", "理解", "拆解", "验收", "风险", "agent-1");
        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.CodeChange, "补丁", "diff --git");
        await host.Tasks.ReportTestResultAsync(task.Id, 10, 9, "用例A失败", 0.9, "agent-1");
        await host.Tasks.PostEventAsync(task.Id, "补充说明", TaskEventSource.Agent, "agent-1");
        await host.Tasks.ChangeStatusAsync(task.Id, TaskStatus.DocsReady, "PRD 已上传", TaskEventSource.Agent, "agent-1");
        await host.Tasks.SetAssignmentAsync(task.Id, "someone", TaskPriority.High);
        await host.Tasks.SetArchivedAsync(task.Id, true);
        await host.Tasks.ReleaseAsync(task.Id, "agent-1");

        var round = await host.Reviews.StartAsync(task.Id, "reviewer-1");
        await host.Reviews.SubmitAsync(task.Id, ReviewDimensions.All[0].Key, true, reviewer: "reviewer-1");

        var events = await host.Db.StatusEvents
            .Where(e => e.TaskId == task.Id)
            .ToListAsync();

        Assert.True(events.Count >= 10, $"事件条数偏少：{events.Count}");

        foreach (var e in events)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.EventType),
                $"事件 {e.Id}（{e.Note}）没有 EventType —— 消费方只能去猜 Note。");

            Assert.False(string.IsNullOrWhiteSpace(e.Payload),
                $"事件 {e.Id}（{e.EventType}）没有 Payload —— 关键字段没法按字段取。");

            using var doc = System.Text.Json.JsonDocument.Parse(e.Payload!);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        // 具体类型也要对得上，不能只是「有个字符串」
        var types = events.Select(e => e.EventType!).ToHashSet();
        Assert.Contains(TaskEventTypes.StatusChanged, types);
        Assert.Contains(TaskEventTypes.PlanSaved, types);
        Assert.Contains(TaskEventTypes.ArtifactAdded, types);
        Assert.Contains(TaskEventTypes.TestReported, types);
        Assert.Contains(TaskEventTypes.Claimed, types);
        Assert.Contains(TaskEventTypes.Released, types);
        Assert.Contains(TaskEventTypes.ReviewRoundOpened, types);
        Assert.Contains(TaskEventTypes.ReviewDimensionResult, types);
    }

    [Fact]
    public async Task 任务创建的事件就带类型()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var events = await host.Db.StatusEvents
            .Where(e => e.TaskId == task.Id)
            .ToListAsync();

        Assert.Single(events);
        Assert.Equal(TaskEventTypes.StatusChanged, events[0].EventType);
        Assert.False(string.IsNullOrWhiteSpace(events[0].Payload));
    }
}
