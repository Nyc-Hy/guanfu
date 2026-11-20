namespace Guanfu.Cli;

/// <summary>CLI 的默认服务地址。会话与配置保存在同一个本地配置目录。</summary>
internal static class CliServer
{
    private const string FileName = "cli-server-url";

    internal static string Resolve(string? explicitUrl)
    {
        var url = explicitUrl ?? Environment.GetEnvironmentVariable("GUANFU_URL")
            ?? Environment.GetEnvironmentVariable("WORKBENCH_URL") ?? ReadDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new GuanfuCliException("尚未配置观复服务。请用 guanfu configure --url <地址>、GUANFU_URL 或本次命令的 --url。");
        }

        return Normalize(url);
    }

    internal static int Configure(string? explicitUrl)
    {
        if (explicitUrl is null)
        {
            Console.WriteLine(ReadDefault() is { } configured ? $"已配置观复服务：{configured}" : "尚未设置默认服务。用法：guanfu configure --url <地址>");
            return 0;
        }

        var url = Normalize(explicitUrl);
        var path = ConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, url);
        Console.WriteLine($"已配置观复服务：{url}");
        return 0;
    }

    internal static string Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new GuanfuCliException("服务地址须为完整的 HTTP 或 HTTPS 地址，且不能带账号、查询参数或片段。");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string ConfigPath() =>
        Path.Combine(CliSession.Home(GuanfuClient.ResolveHomeDirectory()), FileName);

    private static string? ReadDefault()
    {
        var path = ConfigPath();
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    internal static HttpClientHandler NewHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
}
