using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Guanfu.Cli;

/// <summary>按服务地址保存 CLI 登录会话。</summary>
internal static class CliSession
{
    internal static string PathFor(string? homeDirectory, string url)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliServer.Normalize(url))));
        return Path.Combine(Home(homeDirectory), $"cli-session-{key}.json");
    }

    /// <summary>数据目录（没配就用用户主目录下的 .guanfu）。</summary>
    internal static string Home(string? homeDirectory)
    {
        // 会话文件可以单独放（<c>GUANFU_CLI_HOME</c>）——
        // 多人共用一台机器 / 测试时不想写进数据目录，都靠它。
        var dedicated = Environment.GetEnvironmentVariable("GUANFU_CLI_HOME");

        if (!string.IsNullOrWhiteSpace(dedicated))
        {
            return dedicated;
        }

        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            return homeDirectory;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var current = Path.Combine(profile, ".guanfu");
        var legacy = Path.Combine(profile, ".workbench");

        return Directory.Exists(legacy) && !Directory.Exists(current) ? legacy : current;
    }

    /// <summary>读本地会话；没有或读不了就返回 null（当没登录）。</summary>
    internal static Session? Load(string? homeDirectory, string url)
    {
        try
        {
            var path = PathFor(homeDirectory, url);

            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);

            return JsonSerializer.Deserialize<Session>(json, Application.GuanfuJson.Options);
        }
        catch (Exception)
        {
            // 读不了就当没登录 —— 不让 CLI 因为一个坏文件直接崩
            return null;
        }
    }

    /// <summary>写本地会话（0600：只有本人能读）。</summary>
    internal static void Save(string? homeDirectory, string url, Session session)
    {
        Directory.CreateDirectory(Home(homeDirectory));

        var path = PathFor(homeDirectory, url);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, session, Application.GuanfuJson.Options);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>清掉本地会话。</summary>
    internal static void Clear(string? homeDirectory, string url)
    {
        try
        {
            var path = PathFor(homeDirectory, url);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉就算了 —— 下次登录会覆盖
        }
    }

    /// <summary>本地会话：谁的 cookie。</summary>
    internal sealed record Session(string UserName, string Cookie);
}
