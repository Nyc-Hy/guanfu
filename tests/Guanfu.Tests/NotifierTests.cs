using System.Text.Json;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

public class NotifierTests
{
    [Fact]
    public void 钉钉载荷是markdown格式()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            WebhookNotifier.BuildPayload("dingtalk", "观复待审批", "内容"));

        Assert.Equal("markdown", payload.GetProperty("msgtype").GetString());
        Assert.Contains("观复待审批", payload.GetProperty("markdown").GetProperty("title").GetString()!);
    }

    [Fact]
    public void 飞书载荷是text格式()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            WebhookNotifier.BuildPayload("feishu", "观复待审批", "内容"));

        Assert.Equal("text", payload.GetProperty("msg_type").GetString());
        Assert.Contains("观复待审批", payload.GetProperty("content").GetProperty("text").GetString()!);
    }

    [Fact]
    public void 通用载荷带标题与正文()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            WebhookNotifier.BuildPayload(null, "标题", "正文"));

        Assert.Equal("标题", payload.GetProperty("title").GetString());
        Assert.Equal("正文", payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 审批产生时会推通知()
    {
        using var host = new GuanfuTestHost();
        var notifier = new RecordingNotifier();
        var approvals = new ApprovalService(host.Db, host.Clock, notifier);

        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateConfigAsync(project.Id, notificationWebhookUrl: "https://example.com/hook");
        await approvals.RequestAsync("db_execute", "db.write", "{}", "要写库", "agent-1", null, project.Id);

        Assert.Single(notifier.Messages);
        Assert.Contains("要写库", notifier.Messages[0]);
    }

    [Fact]
    public async Task 项目没配通知地址就不推()
    {
        using var host = new GuanfuTestHost();
        var notifier = new RecordingNotifier();
        var approvals = new ApprovalService(host.Db, host.Clock, notifier);

        var project = await host.Projects.CreateAsync("测试项目");
        await approvals.RequestAsync("db_execute", "db.write", "{}", "要写库", "agent-1", null, project.Id);

        Assert.Empty(notifier.Messages);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<string> Messages { get; } = [];

        public Task<bool> NotifyAsync(
            string webhookUrl,
            string? format,
            string title,
            string message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add($"{title}|{message}");
            return Task.FromResult(true);
        }
    }
}
