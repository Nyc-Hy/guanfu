using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Workbench.Application;

namespace Workbench.Cli;

/// <summary>
/// MCP stdio ↔ HTTP 桥接。
///
/// MCP 客户端（Claude Code / Codex 等）多以 stdio 方式启动 MCP 进程；
/// 而工作台的数据与服务都在常驻的 Web 进程里。这里把 stdio 的 JSON-RPC
/// 逐条转发到 Web 的 /mcp 端点（Streamable HTTP），并处理会话头与 SSE 响应。
///
/// 已知限制：只转发客户端 → 服务端的方向，服务端主动发起的请求会被忽略。
/// </summary>
public static class McpBridge
{
    public static async Task<int> RunAsync(string baseUrl, List<string> args)
    {
        var endpoint = baseUrl.TrimEnd('/') + "/mcp";
        using var handler = new HttpClientHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource();

        return await RunCoreAsync(
            Console.In,
            Console.Out,
            (request, ct) => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct),
            endpoint,
            cts.Token,
            ResolveRepoPath(args),
            ResolveAgentId(args));
    }

    /// <summary>
    /// 桥接所在的工作目录 —— agent 在这个目录里干活，它操作的仓库就该是它。
    ///
    /// 优先级：--repo 参数 &gt; WORKBENCH_REPO 环境变量 &gt; 当前工作目录。
    /// 传过去之后，工作台能反查出这个目录属于哪个项目，调用就自动归属了 ——
    /// agent 不必再自己背项目 GUID。
    /// </summary>
    private static string? ResolveRepoPath(List<string> args)
    {
        var flag = ReadOption(args, "--repo");
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return flag;
        }

        var fromEnv = Environment.GetEnvironmentVariable("WORKBENCH_REPO");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception)
        {
            // 取不到当前目录就不传 —— 服务端会当成「没绑定」，
            // 老调用（显式传 projectId）照常能用。
            return null;
        }
    }

    /// <summary>agent 标识。--agent &gt; WORKBENCH_AGENT &gt; 默认值。</summary>
    private static string ResolveAgentId(List<string> args)
    {
        var flag = ReadOption(args, "--agent");
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return flag;
        }

        var fromEnv = Environment.GetEnvironmentVariable("WORKBENCH_AGENT");
        return string.IsNullOrWhiteSpace(fromEnv) ? "cli-bridge" : fromEnv;
    }

    /// <summary>取 --name value 形式的选项值。找不到返回 null。</summary>
    private static string? ReadOption(List<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// 桥接核心。stdin / stdout / 发送函数都从参数进来，脱离进程与网络也能单测。
    ///
    /// 并发模型（P1-6）：
    ///   - stdin 读取循环只负责读行 + 分发，**绝不**阻塞在 HTTP 上；
    ///   - 通知（没有 id）进单独的信道，由一个消费者按序立即转发，不被长请求挡住；
    ///   - 请求（有 id）每条起一个任务并发处理（db_execute 等审批可能挂很久，
    ///     串行会让后面的请求全卡住）；
    ///   - 所有 stdout 写入汇到一条信道、由**单一**消费者输出，杜绝多线程写交错污染 JSON-RPC 流。
    /// </summary>
    internal static async Task<int> RunCoreAsync(
        TextReader stdin,
        TextWriter stdout,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        string endpoint,
        CancellationToken cancellationToken,
        string? repoPath = null,
        string? agentId = null)
    {
        var stdoutChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var notificationChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var session = new SessionState
        {
            RepoPath = repoPath,
            AgentId = agentId
        };
        var requestTasks = new ConcurrentBag<Task>();

        // stdout 单一消费者：无论多少并发请求，输出都从这里串行落盘。
        var stdoutTask = Task.Run(async () =>
        {
            await foreach (var line in stdoutChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await stdout.WriteLineAsync(line);
                await stdout.FlushAsync();
            }
        }, cancellationToken);

        // 通知单一消费者：按 stdin 顺序立即转发，不等任何请求。
        var notificationTask = Task.Run(async () =>
        {
            await foreach (var line in notificationChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ForwardNotificationAsync(line, endpoint, sendAsync, session, cancellationToken);
                }
                catch (Exception)
                {
                    // 通知没有响应可回，转发失败也只能吞掉 —— 不能让它弄死桥接。
                }
            }
        }, cancellationToken);

        while (true)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(cancellationToken);
            }
            catch (IOException)
            {
                break; // stdin 被关掉（客户端先退了），当作正常结束。
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // 先摘 id：出错时要拿它回一条 error，否则客户端对不上自己的请求只能干等。
            // TryExtractId 内部已兜住所有异常，这里再包一层确保读取循环永不因单行崩掉。
            object? requestId;
            try
            {
                requestId = McpBridgeProtocol.TryExtractId(line);
            }
            catch (Exception)
            {
                requestId = null;
            }

            if (requestId is null && !McpBridgeProtocol.LooksLikeJson(line))
            {
                stdoutChannel.Writer.TryWrite(
                    McpBridgeProtocol.BuildError(null, -32700, "请求不是合法 JSON。", null));
                continue;
            }

            if (requestId is null)
            {
                // 通知：进信道立即转发，不阻塞读取循环。
                notificationChannel.Writer.TryWrite(line);
            }
            else
            {
                // 请求：并发处理。
                var task = Task.Run(
                    () => ProcessRequestAsync(line, requestId, endpoint, sendAsync, session, stdoutChannel.Writer, cancellationToken),
                    cancellationToken);
                requestTasks.Add(task);
            }
        }

        // stdin 关了：先把通知发完、请求跑完，再收 stdout，保证响应都落盘。
        notificationChannel.Writer.TryComplete();
        try
        {
            await notificationTask;
            await Task.WhenAll(requestTasks);
        }
        catch (OperationCanceledException)
        {
            // 取消时收尾即可，不再往外抛。
        }

        stdoutChannel.Writer.TryComplete();
        try
        {
            await stdoutTask;
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>处理一条请求：转发、处理会话失效（404 重初始化重试一次）、把响应写进 stdout 信道。</summary>
    private static async Task ProcessRequestAsync(
        string line,
        object requestId,
        string endpoint,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        SessionState session,
        ChannelWriter<string> stdout,
        CancellationToken cancellationToken)
    {
        try
        {
            var usedSession = session.Current;
            using var response = await SendWithSessionAsync(line, endpoint, sendAsync, session, cancellationToken);
            CaptureSession(response, session);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 会话失效（服务端重启 / 会话过期）：清掉 sessionId，重新 initialize，再重试一次。
                await session.RecoveryGate.WaitAsync(cancellationToken);
                try
                {
                    // 只有「我用的会话还是当前会话」时才重初始化 ——
                    // 别的并发请求可能已经恢复过了，直接用新会话重试即可，不重复 initialize。
                    if (session.Current == usedSession)
                    {
                        await ReInitializeAsync(endpoint, sendAsync, session, cancellationToken);
                    }
                }
                finally
                {
                    session.RecoveryGate.Release();
                }

                using var retry = await SendWithSessionAsync(line, endpoint, sendAsync, session, cancellationToken);
                CaptureSession(retry, session);
                await WriteResponseAsync(retry, requestId, endpoint, stdout, cancellationToken);
                return;
            }

            await WriteResponseAsync(response, requestId, endpoint, stdout, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            stdout.TryWrite(McpBridgeProtocol.BuildError(
                requestId, -32000, $"无法连接工作台 MCP 端点 {endpoint}: {ex.Message}", null));
        }
        catch (Exception ex)
        {
            // 兜底：任何别的异常都不能让桥接进程死掉。桥接一死，客户端会认为整个 MCP 服务不可用。
            stdout.TryWrite(McpBridgeProtocol.BuildError(
                requestId, -32603, $"MCP 桥接内部错误: {ex.Message}", null));
        }
    }

    /// <summary>转发一条通知（无 id），丢弃响应。会话失效时顺手恢复一次。</summary>
    private static async Task ForwardNotificationAsync(
        string line,
        string endpoint,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        SessionState session,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithSessionAsync(line, endpoint, sendAsync, session, cancellationToken);
        CaptureSession(response, session);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await session.RecoveryGate.WaitAsync(cancellationToken);
            try
            {
                await ReInitializeAsync(endpoint, sendAsync, session, cancellationToken);
            }
            finally
            {
                session.RecoveryGate.Release();
            }

            using var retry = await SendWithSessionAsync(line, endpoint, sendAsync, session, cancellationToken);
            CaptureSession(retry, session);
        }
    }

    /// <summary>
    /// 重新建立会话：发一条合成的 initialize（响应丢弃，不回给客户端——它没请求过），
    /// 拿到新 sessionId 后再发 notifications/initialized。调用方必须已持有 RecoveryGate。
    /// </summary>
    private static async Task ReInitializeAsync(
        string endpoint,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        SessionState session,
        CancellationToken cancellationToken)
    {
        session.Current = null;

        using var initResponse = await SendWithSessionAsync(McpBridgeProtocol.BuildInitializeRequest(), endpoint, sendAsync, session, cancellationToken);
        CaptureSession(initResponse, session);

        using var initializedResponse = await SendWithSessionAsync(
            McpBridgeProtocol.InitializedNotification, endpoint, sendAsync, session, cancellationToken);
        CaptureSession(initializedResponse, session);
    }

    private static async Task<HttpResponseMessage> SendWithSessionAsync(
        string line,
        string endpoint,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        SessionState session,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(line, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");

        if (session.Current is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.Current);
        }

        // 告诉工作台「这次调用是在哪个仓库里发出来的」。
        // 服务端据此反查项目并建立会话 —— agent 之后不用自己背项目 GUID。
        if (!string.IsNullOrWhiteSpace(session.RepoPath))
        {
            request.Headers.TryAddWithoutValidation("X-Workbench-Repo", session.RepoPath);
        }

        if (!string.IsNullOrWhiteSpace(session.AgentId))
        {
            request.Headers.TryAddWithoutValidation("X-Workbench-Agent", session.AgentId);
        }

        return await sendAsync(request, cancellationToken);
    }

    private static void CaptureSession(HttpResponseMessage response, SessionState session)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var id = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(id))
            {
                session.Current = id;
            }
        }
    }

    /// <summary>把一条 HTTP 响应转成 JSON-RPC 输出写进 stdout 信道（SSE / JSON / 错误页都处理）。</summary>
    private static async Task WriteResponseAsync(
        HttpResponseMessage response,
        object requestId,
        string endpoint,
        ChannelWriter<string> stdout,
        CancellationToken cancellationToken)
    {
        // 202 = 通知已接受，无响应体。
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (await reader.ReadLineAsync(cancellationToken) is { } sseLine)
            {
                if (!sseLine.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = sseLine["data:".Length..].Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                stdout.TryWrite(payload);
            }

            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 服务端回的不是 JSON（400/500 的 HTML 错误页）时，绝不能原样写进 stdout ——
            // 那会污染 JSON-RPC 流，客户端解析失败，连真正的错误原因都看不到。
            stdout.TryWrite(McpBridgeProtocol.BuildError(
                requestId, -32001, $"工作台 MCP 端点返回 {response.StatusCode}。",
                McpBridgeProtocol.ExtractErrorDetail(body, contentType)));
            return;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        if (!McpBridgeProtocol.LooksLikeJson(body))
        {
            stdout.TryWrite(McpBridgeProtocol.BuildError(
                requestId, -32603, "工作台 MCP 端点返回了非 JSON 内容。",
                McpBridgeProtocol.Truncate(body, 500)));
            return;
        }

        stdout.TryWrite(body);
    }

    /// <summary>共享会话态。Current 用 volatile 保证并发可见；恢复用闸门串行化。</summary>
    private sealed class SessionState
    {
        public volatile string? Current;

        /// <summary>
        /// 桥接所在的工作目录。每次转发都带上它，服务端据此反查项目。
        /// </summary>
        public string? RepoPath;

        /// <summary>agent 标识，审计里用它区分是谁在干活。</summary>
        public string? AgentId;

        public readonly SemaphoreSlim RecoveryGate = new(1, 1);
    }
}

