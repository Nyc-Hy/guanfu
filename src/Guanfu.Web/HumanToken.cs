using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Guanfu.Web;

/// <summary>
/// 人工凭证（human token）的策略层。纯函数，不碰 IO，方便单测。
///
/// 为什么需要它：观复默认只监听回环，但设了 ASPNETCORE_URLS=0.0.0.0 之后
/// 内网里任何人都能打到。「人工审批」是这套工具唯一的门禁，所以裁决审批这类
/// 端点必须有一把只有人拿得到的钥匙 —— agent 走 MCP / REST 都拿不到它，
/// 就没法自己批准自己发起的写操作。
/// </summary>
public static class HumanTokenPolicy
{
    /// <summary>请求头名。给脚本 / CLI / Web UI 的服务端调用用。</summary>
    public const string HeaderName = "X-Guanfu-Human-Token";

    /// <summary>
    /// 改名之前的头。**仍然认** —— agent 的配置里可能硬编码了它，
    /// 改名不该让人突然失去「有人在旁边」的资格。
    /// </summary>
    public const string LegacyHeaderName = "X-Workbench-Human-Token";

    /// <summary>Cookie 名。给 Web UI 用 —— 浏览器同源请求会自动带上。</summary>
    public const string CookieName = "guanfu_human_token";

    /// <summary>改名之前的 cookie 名 —— 仍然认，别让人凭空掉登录态。</summary>
    public const string LegacyCookieName = "wb_human_token";

    /// <summary>
    /// 401 的响应体。刻意不区分「没带」和「带错了」——
    /// 多给一点信息就多给攻击者一点信息。
    /// </summary>
    public const string UnauthorizedBody =
        "{\"ok\":false,\"error\":\"该端点需要人工凭证：请从启动观复的控制台复制 token，" +
        "通过请求头 " + HeaderName + " 或 Cookie " + CookieName + " 传入。\"}";

