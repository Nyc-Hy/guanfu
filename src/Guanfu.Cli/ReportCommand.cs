using System.Net.Http;
using System.Text;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;
using static Guanfu.Cli.CliArgs;

namespace Guanfu.Cli;

/// <summary>
/// 本地配套工具：把**本地的结果**报进观复。
///
/// 观复是过程与记录的中枢 —— 谁在什么���候做了什么，得有一道口子能报进来。
/// agent 走 MCP 报；**本地脚本没有 agent**（CI、部署脚本、代码生成器、git 钩子），
/// 所以命令行得能报同样的东西：一行命令把状态、产出、计划、测试结果、文档递进去。
/// </summary>
internal static class ReportCommand
{
    public static async Task<int> RunAsync(string url, List<string> cliArgs)
    {
        var client = new GuanfuClient(url, CliOptions.Token);
        var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "help";

        switch (sub)
        {
            case "status":
            {
                if (cliArgs.Count < 3)
                {
                    Console.Error.WriteLine("用法: guanfu report status <任务ID或编号> <状态> [--note 说明] [--actor 谁]");
                    return 1;
                }

                var taskId = await CliResolve.TaskIdAsync(client, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var status = Arg(cliArgs, 2);

                if (!UiText.TryParseStatus(status, out var parsed))
                {
                    Console.Error.WriteLine($"未知状态：{status}。可用：{UiText.StatusNames()}");
                    return 1;
                }

                var result = await client.PostAsync<TaskSummaryDto>(
                    $"/api/tasks/{taskId}/status", new
                    {
                        status = parsed.ToString(),
                        note = GetOption(cliArgs, "--note"),
                        actor = GetOption(cliArgs, "--actor") ?? "local",
                    });

                Console.WriteLine($"已推进到 {UiText.Status(result.Status)} · {result.Key} {result.Title}");
                return 0;
            }

            case "artifact":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine(
                        "用法: guanfu report artifact <任务ID或编号> --title <标题> [--type 类型] [--file 路径] [--content 文本]");
                    return 1;
                }

                var taskId = await CliResolve.TaskIdAsync(client, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var title = GetOption(cliArgs, "--title") ?? Path.GetFileName(GetOption(cliArgs, "--file") ?? "产物");
                var type = GetOption(cliArgs, "--type") ?? "Other";

                if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var artifactType))
                {
                    Console.Error.WriteLine($"未知产物类型：{type}。可用：{UiText.ArtifactTypeNames()}");
                    return 1;
                }

                var content = GetOption(cliArgs, "--content");
                var file = GetOption(cliArgs, "--file");

                if (file is not null)
                {
                    content = await File.ReadAllTextAsync(file);
                }

                if (content is null)
                {
                    Console.Error.WriteLine("要给内容：--file <路径> 或 --content <文本>");
                    return 1;
                }

                var artifact = await client.PostAsync<ArtifactDto>(
                    $"/api/tasks/{taskId}/artifacts", new
                    {
                        title,
                        type = artifactType.ToString(),
                        content,
                        author = GetOption(cliArgs, "--actor") ?? "local",
                    });

                Console.WriteLine($"已登记产物 {artifact.Title}（{UiText.ArtifactTypeLabel(artifactType)}）");
                return 0;
            }

            case "plan":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine(
                        "用法: guanfu report plan <任务ID或编号> [--understanding 理解] [--breakdown 拆解] [--background 背景] [--acceptance 验收标准]");
                    return 1;
                }

                var taskId = await CliResolve.TaskIdAsync(client, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                await client.PostAsync<PlanDto>(
                    $"/api/tasks/{taskId}/plan", new
                    {
                        background = GetOption(cliArgs, "--background"),
                        understanding = GetOption(cliArgs, "--understanding"),
                        breakdown = GetOption(cliArgs, "--breakdown"),
                        acceptanceCriteria = GetOption(cliArgs, "--acceptance"),
                        risks = GetOption(cliArgs, "--risks"),
                        author = GetOption(cliArgs, "--actor") ?? "local",
                    });

                Console.WriteLine("已回写需求理解与开发计划");
                return 0;
            }

            case "test":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine(
                        "用法: guanfu report test <任务ID或编号> --total 10 --passed 9 [--failures 失败明细] [--coverage 0.9]");
                    return 1;
                }

                var taskId = await CliResolve.TaskIdAsync(client, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                if (!int.TryParse(GetOption(cliArgs, "--total"), out var total))
                {
                    Console.Error.WriteLine("要写 --total 总数与 --passed 通过数");
                    return 1;
                }

                int.TryParse(GetOption(cliArgs, "--passed"), out var passed);
                double.TryParse(GetOption(cliArgs, "--coverage"), out var coverage);

                var result = await client.PostAsync<TestResultDto>(
                    $"/api/tasks/{taskId}/test-result", new
                    {
                        total,
                        passed,
                        failures = GetOption(cliArgs, "--failures"),
                        coverage = coverage > 0 ? (double?)coverage : null,
                        author = GetOption(cliArgs, "--actor") ?? "local",
                    });

                Console.WriteLine($"已回写测试结果 {result.Passed}/{result.Total}");
                return 0;
            }

            case "doc":
            {
                if (cliArgs.Count < 3)
                {
                    Console.Error.WriteLine("用法: guanfu report doc <任务ID或编号> <文件路径...> [--kind prd|testcase|apidoc|delivery|other]");
                    return 1;
                }

                var taskId = await CliResolve.TaskIdAsync(client, Arg(cliArgs, 1));

                if (taskId is null)
                {
                    Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var kind = GetOption(cliArgs, "--kind");
                var files = cliArgs.Skip(2).Where(a => !a.StartsWith("--")).ToList();
                var uploaded = 0;

                foreach (var file in files)
                {
                    if (!File.Exists(file))
                    {
                        Console.Error.WriteLine($"文件不存在：{file}");
                        continue;
                    }

                    var content = new MultipartFormDataContent();
                    var bytes = await File.ReadAllBytesAsync(file);
                    var part = new ByteArrayContent(bytes);
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
                      guanfu report status   <任务ID或编号> <状态> [--note 说明] [--actor 谁]
                      guanfu report artifact <任务ID或编号> --title <标题> [--type 类型] [--file 路径]
                      guanfu report plan     <任务ID或编号> [--understanding 理解] [--breakdown 拆解]
                      guanfu report test     <任务ID或编号> --total 10 --passed 9 [--coverage 0.9]
                      guanfu report doc      <任务ID或编号> <文件路径...> [--kind 类别]

                    agent 走 MCP 报同样的东西；这是给**本地脚本**用的（CI、部署脚本、钩子）。
                    """);
                return 1;
        }
    }

}
