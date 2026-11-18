using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 评审门禁的三处收紧：评审包字段要全、Skipped 要有理由且有上限、轮次要绑 commit。
///
/// 门禁是「不评审完不许结项」的最后一道，任何一处松了都等于没有：
/// 七个维度全标不适用就能结项、评完之后代码改了还能结项，都是真会发生的绕过。
/// </summary>
public class ReviewGateTests
{
    private static async Task<(GuanfuTestHost Host, Guid ProjectId, Guid TaskId)> SetupAsync(string? repoPath = null)
    {
        var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        if (repoPath is not null)
        {
            await host.Projects.UpdateAsync(project.Id, repoPath: repoPath);
        }

        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        return (host, project.Id, task.Id);
    }

    private static async Task MoveToReviewAsync(GuanfuTestHost host, Guid taskId)
    {
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.DocsReady);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Planned);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.InDev);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Testing);
        await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Review);
    }

    private static async Task PassAllAsync(GuanfuTestHost host, Guid taskId)
    {
        foreach (var key in ReviewDimensions.Keys)
        {
            await host.Reviews.SubmitAsync(taskId, key, true, "检查过了");
        }
    }

    // ---- 17. 评审包里的任务摘要字段要全 ----

    [Fact]
    public async Task 评审包带全任务的分派与依赖字段()
    {
        var (host, projectId, taskId) = await SetupAsync();
        using var scope = host;

        var dueAt = host.Clock.UtcNow.AddDays(3);
        await host.Tasks.SetAssignmentAsync(taskId, "张三", TaskPriority.High, dueAt, 2.5);

        var prerequisite = await host.Tasks.CreateAsync(projectId, "前置任务");
        await host.Dependencies.AddAsync(taskId, prerequisite.Id);

        await host.Tasks.SetPlanAsync(taskId, "背景", "理解", "拆解", "验收", "风险", "agent-1");
        await host.Tasks.AddArtifactAsync(taskId, ArtifactType.Commit, "提交", commitSha: "abc123");

        await host.Reviews.StartAsync(taskId, "reviewer-1");
        var pack = await host.Reviews.GetPackAsync(taskId);
        var summary = (await host.Tasks.GetAsync(taskId))!;

        // 老实现的评审包只传 14 个参数，这些字段全是默认值 ——
        // 评审的 agent 看不到负责人、优先级、截止日、权重，也看不到「这任务还动不了」。
        Assert.Equal("张三", pack.Task.Assignee);
        Assert.Equal(TaskPriority.High, pack.Task.Priority);
        Assert.Equal(dueAt, pack.Task.DueAt);
        Assert.Equal(2.5, pack.Task.Weight);
        Assert.Equal(1, pack.Task.PendingDependencies);
        Assert.False(pack.Task.IsArchived);
        Assert.Equal(summary.Stale, pack.Task.Stale);
        Assert.Equal(summary.Key, pack.Task.Key);

        // 映射只有一份：评审包与任务详情必须逐字段一致
        Assert.Equal(summary, pack.Task);

        Assert.NotNull(pack.Plan);
        Assert.Equal(1, pack.Plan!.Version);
        Assert.Single(pack.Artifacts);
        Assert.Equal(7, pack.Dimensions.Count);
    }

    [Fact]
    public async Task 评审包如实反映归档与陈旧标记()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;

        await host.Reviews.StartAsync(taskId);
        await host.Tasks.SetArchivedAsync(taskId, true);

        host.Clock.Advance(TimeSpan.FromHours(TaskService.StaleAfterHours + 1));

        var pack = await host.Reviews.GetPackAsync(taskId);
        Assert.True(pack.Task.IsArchived);
        Assert.True(pack.Task.Stale);
    }

    // ---- 18. Skipped 要有理由、每轮最多两个 ----

    [Fact]
    public async Task 跳过维度必须写明理由()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;
        await host.Reviews.StartAsync(taskId);

        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => host.Reviews.SubmitAsync(taskId, "security", true, skipped: true));
        Assert.Contains("理由", ex.Message);

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Reviews.SubmitAsync(taskId, "security", true, findings: "   ", skipped: true));

        var item = await host.Reviews.SubmitAsync(
            taskId, "security", true, findings: "本次改动不涉及鉴权", skipped: true);
        Assert.Equal(nameof(ReviewStatus.Skipped), item.Status);
    }

    [Fact]
    public async Task 每轮最多跳过两个维度()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;
        await host.Reviews.StartAsync(taskId);

        var keys = ReviewDimensions.Keys.ToList();
        await host.Reviews.SubmitAsync(taskId, keys[0], true, "不适用：没有对外接口", skipped: true);
        await host.Reviews.SubmitAsync(taskId, keys[1], true, "不适用：没有数据库改动", skipped: true);

        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => host.Reviews.SubmitAsync(taskId, keys[2], true, "不适用：懒得看", skipped: true));

        Assert.Contains($"最多跳过 {ReviewService.MaxSkippedPerRound}", ex.Message);

        // 第三个维度必须给真结论
        var item = await host.Reviews.SubmitAsync(taskId, keys[2], true, "检查过了");
        Assert.Equal(nameof(ReviewStatus.Passed), item.Status);
    }

    [Fact]
    public async Task 重复回写同一个跳过维度不占额外名额()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;
        await host.Reviews.StartAsync(taskId);

        var keys = ReviewDimensions.Keys.ToList();
        await host.Reviews.SubmitAsync(taskId, keys[0], true, "不适用", skipped: true);
        await host.Reviews.SubmitAsync(taskId, keys[1], true, "不适用", skipped: true);

        // 改的是已经跳过的那一条，不该被自己占掉的名额挡住
        var again = await host.Reviews.SubmitAsync(taskId, keys[0], true, "补充理由：确认无外部入口", skipped: true);
        Assert.Equal(nameof(ReviewStatus.Skipped), again.Status);
    }

    [Fact]
    public async Task 两个跳过加五个通过仍然能结项()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;
        await MoveToReviewAsync(host, taskId);
        await host.Reviews.StartAsync(taskId);

        var keys = ReviewDimensions.Keys.ToList();
        await host.Reviews.SubmitAsync(taskId, keys[0], true, "不适用：无外部接口", skipped: true);
        await host.Reviews.SubmitAsync(taskId, keys[1], true, "不适用：无库改动", skipped: true);
        foreach (var key in keys.Skip(2))
        {
            await host.Reviews.SubmitAsync(taskId, key, true, "检查过了");
        }

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.Empty(gate.PendingDimensions);
        Assert.Empty(gate.FailedDimensions);
        Assert.Null(gate.BlockReason);

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 老数据里超限或无理由的跳过不被新规则卡死()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;
        await MoveToReviewAsync(host, taskId);
        var round = await host.Reviews.StartAsync(taskId);

        var keys = ReviewDimensions.Keys.ToList();

        // 直接改库造出「升级前写进来的」三条无理由 Skipped
        await host.Db.ReviewItems
            .Where(i => i.ReviewRoundId == round.Id && keys.Take(3).Contains(i.Dimension))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, ReviewStatus.Skipped)
                .SetProperty(i => i.Findings, (string?)null));

        // 门禁只约束新写入：历史轮次照旧按维度状态判，不拿新规则去卡老数据
        foreach (var key in keys.Skip(3))
        {
            await host.Reviews.SubmitAsync(taskId, key, true, "检查过了");
        }

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.Empty(gate.PendingDimensions);
        Assert.Empty(gate.FailedDimensions);

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);

        // 但**新写入**仍然受上限约束：新轮次是干净的，跳满两个之后第三个必须给真结论
        await host.Reviews.StartAsync(taskId);
        await host.Reviews.SubmitAsync(taskId, keys[0], true, "不适用：无外部接口", skipped: true);
        await host.Reviews.SubmitAsync(taskId, keys[1], true, "不适用：无库改动", skipped: true);
        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Reviews.SubmitAsync(taskId, keys[2], true, "不适用", skipped: true));
    }

    // ---- 19. 轮次绑 commit ----

    [Fact]
    public async Task 开轮时记下仓库HEAD()
    {
        var (host, _, taskId) = await SetupAsync("/tmp/repo");
        using var scope = host;
        host.Git.HeadSha = "aaa111222333444555666777888999000aaabbb";

        var round = await host.Reviews.StartAsync(taskId);

        Assert.Equal(host.Git.HeadSha, round.CommitHash);
    }

    [Fact]
    public async Task 项目没配仓库时轮次不绑commit()
    {
        var (host, _, taskId) = await SetupAsync();
        using var scope = host;

        var round = await host.Reviews.StartAsync(taskId);

        Assert.Null(round.CommitHash);
    }

    [Fact]
    public async Task 评审后代码变了就不许结项()
    {
        var (host, _, taskId) = await SetupAsync("/tmp/repo");
        using var scope = host;
        host.Git.HeadSha = "aaa111";

        await MoveToReviewAsync(host, taskId);
        await host.Reviews.StartAsync(taskId);
        await PassAllAsync(host, taskId);

        // 评完之后又提交了新代码
        host.Git.HeadSha = "bbb222";

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.NotNull(gate.BlockReason);
        Assert.Contains("评审后代码已变更", gate.BlockReason);

        var ex = await Assert.ThrowsAsync<ReviewGateException>(
            () => host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done));
        Assert.Contains("评审后代码已变更", ex.Message);

        // 开新轮评审（绑到新 HEAD）后就能结项
        await host.Reviews.StartAsync(taskId);
        await PassAllAsync(host, taskId);

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 读不到HEAD时绑了commit的轮次也不许结项()
    {
        var (host, _, taskId) = await SetupAsync("/tmp/repo");
        using var scope = host;
        host.Git.HeadSha = "aaa111";

        await MoveToReviewAsync(host, taskId);
        await host.Reviews.StartAsync(taskId);
        await PassAllAsync(host, taskId);

        // 仓库读不到了（被移走 / git 不可用）：「不知道现在是什么代码」不等于「代码没变」
        host.Git.HeadSha = null;

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.NotNull(gate.BlockReason);
        Assert.Contains("读不到仓库 HEAD", gate.BlockReason);

        await Assert.ThrowsAsync<ReviewGateException>(
            () => host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done));
    }

    [Fact]
    public async Task HEAD没变时门禁不拦()
    {
        var (host, _, taskId) = await SetupAsync("/tmp/repo");
        using var scope = host;
        host.Git.HeadSha = "aaa111";

        await MoveToReviewAsync(host, taskId);
        await host.Reviews.StartAsync(taskId);
        await PassAllAsync(host, taskId);

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.Null(gate.BlockReason);

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 老轮次没有commit时跳过这项校验()
    {
        var (host, projectId, taskId) = await SetupAsync();
        using var scope = host;

        await MoveToReviewAsync(host, taskId);
        // 开轮时项目还没配仓库 → CommitHash 为空（老轮次）
        await host.Reviews.StartAsync(taskId);
        await PassAllAsync(host, taskId);

        // 之后才配上仓库，HEAD 也读得到 —— 但不能拿新规则去卡老轮次
        await host.Projects.UpdateAsync(projectId, repoPath: "/tmp/repo");
        host.Git.HeadSha = "ccc333";

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.Null(gate.BlockReason);

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 没开评审的老流程照旧能结项()
    {
        var (host, _, taskId) = await SetupAsync("/tmp/repo");
        using var scope = host;
        host.Git.HeadSha = "aaa111";

        await MoveToReviewAsync(host, taskId);

        var gate = await host.Reviews.GetGateAsync(taskId);
        Assert.False(gate.HasReview);
        Assert.Null(gate.BlockReason);

        var done = await host.Tasks.ChangeStatusAsync(taskId, TaskStatus.Done);
        Assert.Equal(TaskStatus.Done, done.Status);
    }

    [Fact]
    public async Task 评审事件带上类型与结构化载荷()
    {
        var (host, _, taskId) = await SetupAsync("/tmp/repo");
        using var scope = host;
        host.Git.HeadSha = "aaa111";

        var round = await host.Reviews.StartAsync(taskId, "reviewer-1");
        await PassAllAsync(host, taskId);

        var events = await host.Tasks.ListEventsAsync(taskId, 100);

        var opened = events.First(e => e.EventType == TaskEventTypes.ReviewRoundOpened);
        Assert.Contains("\"round\":1", opened.Payload);
        Assert.Contains("aaa111", opened.Payload);

        var results = events.Where(e => e.EventType == TaskEventTypes.ReviewDimensionResult).ToList();
        Assert.Equal(7, results.Count);
        Assert.All(results, e => Assert.Contains("\"result\":\"Passed\"", e.Payload));

        var closed = events.First(e => e.EventType == TaskEventTypes.ReviewRoundClosed);
        Assert.Contains("\"verdict\":\"Passed\"", closed.Payload);
        Assert.Contains("\"round\":1", closed.Payload);
        Assert.Equal(1, round.Round);
    }
}