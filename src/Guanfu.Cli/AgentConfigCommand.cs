using Guanfu.Application.Contracts;
using static Guanfu.Cli.CliArgs;

namespace Guanfu.Cli;

/// <summary>
/// 打印接 agent 的配置片段。
///
/// 观复的「接入 agent」页也能复制 —— 但**接 agent 的人常常在服务器外面**
/// （自己的笔记本、CI 机器），那时手边只有命令行。
/// 配置片段的形状与页面共用一份（`McpConnectInfo`）—— 抄两份就会不一样。
/// </summary>
internal static class AgentConfigCommand
{
    public static int Run(string url, List<string> cliArgs)
    {
        var baseUrl = GetOption(cliArgs, "--url") ?? url.TrimEnd('/');
        var kind = (GetOption(cliArgs, "--client") ?? "http").ToLowerInvariant();

        switch (kind)
        {
            case "http":
                Console.WriteLine(McpConnectInfo.HttpConfig(baseUrl));
                Console.WriteLine();
                Console.WriteLine("把 <凭证> 换成「访问凭证」页签里签发的那一把（建议绑到成员名下）。");
                return 0;

            case "stdio":
                Console.WriteLine(McpConnectInfo.StdioConfig(baseUrl));
                Console.WriteLine();
                Console.WriteLine("只支持 stdio 的客户端走这条（guanfu 必须在 PATH 里）。");
                return 0;

            default:
                Console.Error.WriteLine("用法: guanfu agent-config [--client http|stdio] [--url 地址]");
                return 1;
        }
    }
}
