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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消 —— 不该被当成「通知失败」吞掉
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or InvalidOperationException or UriFormatException)
        {
            // 通知失败不能影响主流程。
            // InvalidOperationException / UriFormatException 是 URL 配错时
            // HttpClient 抛的 —— 以前它们不在名单里，会直接逃出去。
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
