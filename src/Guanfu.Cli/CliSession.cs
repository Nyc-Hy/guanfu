using System.Text.Json;

namespace Guanfu.Cli;

/// <summary>
/// 命令行的本地会话（谁在用这台机器上的观复）。
///
/// **为什么要有**：观复现在是**一个团队**用的 —— 成员各有各的账号，
/// 每个人看到的可见范围不一样。命令行以前只有一把 owner 的万能凭证，
/// 于是「用命令行的人」永远是 owner —— 团队成员在笔记本上用命令行时，
/// 看到的也是全部（owner 视角），跟他们在网页上看到的不一样。
///
/// 所以命令行也要能**以某个成员的身份**说话：
/// `guanfu login` 换一个身份，`guanfu logout` 收工，`guanfu whoami` 看现在是谁。
///
/// 会话值存在**本地**（数据目录下的 cli.json，0600）——
/// 它是这台机器上这个人的凭据，不该进版本库、不该传给别人。
/// </summary>
internal static class CliSession
{
    private const string FileName = "cli.json";

    /// <summary>本地会话文件的位置（数据目录里）。</summary>
    internal static string PathFor(string? homeDirectory) =>
        Path.Combine(Home(homeDirectory), FileName);

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
    internal static Session? Load(string? homeDirectory)
    {
        try
        {
            var path = PathFor(homeDirectory);

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
    internal static void Save(string? homeDirectory, Session session)
    {
        Directory.CreateDirectory(Home(homeDirectory));

        var path = PathFor(homeDirectory);

        File.WriteAllText(path, JsonSerializer.Serialize(session, Application.GuanfuJson.Options));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>清掉本地会话。</summary>
    internal static void Clear(string? homeDirectory)
    {
        try
        {
            var path = PathFor(homeDirectory);

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
