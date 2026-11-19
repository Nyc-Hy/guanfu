using System.Net;
using System.Text;
using Guanfu.Application.Contracts;
using static Guanfu.Cli.CliArgs;

namespace Guanfu.Cli;

/// <summary>
/// 账号验证：命令行**以某个成员的身份**说话。
///
/// 观复是一个团队用的 —— 成员各有各的账号与可见范围。
/// 命令行以前只有一把 owner 的万能凭证，于是「用命令行的人」永远是 owner。
/// 团队成员在自己的笔记本上敲 `guanfu task list`，看到的却是全部 ——
/// 跟他在网页上看到的不一样。
///
/// 所以：登录换一个身份，退出收工，`whoami` 看现在是谁。
/// </summary>
internal static class AuthCommand
{
    public static async Task<int> LoginAsync(string url, List<string> cliArgs)
    {
        {
                if (cliArgs.Count < 1)
                {
                    Console.Error.WriteLine("用法: guanfu login <用户名> [--password 密码]");
                    return 1;
                }

                var userName = Arg(cliArgs, 0);
                var password = GetOption(cliArgs, "--password") ?? ReadPassword();

                if (string.IsNullOrWhiteSpace(password))
                {
                    Console.Error.WriteLine("没有密码 —— 用 --password 或从标准输入读一行。");
                    return 1;
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
                using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/auth/login")
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(new { userName, password }),
                        Encoding.UTF8,
                        "application/json")
                };

                using var response = await http.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Console.Error.WriteLine($"登录失败（HTTP {(int)response.StatusCode}）：{body}");
                    return 1;
                }

                var cookie = ReadSessionCookie(response);

                if (cookie is null)
                {
                    Console.Error.WriteLine("服务端没给会话 Cookie —— 观复的版本不对？");
                    return 1;
                }

                var home = GuanfuClient.ResolveHomeDirectory();
                CliSession.Save(home, new CliSession.Session(userName, cookie));

                Console.WriteLine($"已登录：{userName}（会话存在 {CliSession.PathFor(home)}）");
                Console.WriteLine("这条命令行现在以这个成员的身份说话 —— 可见范围按他算。");
                return 0;
        }
    }

    public static async Task<int> LogoutAsync(string url)
    {
        {
                var home = GuanfuClient.ResolveHomeDirectory();

                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/auth/logout");
                    await http.SendAsync(request);
                }
                catch (Exception)
                {
                    // 服务端连不上也要把本地的会话清掉
                }

                CliSession.Clear(home);
                Console.WriteLine("已退出登录（本地会话已清）。");
                return 0;
        }
    }

    public static async Task<int> WhoamiAsync(string url)
    {
        {
                var me = await new GuanfuClient(url, CliOptions.Token)
                    .GetAsync<MeDto>("/api/auth/me");

                if (me.Member is null)
                {
                    Console.WriteLine("没登录（也没带凭证）—— 现在看到的是本机 owner 视角。");
                    Console.WriteLine("要换成某个成员：guanfu login <用户名>");
                    return 0;
                }

                var role = me.Member.Role == "Admin" ? "管理员" : "成员";
                var name = string.IsNullOrWhiteSpace(me.Member.DisplayName)
                    ? me.Member.UserName ?? "?"
                    : me.Member.DisplayName;

                Console.WriteLine($"现在是谁：{name}（{role}）· 登录名 {me.Member.UserName}");
                return 0;
        }
    }

    /// <summary>从响应头里抠出会话 Cookie 的值。</summary>
    private static string? ReadSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            var pair = value.Split(';')[0].Trim();

            if (pair.StartsWith("guanfu_session=", StringComparison.Ordinal))
            {
                return pair["guanfu_session=".Length..];
            }
        }

        return null;
    }

    /// <summary>从标准输入读一行密码（不进 shell 历史）。</summary>
    private static string? ReadPassword()
    {
        try
        {
            return Console.In.ReadLine();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>「我是谁」的回包（字段名与 /api/auth/me 一致）。</summary>
    private sealed record MeDto(MeInfo? Member);

    private sealed record MeInfo(Guid? Id, string? UserName, string? DisplayName, string? Role, bool IsAdmin);
}
