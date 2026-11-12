using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Application;

namespace Workbench.Cli;

/// <summary>工作台 REST API 的 HTTP 客户端。CLI 只走 HTTP，不直连数据库。</summary>
public sealed class WorkbenchClient(string baseUrl)
{
    // 每个子命令 new 一个 HttpClient 会耗尽端口，而且连接池不共享。
    // 按地址缓存一份就行 —— 进程退出时一起回收。
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    private readonly HttpClient _http = Clients.GetOrAdd(
        baseUrl.TrimEnd('/') + "/",
        key => new HttpClient { BaseAddress = new Uri(key), Timeout = TimeSpan.FromMinutes(2) });

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

    /// <summary>取原始文本（导出 Markdown / CSV 时用，JSON 解析会把它弄坏）。</summary>
    public async Task<string> GetRawAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>拼查询串用的转义。编号、关键字里可能有空格和 &，不转义就拼出非法 URL。</summary>
    public static string Query(string name, string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $"{name}={Uri.EscapeDataString(value.Trim())}";

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
