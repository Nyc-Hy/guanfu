using System.Net.Http.Json;
using System.Text.Json;

namespace Workbench.Cli;

/// <summary>工作台 REST API 的 HTTP 客户端。CLI 只走 HTTP，不直连数据库。</summary>
public sealed class WorkbenchClient(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
            new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver())
    };

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
