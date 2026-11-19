using System.Net.Http;
using Guanfu.Application.Contracts;
using Guanfu.Domain;
using static Guanfu.Cli.CliArgs;

namespace Guanfu.Cli;

/// <summary>
/// 本地文件报进观复。
///
/// **这里只留 MCP 做不到的那条路**：MCP 的工具在**服务端**跑，
/// 它够不到调用者本地的文件 —— 想把本地生成的脚本 / 报告交进观复，
/// 得有人在本地把文件读出来再送过去。命令行就是那个人。
///
/// 别的（推进状态、回写计划、报测试结果）都走 `guanfu call <工具名>` ——
/// **同一套工具面**，只是换个调用者。命令行不另抄一套。
/// </summary>
internal static class ReportCommand
{
    public static async Task<int> RunAsync(string url, List<string> cliArgs)
    {
        var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "help";

        switch (sub)
        {
            case "artifact":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine(
                        "用法: guanfu report artifact <任务ID或编号> --file <本地文件> [--title 标题] [--type 类型]");
                    return 1;
                }

                var file = GetOption(cliArgs, "--file");

                if (file is null || !File.Exists(file))
                {
                    Console.Error.WriteLine("要给一个**本地**文件：--file <路径>");
                    return 1;
                }

                var title = GetOption(cliArgs, "--title") ?? Path.GetFileName(file);
                var type = GetOption(cliArgs, "--type") ?? "Other";

                if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var artifactType))
                {
                    Console.Error.WriteLine($"未知产物类型：{type}。可用：{UiText.ArtifactTypeNames()}");
                    return 1;
                }

                var taskId = await ResolveTaskIdAsync(url, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var content = await File.ReadAllTextAsync(file);

                // 走 MCP 工具 —— 与 agent 用的是同一套（同一套校验、同一套审计）
                var result = await McpCallCommand.CallAsync(
                    url,
                    "guanfu_add_artifact",
                    new Dictionary<string, object?>
                    {
                        ["taskId"] = taskId.ToString(),
                        ["title"] = title,
                        ["type"] = artifactType.ToString(),
                        ["content"] = content,
                        ["author"] = GetOption(cliArgs, "--actor") ?? "local",
                    },
                    McpBridge.ResolveToken(cliArgs));

                Console.WriteLine(result);
                return 0;
            }

            case "doc":
            {
                if (cliArgs.Count < 3)
                {
                    Console.Error.WriteLine(
                        "用法: guanfu report doc <任务ID或编号> <本地文件...> [--kind prd|testcase|apidoc|delivery|other]");
                    return 1;
                }

                var taskId = await ResolveTaskIdAsync(url, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var client = new GuanfuClient(url, CliOptions.Token);
                var kind = GetOption(cliArgs, "--kind");
                var files = cliArgs.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
                var uploaded = 0;

                foreach (var file in files)
                {
                    if (!File.Exists(file))
                    {
                        Console.Error.WriteLine($"文件不存在：{file}");
                        continue;
                    }

                    var content = new MultipartFormDataContent();
                    var part = new ByteArrayContent(await File.ReadAllBytesAsync(file));
                    part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Add(part, "file", Path.GetFileName(file));

                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        content.Add(new StringContent(kind), "kind");
                    }

                    await client.PostMultipartAsync($"/api/tasks/{taskId}/documents", content);
                    uploaded++;
                }

                Console.WriteLine($"已上传 {uploaded} 个文档");
                return uploaded > 0 ? 0 : 1;
            }

            default:
                Console.Error.WriteLine("""
                    用法:
                      guanfu report artifact <任务ID或编号> --file <本地文件> [--title 标题] [--type 类型]
                      guanfu report doc      <任务ID或编号> <本地文件...> [--kind 类别]

                    这两条走的是「读**本地**文件」—— MCP 的工具在服务端跑，够不到本地文件。

                    其余的事走同一套 MCP 工具：
                      guanfu call guanfu_update_status --arg taskId=... --arg status=InDev
                      guanfu call guanfu_set_plan --arg taskId=... --arg understanding=...
                    """);
                return 1;
        }
    }

    private static async Task<Guid?> ResolveTaskIdAsync(string url, string reference)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var client = new GuanfuClient(url, CliOptions.Token);

        var found = await client.GetAsync<List<TaskSummaryDto>>(
            $"/api/tasks?key={Uri.EscapeDataString(reference)}&limit=1");

        return found.Count > 0 ? found[0].Id : null;
    }
}
