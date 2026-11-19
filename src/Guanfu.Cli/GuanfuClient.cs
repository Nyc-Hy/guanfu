using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Guanfu.Application;

namespace Guanfu.Cli;

/// <summary>观复 REST API 的 HTTP 客户端。CLI 只走 HTTP，不直连数据库。</summary>
public sealed class GuanfuClient(string baseUrl, string? token = null)
{
    /// <summary>人工凭证请求头。敏感端点（裁决/撤销审批、改配置、探活等）必须带它。</summary>
    public const string HumanTokenHeader = "X-Guanfu-Human-Token";

    /// <summary>改名之前的头 —— 一并带上，老服务端也认。</summary>
    public const string LegacyHumanTokenHeader = "X-Workbench-Human-Token";

    // 每个子命令 new 一个 HttpClient 会耗尽端口，而且连接池不共享。
    // 按地址缓存一份就行 —— 进程退出时一起回收。
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    private readonly HttpClient _http = Clients.GetOrAdd(
        baseUrl.TrimEnd('/') + "/",
        key => new HttpClient { BaseAddress = new Uri(key), Timeout = TimeSpan.FromMinutes(2) });

    // token 优先级：--token 显式 > GUANFU_HUMAN_TOKEN（旧的 WORKBENCH_HUMAN_TOKEN 也认） > 数据目录/.human-token 文件。
    // HttpClient 按地址共享，所以 token 不能塞进 DefaultRequestHeaders（不同 token 会互相串），
    // 必须每条请求单独带头。
    private readonly string? _token = ResolveToken(
        token,
        // 新的环境变量优先；旧的仍然认（脚本里的写法不该失效）
        Environment.GetEnvironmentVariable("GUANFU_HUMAN_TOKEN")
        ?? Environment.GetEnvironmentVariable("WORKBENCH_HUMAN_TOKEN"),
        ResolveHomeDirectory());

    /// <summary>
    /// CLI 统一用应用层那一份 JSON 选项：读服务端、打印给自己看都用它。
    /// 抄第二份就会出现「读的时候能读枚举、打印的时候写成数字」这种鬼。
    /// </summary>
    public static JsonSerializerOptions JsonOptions => Json;

    private static readonly JsonSerializerOptions Json = GuanfuJson.Options;

    /// <summary>
    /// 解析人工凭证。纯函数（显式 / 环境变量 / home 目录都从参数进来），方便单测。
    /// 读不到文件就当作没有 token —— 非敏感端点照样能用。
    /// </summary>
    internal static string? ResolveToken(string? explicitToken, string? envToken, string? homeDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken))
        {
            return explicitToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return envToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            try
            {
                var path = Path.Combine(homeDirectory, ".human-token");
                if (File.Exists(path))
                {
                    var fileToken = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrEmpty(fileToken))
                    {
                        return fileToken;
                    }
                }
            }
            catch (Exception)
            {
                // 文件读不了（权限/IO）就当作没有 token，不让 CLI 直接崩。
            }
        }

        return null;
    }

    /// <summary>数据目录，缺省 ~/.workbench（与 Web 侧 GuanfuOptions 同口径）。</summary>
    internal static string? ResolveHomeDirectory()
    {
        // 新的环境变量优先；旧的仍然认
        foreach (var variable in new[] { "GUANFU_HOME", "WORKBENCH_HOME" })
        {
            var fromEnv = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Path.GetFullPath(fromEnv);
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".workbench");
    }

    /// <summary>
    /// 本地会话的 Cookie（<c>guanfu login</c> 换来的身份）。
    ///
    /// **为什么命令行要认这个**：观复是一个团队用的，成员各有各的可见范围。
    /// 命令行若只用 owner 的万能凭证，团队成员在笔记本上看到的就永远是
    /// owner 视角 —— 跟他在网页上看到的不一样。
    /// </summary>
    private readonly string? _sessionCookie = CliSession
        .Load(GuanfuClient.ResolveHomeDirectory())?.Cookie;

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(_token))
        {
            request.Headers.TryAddWithoutValidation(HumanTokenHeader, _token);

            // 旧的头也带上 —— 老服务端还认它
            request.Headers.TryAddWithoutValidation(LegacyHumanTokenHeader, _token);
        }

        // 成员会话（若有）—— 服务端据此认出「是谁」，可见性按他算。
        if (!string.IsNullOrWhiteSpace(_sessionCookie))
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie", $"guanfu_session={_sessionCookie}");
        }

        return request;
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? default!;
    }

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, options: Json);
        using var response = await _http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? default!;
    }

    public async Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Content = JsonContent.Create(body, options: Json);
        using var response = await _http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? default!;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
    }

    public async Task<string> PostRawAsync(string path, object body, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, options: Json);
        using var response = await _http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// 上传 multipart/form-data（文档上传用）。
    /// 以前 doc upload 把 MultipartFormDataContent 塞进 PostRawAsync，
    /// 后者用 PostAsJsonAsync 把它当 JSON 序列化 —— 服务端收到的是一坨 JSON 而不是文件，上传必坏。
    /// 这里直接把 multipart 内容作为请求体发出去。
    /// </summary>
    public async Task<string> PostMultipartAsync(string path, MultipartFormDataContent content, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = content;
        using var response = await _http.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>取原始文本（导出 Markdown / CSV 时用，JSON 解析会把它弄坏）。</summary>
    public async Task<string> GetRawAsync(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, ct);
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
        throw new GuanfuCliException($"HTTP {(int)response.StatusCode}: {text}");
    }
}

public sealed class GuanfuCliException(string message) : Exception(message);

/// <summary>CLI 的 JSON 选项集中在这里，别再抄第二份。</summary>
internal static class CliJson
{
    /// <summary>给人看的 JSON：缩进 + 枚举输出成字符串，和服务端一致。</summary>
    public static JsonSerializerOptions Pretty { get; } =
        new(GuanfuClient.JsonOptions) { WriteIndented = true };
}