    /// <summary>
    /// 判断某个请求是不是「必须有人在场」的端点。
    ///
    /// 覆盖的是「拿到这个能力就能自己给自己放行」的那几个口子：
    /// 裁决 / 撤销审批、改项目配置（agentCommand / 命令白名单）、启动外部 agent、
    /// 工作区搬迁、存数据库连接、改表级规则（免审批写的门）、探活数据库连接、
    /// MCP 工具开关。其余端点一律保持开放 ——
    /// agent 走 MCP、CI 走普通任务写，堵死了整套工具就没用了。
    /// 新增写端点必须在这里或端点对账测试的开放清单里归类，否则测试会红。
    /// </summary>
    public static bool IsSensitiveEndpoint(string? method, string? path)
    {
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = Split(path);
        if (segments.Length == 0 || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            // MCP 端点（/mcp）不在这里 —— agent 走的就是它，它不能有裁决权
            return false;
        }

        // /api/approvals/{id}/decide —— 裁决审批，最关键的一步
        if (segments.Length == 4
            && segments[1].Equals("approvals", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("decide", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/approvals/batch-decide —— 批量裁决。
        // 这条必须一起保护：它是「批量版」的裁决，
        // 只保护单条那条的话，绕一圈就又能批任意条了。
        if (segments.Length == 3
            && segments[1].Equals("approvals", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("batch-decide", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/approvals/{id}/revoke —— 人工撤销。
        // 撤销改变的是「哪条写操作能执行」，和裁决同权，必须有人在场。
        if (segments.Length == 4
            && segments[1].Equals("approvals", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("revoke", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/db-connections/{id}/rules —— 表级规则。
        // 加一条「免审批写」的规则等于给 db_execute 开了一扇不要审批的门，
        // 删规则同理（改的是门禁本身）。读规则是普通读，放行。
        if (segments.Length == 4
            && segments[1].Equals("db-connections", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("rules", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method) || IsDelete(method);
        }

        // DELETE /api/db-connections/{id}/rules/{ruleId} —— 带 ruleId 的 5 段路径
        if (segments.Length == 5
            && segments[1].Equals("db-connections", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("rules", StringComparison.OrdinalIgnoreCase))
        {
            return IsDelete(method);
        }

        // /api/db-connections/{id}/test —— 拿存好的密码去连数据库。
        // 连接串与密码只有人配过，探活会暴露内网可达性，归入人工操作。
        if (segments.Length == 4
            && segments[1].Equals("db-connections", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // POST /api/audit/archive —— 往观复的数据目录**之外**写归档文件。
        // 归档位置由部署方决定，写出去的东西（链头哈希）就是将来的对账依据，
        // 跟改项目配置同权：要人工在场。
        // 读归档（GET /api/audit/archive）只是对账，不算敏感。
        if (segments.Length == 3
            && segments[1].Equals("audit", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("archive", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/mcp/tools/reset —— 一键恢复全部工具，同样是改能力面。
        // 必须排在 tools/{name} 之前：否则 reset 会被那条更宽的规则先接住，
        // 而那条只认 PUT，POST reset 就漏判了。
        if (segments.Length == 4
            && segments[1].Equals("mcp", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("tools", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/mcp/tools/{name} —— 工具开关。
        // 关掉审批类工具或打开危险工具改的是 agent 的能力面，必须有人在场。
        if (segments.Length == 4
            && segments[1].Equals("mcp", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("tools", StringComparison.OrdinalIgnoreCase))
        {
            return IsPut(method);
        }

        // POST /api/projects —— 新建项目。
        // 项目一创建就把一个**文件系统根目录**授给了这个项目：建完之后
        // fs_list / fs_read 就以它为根，读得到根下面的一切。
        // 改项目配置（改 repoPath）要凭证，创建项目却不要 —— 等于开了扇后门：
        // 拿到 MCP / REST 的 agent 自己建个项目指向 /etc 就能把文件读出去。
        // 建项目是人的决定（它决定了后面谁能读哪些目录），必须有人在场。
        if (segments.Length == 2
            && segments[1].Equals("projects", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/workspaces/{id}/spec —— 改工作区标准规约（所有项目通用的底线）。
        if (segments.Length == 4
            && segments[1].Equals("workspaces", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("spec", StringComparison.OrdinalIgnoreCase))
        {
            return !IsRead(method);
        }

        // /api/tasks/{id}/pin —— 排期干预（排进人工计划）。
        // 「先干哪个」是人的决定 —— agent 不该自己改顺序。
        if (segments.Length == 4
            && segments[1].Equals("tasks", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("pin", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/milestones/{id}/spec —— 改里程碑级规约（这个版本的完成定义）。
        // 规矩是人的决定，agent 不该自己改「这个版本什么算做完」。
        if (segments.Length == 4
            && segments[1].Equals("milestones", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("spec", StringComparison.OrdinalIgnoreCase))
        {
            return !IsRead(method);
        }

        // /api/milestones/{id}/accept | /unaccept —— 验收是人的签字：
        // 「这个版本做完了」得由人确认，agent 不该自己宣布。
        if (segments.Length == 4
            && segments[1].Equals("milestones", StringComparison.OrdinalIgnoreCase)
            && (segments[3].Equals("accept", StringComparison.OrdinalIgnoreCase) ||
                segments[3].Equals("unaccept", StringComparison.OrdinalIgnoreCase)))
        {
            return IsPost(method);
        }

        // PUT /api/projects/{id}/spec —— 改项目级规约（DoD / 提交约定 / 测试命令）。
        // 规矩是**人的决定**：agent 读它、照着做，但不该自己改「什么算做完」。
        if (segments.Length == 4
            && segments[1].Equals("projects", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("spec", StringComparison.OrdinalIgnoreCase))
        {
            return !IsRead(method);
        }

        // /api/projects/{id}/config —— 改 agentCommand / 命令白名单
        // 读配置是普通读，任何写方法都要人工在场
        if (segments.Length == 4
            && segments[1].Equals("projects", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            return !IsRead(method);
        }

        // /api/projects/{id}/db-connections —— 会存数据库密码
        if (segments.Length == 4
            && segments[1].Equals("projects", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("db-connections", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/tasks/{id}/launch-agent —— 执行外部命令
        if (segments.Length == 4
            && segments[1].Equals("tasks", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("launch-agent", StringComparison.OrdinalIgnoreCase))
        {
            return IsPost(method);
        }

        // /api/workspace/export | /api/workspace/import —— 整目录搬家
        if (segments.Length == 3
            && segments[1].Equals("workspace", StringComparison.OrdinalIgnoreCase)
            && (segments[2].Equals("export", StringComparison.OrdinalIgnoreCase)
                || segments[2].Equals("import", StringComparison.OrdinalIgnoreCase)))
        {
            return IsPost(method);
        }

        return false;
    }

    /// <summary>请求带了正确的 header 或 cookie，就算有人在场。</summary>
    public static bool IsAuthorized(string? headerValue, string? cookieValue, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(headerValue) && TokenMatches(headerValue, expected))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(cookieValue) && TokenMatches(cookieValue, expected))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 人工凭证的指纹（哈希前 12 位）。
    ///
    /// 共享 token 的现状下系统认不出真人 —— 但记下凭证指纹，好歹能区分
    /// 「同一个操作员」和「换了个人在操作」，而且不用把 token 明文写进审计。
    ///
    /// REST 与界面两条路**用同一个算法** —— 不然界面上批的和脚本里批的
    /// 指纹对不上，审计就分不出是谁在操作了。
    /// </summary>
    public static string? Fingerprint(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(hash)[..12];
    }

    /// <summary>
    /// 固定时间比较。
    ///
    /// 先各自哈希再比：FixedTimeEquals 在长度不等时会直接返回 false，
    /// 那就把「token 长度」泄出去了。哈希之后两边都是 32 字节，
    /// 比较耗时就与内容、长度都无关了。
    /// </summary>
    public static bool TokenMatches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var left = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>
    /// 是不是回环地址。IPv4-mapped 的 IPv6（::ffff:127.0.0.1）要归一化 ——
    /// Kestrel 监听双栈时看到的就是这种形式，而 IPAddress.IsLoopback 对它判 false。
    /// </summary>
    public static bool IsLoopback(IPAddress? address)
    {
        var normalized = Normalize(address);
        return normalized is not null && IPAddress.IsLoopback(normalized);
    }

    /// <summary>把 IPv4-mapped 的 IPv6 归一化成 IPv4，方便比较。</summary>
    public static IPAddress? Normalize(IPAddress? address) =>
        address is not null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsPost(string method) => method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    private static bool IsPut(string method) => method.Equals("PUT", StringComparison.OrdinalIgnoreCase);

    private static bool IsDelete(string method) => method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

    private static bool IsRead(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    private static string[] Split(string path)
    {
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// 人工凭证的落盘位置与生成。
///
/// token 放在 数据目录/.human-token，权限 0600，重启不变 ——
/// UI 里种下的 Cookie 不能因为服务重启就失效。
/// </summary>
public sealed class HumanTokenStore
{
    public const string TokenFileName = ".human-token";

    private const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _filePath;

    public HumanTokenStore(string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new ArgumentException("数据目录 不能为空。", nameof(homeDirectory));
        }

        _filePath = Path.Combine(Path.GetFullPath(homeDirectory), TokenFileName);
    }

    public string FilePath => _filePath;

    /// <summary>读出 token；没有就生成一个。并发启动时以先落盘的那个为准。</summary>
    public string LoadOrCreate()
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        if (File.Exists(_filePath))
        {
            var existing = File.ReadAllText(_filePath).Trim();
            if (!string.IsNullOrEmpty(existing))
            {
                EnsurePermissions();
                return existing;
            }
        }

        var token = GenerateToken();
        Write(token);
        return token;
    }

    /// <summary>32 字节随机 → base64url（43 字符，没有 + / = ，可以直接粘到命令行里）。</summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private void Write(string token)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            // 建文件的那一刻就是 0600 —— 先建再 chmod 中间有个谁都能读到的窗口
            options.UnixCreateMode = SecretFileMode;
        }

        using (var stream = new FileStream(_filePath, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(token);
        }

        // open(2) 还会过一遍 umask，落地后再钉死一次
        EnsurePermissions();
    }

    private void EnsurePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_filePath, SecretFileMode);
    }
}

/// <summary>
/// 人工凭证的运行时入口。
///
/// Web UI（razor）可以直接注入它做自己的门禁 —— UI 那条路是直接调应用服务的，
/// 不经过 REST，REST 上的门禁管不到它，所以 UI 必须自己判一次。
/// </summary>
public sealed class HumanTokenAccessor
{
    private readonly string _token;

    public HumanTokenAccessor(string token) => _token = token ?? string.Empty;

    /// <summary>token 本身。别写进日志。</summary>
    public string Token => _token;

    /// <summary>请求带了正确的 header 或 cookie，就算有人在场。</summary>
    public bool IsAuthorized(HttpRequest request) =>
        HumanTokenPolicy.IsAuthorized(
            request.Headers[HumanTokenPolicy.HeaderName].ToString() is { Length: > 0 } header
                ? header
                : request.Headers[HumanTokenPolicy.LegacyHeaderName].ToString(),
            request.Cookies[HumanTokenPolicy.CookieName]
            ?? request.Cookies[HumanTokenPolicy.LegacyCookieName],
            _token);

    /// <summary>
    /// 请求是不是「直接从本机发出来的」。
    ///
    /// 只看转发之后的地址不够：X-Forwarded-For 能把回环地址伪造出来。
    /// 转发前后一致，才能说明这条请求真是本机发出去的。
    /// </summary>
    public bool IsDirectLoopbackRequest(HttpContext context)
    {
        var effective = HumanTokenPolicy.Normalize(context.Connection.RemoteIpAddress);
        var original = HumanTokenPolicy.Normalize(OriginalRemoteAddressMiddleware.Get(context));

        if (effective is null || original is null)
        {
            return false;
        }

        if (!HumanTokenPolicy.IsLoopback(effective) || !HumanTokenPolicy.IsLoopback(original))
        {
            return false;
        }

        return effective.Equals(original);
    }
}