/// <summary>
/// 桥接的纯逻辑：摘 id、判 JSON、造 error 响应。
/// 单独拎出来是为了能脱离进程和网络做单元测试 —— 这几条全错了的表现是
/// "客户端莫名其妙收不到响应"，不写测试基本发现不了。
/// </summary>
public static class McpBridgeProtocol
{
    /// <summary>
    /// 摘出 JSON-RPC 请求的 id。解析不了就返回 null（通知类请求本来就没有 id）。
    /// 兜住**所有**异常（不只 JsonException）：id 是数字 1.5 / 超大整数时 GetInt64 会抛，
    /// 不能让一条畸形请求把整个读取循环带崩。
    /// </summary>
    public static object? TryExtractId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("id", out var id))
            {
                return null;
            }

            return id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                // 取不出 Int64（小数 / 超范围）就退回原始文本，保住 id 不丢也不抛。
                JsonValueKind.Number => id.TryGetInt64(out var number) ? number : id.GetRawText(),
                JsonValueKind.Null => null,
                _ => id.GetRawText()
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>会话失效后重初始化用的合成 initialize 请求（响应不回给客户端）。</summary>
    public static string BuildInitializeRequest() => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = "__wb_bridge_init__",
        method = "initialize",
        @params = new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "workbench-bridge", version = "0.1.0" }
        }
    }, WorkbenchJson.Options);

    /// <summary>重初始化后补发的 notifications/initialized 通知。</summary>
    public const string InitializedNotification =
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    /// <summary>粗判是不是 JSON（对象或数组）。用来挡住 HTML 错误页。</summary>
    public static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    /// <summary>造一条 JSON-RPC error 响应。id 沿用原请求的。</summary>
    public static string BuildError(object? id, int code, string message, string? data)
    {
        // 两个分支的匿名类型不一样，条件表达式推不出类型 —— 直接分两次序列化。
        var payload = data is null
            ? JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message }
            }, WorkbenchJson.Options)
            : JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message, data }
            }, WorkbenchJson.Options);

        return payload;
    }

    /// <summary>
    /// 从错误响应里抽出能给人看的一句。
    /// 服务端回 HTML 时把标签剥掉留正文，别把一整页标签糊到 agent 脸上。
    /// </summary>
    public static string? ExtractErrorDetail(string body, string contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString();
                    }

                    if (error.TryGetProperty("message", out var message))
                    {
                        return message.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // 不是合法 JSON 就按纯文本处理
            }
        }

        var text = StripTags(body);
        return Truncate(text, 500);
    }

    private static string StripTags(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        var insideTag = false;

        foreach (var ch in html)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                sb.Append(ch);
            }
        }

        return System.Net.WebUtility.HtmlDecode(sb.ToString()).Trim();
    }

    public static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text.Length <= max ? text : text[..max] + "…";
    }
}
