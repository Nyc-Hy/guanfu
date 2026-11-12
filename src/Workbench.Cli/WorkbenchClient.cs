using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Application;

namespace Workbench.Cli;

/// <summary>工作台 REST API 的 HTTP 客户端。CLI 只走 HTTP，不直连数据库。</summary>
public sealed class WorkbenchClient(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// CLI 统一用应用层那一份 JSON 选项：读服务端、打印给自己看都用它。
    /// 抄第二份就会出现「读的时候能读枚举、打印的时候写成数字」这种鬼。
    /// </summary>
    public static JsonSerializerOptions JsonOptions => Json;

    private static readonly JsonSerializerOptions Json = WorkbenchJson.Options;

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? default!;
    }

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? default!;
    }

    public async Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync(path, body, Json, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? default!;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(path, ct);
        await EnsureOkAsync(response, ct);
    }

    public async Task<string> PostRawAsync(string path, object body, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        throw new WorkbenchCliException($"HTTP {(int)response.StatusCode}: {text}");
    }
}

public sealed class WorkbenchCliException(string message) : Exception(message);

/// <summary>CLI 的 JSON 选项集中在这里，别再抄第二份。</summary>
internal static class CliJson
{
    /// <summary>给人看的 JSON：缩进 + 枚举输出成字符串，和服务端一致。</summary>
    public static JsonSerializerOptions Pretty { get; } =
        new(WorkbenchClient.JsonOptions) { WriteIndented = true };
}
