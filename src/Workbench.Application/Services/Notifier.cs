using System.Text;
using System.Text.Json;

namespace Workbench.Application.Services;

/// <summary>通知推送。审批产生时推给「人」，避免人不在电脑前时审批堆积。</summary>
public interface INotifier
{
    Task<bool> NotifyAsync(
        string webhookUrl,
        string? format,
        string title,
        string message,
        CancellationToken cancellationToken = default);
}

public sealed class WebhookNotifier(HttpClient http) : INotifier
{
    public async Task<bool> NotifyAsync(
        string webhookUrl,
        string? format,
        string title,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        var payload = BuildPayload(format, title, message);

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(webhookUrl, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 通知失败不能影响主流程
            return false;
        }
    }

    public static string BuildPayload(string? format, string title, string message)
    {
        var text = $"{title}\n\n{message}";

        return (format ?? "generic").ToLowerInvariant() switch
        {
            "dingtalk" => JsonSerializer.Serialize(new
            {
                msgtype = "markdown",
                markdown = new { title, text = $"### {title}\n\n{message}" }
            }, JsonOptions),

            "feishu" => JsonSerializer.Serialize(new
            {
                msg_type = "text",
                content = new { text }
            }, JsonOptions),

            _ => JsonSerializer.Serialize(new { title, message, timestamp = DateTimeOffset.UtcNow }, JsonOptions)
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
