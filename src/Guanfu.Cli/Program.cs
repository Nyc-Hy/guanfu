using System.Text.Json;
using Guanfu.Application.Contracts;
using static Guanfu.Cli.CliArgs;
using Guanfu.Application.Services;
using Guanfu.Cli;

// guanfu —— 观复的命令行。
// 约定：不直连数据库，全部走 REST API，保持单一数据通路。
//   guanfu serve                              启动 Web（UI + REST + MCP）
//   guanfu project list|add|show
//   guanfu task list|create|show|claim
//   guanfu status set <taskId> <status> [--note ...]
//   guanfu doc upload <taskId> <file...> [--kind prd]
//   guanfu approval list|approve|reject
//   guanfu mcp                                stdio ↔ HTTP 的 MCP 桥接

// 新的环境变量优先；旧的名仍然认（改名不该让脚本失效）
var url = Environment.GetEnvironmentVariable("GUANFU_URL")
         ?? Environment.GetEnvironmentVariable("WORKBENCH_URL")
         ?? "http://127.0.0.1:5177";
var cliArgs = args.ToList();

// 全局参数
var urlIndex = cliArgs.FindIndex(a => a == "--url");
if (urlIndex >= 0 && urlIndex + 1 < cliArgs.Count)
{
    url = cliArgs[urlIndex + 1];
    cliArgs.RemoveRange(urlIndex, 2);
}

if (cliArgs.Count == 0)
{
    PrintUsage();
    return 1;
}

// --help / help：给用法，不算错误（退出码 0）。
// 少了这一步的话 `guanfu --help` 会掉进 Arg() 的「缺少位置参数」——
// 而 AGENTS.md 里就写着用 --help 看用法。
if (cliArgs[0] is "--help" or "-h" or "-?" or "help")
{
    PrintUsage();
    return 0;
}

// --token 人工凭证。敏感命令（裁决审批 / 改配置 / 启动 agent / 搬迁工作区）
// 没有它就是 401。优先级最高，胜过环境变量与 .human-token 文件。
var tokenIndex = cliArgs.FindIndex(a => a == "--token");
if (tokenIndex >= 0 && tokenIndex + 1 < cliArgs.Count)
{
    CliOptions.Token = cliArgs[tokenIndex + 1];
    cliArgs.RemoveRange(tokenIndex, 2);
}

try
{
    // 命令名要在 try 里取：少给命令时是「用法错误」，不是崩溃。
    var command = Arg(cliArgs, 0).ToLowerInvariant();
    cliArgs.RemoveAt(0);

    switch (command)
    {
        case "serve":
            return await ServeAsync(cliArgs);
        case "mcp":
            return await McpBridge.RunAsync(url, cliArgs);
        case "project":
            return await ProjectCommandAsync(url, cliArgs);
        case "task":
            return await TaskCommandAsync(url, cliArgs);
        case "status":
            return await StatusCommandAsync(url, cliArgs);
        case "doc":
            return await DocCommandAsync(url, cliArgs);
        case "approval":
            return await ApprovalCommandAsync(url, cliArgs);
        case "milestone":
            return await MilestoneCommandAsync(url, cliArgs);
        case "metrics":
            return await MetricsCommandAsync(url, cliArgs);
        case "workspace":
            return await WorkspaceCommandAsync(url, cliArgs);
        case "tool":
            return await ToolCommandAsync(url, cliArgs);
        case "review":
            return await ReviewCommandAsync(url, cliArgs);
        case "audit":
            return await AuditCommandAsync(url, cliArgs);
        case "report":
            return await ReportCommand.RunAsync(url, cliArgs);
        case "delivery":
            return await DeliveryCommand.RunAsync(url, cliArgs);
        case "agent-config":
            return AgentConfigCommand.Run(url, cliArgs);
        case "db":
            return await DbCommandAsync(url, cliArgs);
        case "token":
            return TokenCommand();
        default:
            Console.Error.WriteLine($"未知命令: {command}");
            PrintUsage();
            return 1;
    }
}
catch (GuanfuCliException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

static void PrintUsage()
{
    Console.WriteLine("""
        guanfu —— 观复的命令行

        用法:
          guanfu serve                              启动 Web（UI + REST + MCP 端点）
          guanfu mcp                                stdio ↔ HTTP 的 MCP 桥接
          guanfu report                             把本地结果报进观复（状态/产出/计划/测试/文档）
          guanfu delivery                           交付打包与分享链接
          guanfu agent-config [--client http|stdio] 打印接 agent 的配置片段
          guanfu project list                       列出项目
          guanfu project add <名称> [--desc 描述]
          guanfu project show <项目ID>
          guanfu task list [--project <项目ID>] [--status <状态>] [--key 编号] [--type 类型]
                            [--branch 分支] [--milestone 里程碑ID] [--limit 条数] [--offset 偏移]
                            [--archived]
          guanfu task create <项目ID> <标题> [--desc 描述] [--type requirement|bug|optimization]
                                       [--branch 分支]
          guanfu task show <任务ID或编号> [--project <项目ID>]
          guanfu task claim <任务ID> --agent <名称> [--lease 30]
          guanfu status set <任务ID> <状态> [--note 说明]
          guanfu doc upload <任务ID> <文件路径...> [--kind prd|testcase|apidoc|other]
          guanfu approval list
          guanfu approval approve <审批ID> [--by 决策人]
          guanfu approval reject <审批ID> --reason <理由>
          guanfu milestone list <项目ID>
          guanfu milestone create <项目ID> <名称> [--desc 描述]
          guanfu milestone delete <里程碑ID>
          guanfu milestone assign <任务ID或编号> <里程碑ID或名字>   传 0 表示摘出来
          guanfu metrics [<项目ID>]
          guanfu workspace export <目录>
          guanfu workspace import <目录> --home <目标数据目录>
          guanfu tool list
          guanfu tool enable <工具名>
          guanfu tool disable <工具名>
          guanfu tool reset
          guanfu review start <任务ID>                              开一轮评审（7 个维度）
          guanfu review list <任务ID>                               所有轮次与逐维度结论
          guanfu review submit <任务ID> <维度> --pass|--fail [--findings 说明] [--evidence 证据]
          guanfu project config <项目ID> [--repo 路径] [--agent 命令] [--allow 命令1,命令2]
                                 [--webhook 地址] [--format generic|dingtalk|feishu]
          guanfu task launch <任务ID>
          guanfu task assign <任务ID> [--to 负责人] [--priority low|normal|high|critical]
                                    [--due 截止日] [--weight 权重]
          guanfu task deps <任务ID>                      列任务依赖
          guanfu task depend <任务ID> <前置任务ID>        本任务依赖前置任务
          guanfu task undepend <任务ID> <前置任务ID>      移除依赖
          guanfu task events <任务ID> [--format markdown|csv|json]   导出事件流水
          guanfu approval batch <审批ID> [<审批ID>...] [--reject] [--by 决策人]
          guanfu approval revoke <审批ID> [--reason 理由]
          guanfu review export <任务ID> [--round 轮次]     导出评审报告
          guanfu audit list [--tool 工具名] [--project 项目ID] [--task 任务ID] [--limit 条数]
          guanfu db connections <项目ID>                   列数据库连接
          guanfu db rules <连接ID>                         列表级规则（免审批写 / 不许读）
          guanfu db test <连接ID>                          试连
          guanfu token                                  打印人工凭证（远程访问网页时粘贴用）

        全局参数:
          --url <地址>        服务地址，默认 http://127.0.0.1:5177
                              也可用环境变量 GUANFU_URL 指定
          --token <凭证>      人工凭证。敏感命令（裁决审批 / 改项目配置 / 启动 agent /
                              工作区搬迁）没有它会 401。优先级：--token > 环境变量
                              GUANFU_HUMAN_TOKEN > 数据目录/.human-token 文件
        """);
}

/// <summary>
/// 打印人工凭证。
///
/// 远程访问观复时浏览器拿不到本机凭证，得手动粘贴 ——
/// 而凭证只出现在启动横幅里，从一堆启动输出里挑那串东西很费劲。
/// </summary>
static int TokenCommand()
{
    var token = GuanfuClient.ResolveToken(
        CliOptions.Token,
        Environment.GetEnvironmentVariable("GUANFU_HUMAN_TOKEN")
        ?? Environment.GetEnvironmentVariable("WORKBENCH_HUMAN_TOKEN"),
        GuanfuClient.ResolveHomeDirectory());

    if (string.IsNullOrWhiteSpace(token))
    {
        var home = GuanfuClient.ResolveHomeDirectory() ?? "~/.workbench";
        Console.Error.WriteLine($"没找到人工凭证。先启动观复（guanfu serve），凭证会写在 {Path.Combine(home, ".human-token")}。");
        return 1;
    }

    Console.WriteLine(token);
    return 0;
}

static async Task<int> ServeAsync(List<string> cliArgs)
{
    // **发布出来的包里**：同目录有 `guanfu-server`（自包含的服务端二进制）——
    // 直接跑它，不需要源码，也不需要装 .NET。
    var sibling = ResolveSiblingServer();
    if (sibling is not null)
    {
        return await RunServerAsync(sibling, cliArgs);
    }

    // **源码树里**：退回到 `dotnet run --project`。
    var projectPath = ResolveWebProject();
    if (projectPath is null)
    {
        Console.Error.WriteLine(
            "找不到观复服务端（guanfu-server），也找不到 Guanfu.Web 工程。" +
            "发布包里应该有 guanfu-server；源码树里请用 --project <路径> 指定，"+
            "或设置环境变量 WORKBENCH_WEB_PROJECT。");
        return 4;
    }

    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        UseShellExecute = false
    };

    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(projectPath);

    foreach (var arg in cliArgs)
    {
        startInfo.ArgumentList.Add(arg);
    }

    Console.WriteLine($"启动观复: dotnet run --project {projectPath}");

    using var process = System.Diagnostics.Process.Start(startInfo)
                        ?? throw new GuanfuCliException("无法启动 dotnet 进程。");

    await process.WaitForExitAsync();
    return process.ExitCode;
}

/// <summary>
/// 找同目录的 `guanfu-server`（发布包里的服务端二进制）。
///
/// 发布出来的观复是「两个二进制 + 一份说明」：服务端自包含，
/// CLI 负责 stdio 桥接与命令行 —— 桥接是只支持 stdio 的 MCP 客户端唯一的入口。
/// </summary>
static string? ResolveSiblingServer()
{
    try
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(self);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var name = OperatingSystem.IsWindows() ? "guanfu-server.exe" : "guanfu-server";

        // 发布包的结构：bin/guanfu-cli/（命令行）与 bin/guanfu-server/（服务端）是兄弟，
        // 所以同目录找不到时要往上一级看一眼。
        foreach (var baseDir in new[] { dir, Path.GetDirectoryName(dir) })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            var candidate = Path.Combine(baseDir, name);

            // 服务端是一个可执行文件
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // 或者一个**目录**（自包含发布：一堆 dll + wwwroot）
            var inside = Path.Combine(candidate, name);
            if (File.Exists(inside))
            {
                return inside;
            }
        }

        return null;
    }
    catch (Exception)
    {
        return null;
    }
}

/// <summary>跑同目录的服务端二进制。</summary>
static async Task<int> RunServerAsync(string serverPath, List<string> cliArgs)
{
    Console.WriteLine($"启动观复: {serverPath}");

    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = serverPath,
        UseShellExecute = false
    };

    foreach (var arg in cliArgs)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using var process = System.Diagnostics.Process.Start(startInfo)
                        ?? throw new GuanfuCliException("无法启动观复服务端。");

    await process.WaitForExitAsync();
    return process.ExitCode;
}

static string? ResolveWebProject()
{
    var fromEnv = Environment.GetEnvironmentVariable("WORKBENCH_WEB_PROJECT");
    if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
    {
        return fromEnv;
    }

    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "src", "Guanfu.Web", "Guanfu.Web.csproj");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir);
    }

    return null;
}

static async Task<int> ProjectCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var projects = await client.GetAsync<List<ProjectSummaryDto>>("/api/projects");
            if (projects.Count == 0)
            {
                Console.WriteLine("还没有项目。");
                return 0;
            }

            Console.WriteLine($"{"ID",-38} {"进度",-8} {"任务",-10} 名称");
            foreach (var p in projects)
            {
                Console.WriteLine($"{p.Id,-38} {p.Progress * 100,7:0.#}% {p.DoneTasks}/{p.TotalTasks,-8} {p.Name}");
            }

            return 0;
        }

        case "add":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu project add <名称> [--desc 描述]");
                return 1;
            }

            var name = Arg(cliArgs, 1);
            var desc = GetOption(cliArgs, "--desc");
            var created = await client.PostAsync<ProjectSummaryDto>(
                "/api/projects", new { name, description = desc });
            Console.WriteLine($"已创建项目 {created.Id} · {created.Name}");
            return 0;
        }

        case "show":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu project show <项目ID>");
                return 1;
            }

            var project = await client.GetAsync<ProjectSummaryDto>($"/api/projects/{Arg(cliArgs, 1)}");
            Console.WriteLine(JsonSerializer.Serialize(project, CliJson.Pretty));
            return 0;
        }

        case "config":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine(
                    "用法: guanfu project config <项目ID> [--repo 路径] [--agent 命令] [--allow 命令1,命令2] [--webhook 地址] [--format 格式]");
                return 1;
            }

            var projectId = Arg(cliArgs, 1);
            var repo = GetOption(cliArgs, "--repo");
            var agent = GetOption(cliArgs, "--agent");
            var allow = GetOption(cliArgs, "--allow");
            var webhook = GetOption(cliArgs, "--webhook");
            var format = GetOption(cliArgs, "--format");

            if (repo is null && agent is null && allow is null && webhook is null && format is null)
            {
                var current = await client.GetAsync<ProjectConfigDto>($"/api/projects/{projectId}/config");
                Console.WriteLine(JsonSerializer.Serialize(
                    current, CliJson.Pretty));
                return 0;
            }

            var allowed = allow is null
                ? null
                : allow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var updated = await client.PutAsync<ProjectConfigDto>(
                $"/api/projects/{projectId}/config",
                new
                {
                    name = (string?)null,
                    description = (string?)null,
                    repoPath = repo,
                    repoRemote = (string?)null,
                    defaultBranch = (string?)null,
                    allowedCommands = allowed,
                    agentCommand = agent,
                    notificationWebhookUrl = webhook,
                    notificationFormat = format
                });

            Console.WriteLine($"已更新项目配置：{updated.Name}");
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: guanfu project {sub}");
            return 1;
    }
}

static async Task<int> TaskCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var query = new List<string>();
            var project = GetOption(cliArgs, "--project");
            var status = GetOption(cliArgs, "--status");
            var key = GetOption(cliArgs, "--key");
            var type = GetOption(cliArgs, "--type");
            var branch = GetOption(cliArgs, "--branch");
            var milestone = GetOption(cliArgs, "--milestone");
            var limit = GetOption(cliArgs, "--limit");
            var offset = GetOption(cliArgs, "--offset");
            var includeArchived = cliArgs.Contains("--archived");
            if (project is not null) query.Add(GuanfuClient.Query("projectId", project));
            if (status is not null) query.Add(GuanfuClient.Query("status", status));
            if (key is not null) query.Add(GuanfuClient.Query("key", key));
            if (type is not null) query.Add(GuanfuClient.Query("type", type));
            if (branch is not null) query.Add(GuanfuClient.Query("branch", branch));
            if (milestone is not null) query.Add(GuanfuClient.Query("milestoneId", milestone));
            if (limit is not null) query.Add(GuanfuClient.Query("limit", limit));
            if (offset is not null) query.Add(GuanfuClient.Query("offset", offset));
            if (includeArchived) query.Add(GuanfuClient.Query("includeArchived", "true"));

            var path = "/api/tasks" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
            var tasks = await client.GetAsync<List<TaskSummaryDto>>(path);
            if (tasks.Count == 0)
            {
                Console.WriteLine("没有任务。");
                return 0;
            }

            Console.WriteLine($"{"编号",-12} {"类型",-6} {"ID",-38} {"状态",-12} 标题");
            foreach (var t in tasks)
            {
                Console.WriteLine(
                    $"{Truncate(t.Key ?? "-", 12),-12} {TaskTypeLabel(t.Type),-6} {t.Id,-38} {UiText.Status(t.Status),-12} " +
                    $"{t.Title}");
            }

            return 0;
        }

        case "create":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: guanfu task create <项目ID> <标题> [--desc 描述]");
                return 1;
            }

            var created = await client.PostAsync<TaskSummaryDto>(
                $"/api/projects/{Arg(cliArgs, 1)}/tasks",
                new
                {
                    title = Arg(cliArgs, 2),
                    description = GetOption(cliArgs, "--desc"),
                    type = GetOption(cliArgs, "--type")
                });
            Console.WriteLine($"已创建任务 {created.Id} · {created.Key} · {created.Title}");
            return 0;
        }

        case "show":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu task show <任务ID或编号>");
                return 1;
            }

            // 传编号（REQ-001）也能找到 —— 人说话是「看下 req001」
            if (Guid.TryParse(Arg(cliArgs, 1), out var taskId))
            {
                var byId = await client.GetAsync<TaskSummaryDto>($"/api/tasks/{taskId}");
                Console.WriteLine(JsonSerializer.Serialize(byId, CliJson.Pretty));
                return 0;
            }

            var projectId = GetOption(cliArgs, "--project");
            if (string.IsNullOrWhiteSpace(projectId))
            {
                Console.Error.WriteLine("按编号查需要 --project <项目ID>。");
                return 1;
            }

            var byKey = await client.GetAsync<List<TaskSummaryDto>>(
                $"/api/tasks?projectId={Uri.EscapeDataString(projectId)}&key={Uri.EscapeDataString(Arg(cliArgs, 1))}");
            if (byKey.Count == 0)
            {
                Console.Error.WriteLine($"项目 {projectId} 里没有编号 {Arg(cliArgs, 1)} 的任务。");
                return 1;
            }

            Console.WriteLine(JsonSerializer.Serialize(byKey[0], CliJson.Pretty));
            return 0;
        }

        case "plan":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine(
                    "用法: guanfu task plan <任务ID> [--background 背景] [--understanding 理解] [--breakdown 拆解]" +
                    " [--acceptance 验收标准] [--risks 风险] [--author 作者] [--occurred-at 实际发生时间]");
                return 1;
            }

            var plan = await client.PostRawAsync($"/api/tasks/{Arg(cliArgs, 1)}/plan", new
            {
                background = GetOption(cliArgs, "--background"),
                understanding = GetOption(cliArgs, "--understanding"),
                breakdown = GetOption(cliArgs, "--breakdown"),
                acceptanceCriteria = GetOption(cliArgs, "--acceptance"),
                risks = GetOption(cliArgs, "--risks"),
                author = GetOption(cliArgs, "--author"),
                occurredAt = ParseOccurredAt(GetOption(cliArgs, "--occurred-at"))
            });

            Console.WriteLine(plan);
            return 0;
        }

        case "context":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu task context <任务ID>");
                return 1;
            }

            Console.WriteLine(await client.GetRawAsync($"/api/tasks/{Arg(cliArgs, 1)}/context"));
            return 0;
        }

        case "artifact":
        {
            if (cliArgs.Count < 4)
            {
                Console.Error.WriteLine(
                    "用法: guanfu task artifact <任务ID> <类型> <标题> [--content 内容] [--path 外部路径]" +
                    " [--sha 提交号] [--branch 分支] [--author 作者] [--occurred-at 实际发生时间]");
                Console.Error.WriteLine(
                    "类型: Plan / DesignDoc / CodeChange / TestReport / DeployRecord / Screenshot / Commit / Document / Other");
                return 1;
            }

            var artifact = await client.PostRawAsync($"/api/tasks/{Arg(cliArgs, 1)}/artifacts", new
            {
                title = Arg(cliArgs, 3),
                type = Arg(cliArgs, 2),
                content = GetOption(cliArgs, "--content"),
                externalPath = GetOption(cliArgs, "--path"),
                commitSha = GetOption(cliArgs, "--sha"),
                branch = GetOption(cliArgs, "--branch"),
                author = GetOption(cliArgs, "--author"),
                occurredAt = ParseOccurredAt(GetOption(cliArgs, "--occurred-at"))
            });

            Console.WriteLine(artifact);
            return 0;
        }

        case "test-result":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine(
                    "用法: guanfu task test-result <任务ID> --total 10 --passed 9 [--failures \"用例A,用例B\"]" +
                    " [--coverage 0.9] [--author 作者] [--occurred-at 实际发生时间]");
                return 1;
            }

            var total = ParseIntArg(GetOption(cliArgs, "--total") ?? "0", "用例总数", 0);
            var passed = ParseIntArg(GetOption(cliArgs, "--passed") ?? "0", "通过数", 0);
            var coverage = GetOption(cliArgs, "--coverage");

            var result = await client.PostRawAsync($"/api/tasks/{Arg(cliArgs, 1)}/test-result", new
            {
                total,
                passed,
                failures = GetOption(cliArgs, "--failures"),
                coverage = coverage is null ? (double?)null : ParseDoubleArg(coverage, "覆盖率"),
                author = GetOption(cliArgs, "--author"),
                occurredAt = ParseOccurredAt(GetOption(cliArgs, "--occurred-at"))
            });

            Console.WriteLine(result);
            return 0;
        }

        case "claim":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu task claim <任务ID> --agent <名称> [--lease 30] [--worktree <目录>]");
                return 1;
            }

            var agent = GetOption(cliArgs, "--agent") ?? "cli";
            var lease = ParseIntArg(GetOption(cliArgs, "--lease") ?? "30", "租约分钟数", 30);

            // --worktree：自己在哪个工作目录干活。不给 = 项目主工作区（同一时刻只允许一个 agent）。
            var worktree = GetOption(cliArgs, "--worktree");
            var task = await client.PostAsync<TaskSummaryDto>(
                $"/api/tasks/{Arg(cliArgs, 1)}/claim",
                new { agentId = agent, leaseMinutes = lease, workPath = worktree });
            Console.WriteLine($"已认领 {task.Id}，租约至 {task.LeaseExpiresAt}");
            return 0;
        }

        case "launch":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu task launch <任务ID>");
                return 1;
            }

            var result = await client.PostAsync<AgentLaunchResult>($"/api/tasks/{Arg(cliArgs, 1)}/launch-agent", new { });
            Console.WriteLine($"已拉起外部 agent（PID {result.ProcessId}）：{result.Command}");
            return 0;
        }

        case "assign":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine(
                    "用法: guanfu task assign <任务ID> [--to 负责人] [--priority low|normal|high|critical] [--due 截止日] [--weight 权重]");
                return 1;
            }

            var assigned = await client.PostAsync<TaskSummaryDto>(
                $"/api/tasks/{Arg(cliArgs, 1)}/assign",
                new
                {
                    assignee = GetOption(cliArgs, "--to"),
                    priority = GetOption(cliArgs, "--priority"),
                    dueAt = GetOption(cliArgs, "--due"),
                    weight = GetOption(cliArgs, "--weight") is { } w ? ParseDoubleArg(w, "权重") : (double?)null
                });

            Console.WriteLine(
                $"{assigned.Key} 分派：负责人 {assigned.Assignee ?? "-"} · 优先级 {assigned.Priority} · " +
                $"截止 {assigned.DueAt?.ToString("u") ?? "-"} · 权重 {assigned.Weight}");
            return 0;
        }

        case "deps":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu task deps <任务ID>");
                return 1;
            }

            var deps = await client.GetAsync<List<TaskDependencyDto>>($"/api/tasks/{Arg(cliArgs, 1)}/dependencies");
            if (deps.Count == 0)
            {
                Console.WriteLine("没有依赖。");
                return 0;
            }

            Console.WriteLine($"{"方向",-6} {"前置",-14} {"标题",-30} 完成");
            foreach (var d in deps)
            {
                Console.WriteLine(
                    $"{d.Type,-6} {Truncate(d.DependsOnKey ?? "-", 14),-14} " +
                    $"{Truncate(d.DependsOnTitle, 30),-30} {(d.DependsOnDone ? "是" : "否")}");
            }

            return 0;
        }

        case "depend":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: guanfu task depend <任务ID> <前置任务ID> [--type blockedby|blocks]");
                return 1;
            }

            var created = await client.PostAsync<TaskDependencyDto>(
                $"/api/tasks/{Arg(cliArgs, 1)}/dependencies",
                new
                {
                    dependsOnTaskId = Arg(cliArgs, 2),
                    type = GetOption(cliArgs, "--type"),
                    note = GetOption(cliArgs, "--note")
                });

            Console.WriteLine($"已加依赖：{created.TaskTitle} 依赖 {created.DependsOnTitle}");
            return 0;
        }

        case "undepend":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: guanfu task undepend <任务ID> <前置任务ID>");
                return 1;
            }

            await client.DeleteAsync($"/api/tasks/{Arg(cliArgs, 1)}/dependencies/{Arg(cliArgs, 2)}");
            Console.WriteLine("依赖已移除。");
            return 0;
        }

        case "events":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu task events <任务ID> [--limit 50] [--format markdown|csv|json]");
                return 1;
            }

            var limit = ParseIntArg(GetOption(cliArgs, "--limit") ?? "50", "条数", 50);
            var format = GetOption(cliArgs, "--format") ?? "markdown";

            var text = await client.GetRawAsync($"/api/tasks/{Arg(cliArgs, 1)}/export/events?format={format}");
            Console.WriteLine(text);
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: guanfu task {sub}");
            return 1;
    }
}

static async Task<int> StatusCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);

    if (cliArgs.Count < 3 || !string.Equals(Arg(cliArgs, 0), "set", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("用法: guanfu status set <任务ID> <状态> [--note 说明]");
        return 1;
    }

    var task = await client.PostAsync<TaskSummaryDto>(
        $"/api/tasks/{Arg(cliArgs, 1)}/status",
        new { status = Arg(cliArgs, 2), note = GetOption(cliArgs, "--note"), actor = "cli", source = "Human" });

    Console.WriteLine($"状态已更新为 {UiText.Status(task.Status)}");
    return 0;
}

static async Task<int> DocCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);

    if (cliArgs.Count < 3 || !string.Equals(Arg(cliArgs, 0), "upload", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("用法: guanfu doc upload <任务ID> <文件路径...> [--kind prd|testcase|apidoc|other]");
        return 1;
    }

    var taskId = Arg(cliArgs, 1);
    var kind = GetOption(cliArgs, "--kind");

    // 位置参数：第 2 个起都是文件（可以给多个）
    var positionals = Positionals(cliArgs);
    var paths = positionals.Skip(2).Select(Path.GetFullPath).ToList();

    var missing = paths.Where(p => !File.Exists(p)).ToList();
    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"文件不存在: {string.Join("、", missing)}");
        return 1;
    }

    using var content = new MultipartFormDataContent();

    foreach (var path in paths)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(path));
    }

    if (!string.IsNullOrWhiteSpace(kind))
    {
        content.Add(new StringContent(kind), "kind");
    }

    var response = await client.PostMultipartAsync($"/api/tasks/{taskId}/documents/batch", content);

    // 回执要能一眼看懂：传上去哪些、哪个没传上去、为什么
    using var payload = JsonDocument.Parse(response);
    var uploaded = payload.RootElement.GetProperty("uploaded").EnumerateArray().ToList();
    var failed = payload.RootElement.GetProperty("failed").EnumerateArray().ToList();

    Console.WriteLine($"已上传 {uploaded.Count} 个文件：");
    foreach (var doc in uploaded)
    {
        var name = doc.GetProperty("fileName").GetString();
        var docKind = doc.GetProperty("kind").GetString();
        var size = doc.GetProperty("sizeBytes").GetInt64() / 1024;
        Console.WriteLine($"  · {name}（{docKind}，{size} KB）");
    }

    if (failed.Count > 0)
    {
        Console.WriteLine($"{failed.Count} 个没传上去：");
        foreach (var item in failed)
        {
            Console.WriteLine($"  · {item.GetProperty("fileName").GetString()} —— {item.GetProperty("error").GetString()}");
        }
    }

    return failed.Count > 0 ? 1 : 0;
}

static async Task<int> ApprovalCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var approvals = await client.GetAsync<List<ApprovalDto>>("/api/approvals?status=Pending");
            if (approvals.Count == 0)
            {
                Console.WriteLine("没有待处理的审批。");
                return 0;
            }

            foreach (var a in approvals)
            {
                Console.WriteLine($"{a.Id}  {UiText.Tool(a.ToolName)}  {a.Summary}");
            }

            return 0;
        }

        case "approve":
        case "reject":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine($"用法: guanfu approval {sub} <审批ID> [--by 决策人] [--reason 理由]");
                return 1;
            }

            var reason = GetOption(cliArgs, "--reason");
            if (sub == "reject" && string.IsNullOrWhiteSpace(reason))
            {
                Console.Error.WriteLine("驳回必须提供 --reason。");
                return 1;
            }

            await client.PostAsync<ApprovalDto>(
                $"/api/approvals/{Arg(cliArgs, 1)}/decide",
                new { approve = sub == "approve", decidedBy = GetOption(cliArgs, "--by") ?? "cli", reason });

            Console.WriteLine(sub == "approve" ? "已批准。" : "已驳回。");
            return 0;
        }

        case "revoke":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu approval revoke <审批ID> [--reason 理由]");
                return 1;
            }

            var revoked = await client.PostAsync<ApprovalDto>(
                $"/api/approvals/{Arg(cliArgs, 1)}/revoke",
                new { reason = GetOption(cliArgs, "--reason") });

            Console.WriteLine($"已撤销审批 {revoked.Id}（{UiText.ApprovalStatus(revoked.Status)}）。");
            return 0;
        }

        case "batch":
        {
            // 批量裁决：一条一条点太慢了，人工审核通常是「这批都同意」。
            // 逐条回结果 —— 部分失败不能吞掉。
            var ids = cliArgs
                .Skip(1)
                .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                .Where(a => Guid.TryParse(a, out _))
                .ToList();

            if (ids.Count == 0)
            {
                Console.Error.WriteLine("用法: guanfu approval batch <审批ID> [<审批ID>...] [--reject] [--by 决策人]");
                return 1;
            }

            var approve = !cliArgs.Contains("--reject");
            var result = await client.PostAsync<BatchDecisionResponse>(
                "/api/approvals/batch-decide",
                new
                {
                    approvalIds = ids,
                    approve,
                    decidedBy = GetOption(cliArgs, "--by") ?? "cli",
                    reason = GetOption(cliArgs, "--reason")
                });

            foreach (var item in result.Results)
            {
                Console.WriteLine(item.Ok
                    ? $"已{(approve ? "批准" : "驳回")} {item.Id}"
                    : $"失败 {item.Id}：{item.Error}");
            }

            Console.WriteLine($"共 {result.Total} 条，失败 {result.Failed} 条。");
            return result.Failed > 0 ? 1 : 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: guanfu approval {sub}");
            return 1;
    }
}

static async Task<int> MilestoneCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu milestone list <项目ID>");
                return 1;
            }

            var milestones = await client.GetAsync<List<MilestoneDto>>($"/api/projects/{Arg(cliArgs, 1)}/milestones");
            if (milestones.Count == 0)
            {
                Console.WriteLine("该项目还没有里程碑。");
                return 0;
            }

            foreach (var m in milestones)
            {
                Console.WriteLine($"{m.Id}  {m.Name,-24} {m.DoneTasks}/{m.TotalTasks}  {m.Progress * 100:0.#}%");
            }

            return 0;
        }

        case "create":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: guanfu milestone create <项目ID> <名称> [--desc 描述]");
                return 1;
            }

            var created = await client.PostAsync<MilestoneDto>(
                $"/api/projects/{Arg(cliArgs, 1)}/milestones",
                new { name = Arg(cliArgs, 2), description = GetOption(cliArgs, "--desc") });

            Console.WriteLine($"已创建里程碑 {created.Id} · {created.Name}");
            return 0;
        }

        case "delete":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu milestone delete <里程碑ID>");
                return 1;
            }

            await client.DeleteAsync($"/api/milestones/{Arg(cliArgs, 1)}");
            Console.WriteLine("已删除里程碑（任务保留，只是不再归属任何里程碑）。");
            return 0;
        }

        case "assign":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: guanfu milestone assign <任务ID或编号> <里程碑ID或名字>（传 0 表示摘出来）");
                return 1;
            }

            // 人说「REQ-001」「12 月版本」—— 编号与名字都要认。
            var taskId = await CliResolve.TaskIdAsync(client, Arg(cliArgs, 1));

            if (taskId is null)
            {
                Console.Error.WriteLine($"找不到任务：{Arg(cliArgs, 1)}");
                return 1;
            }

            Guid? milestoneId = Arg(cliArgs, 2) == "0"
                ? null
                : await CliResolve.MilestoneIdAsync(client, Arg(cliArgs, 2));

            if (Arg(cliArgs, 2) != "0" && milestoneId is null)
            {
                Console.Error.WriteLine($"找不到迭代版本：{Arg(cliArgs, 2)}");
                return 1;
            }
            var task = await client.PostAsync<TaskSummaryDto>(
                $"/api/tasks/{taskId}/milestone", new { milestoneId });
            Console.WriteLine($"已更新任务 {task.Id} 的里程碑。");
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: guanfu milestone {sub}");
            return 1;
    }
}

static async Task<int> MetricsCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);

    if (cliArgs.Count >= 2)
    {
        var one = await client.GetAsync<ProjectMetricsDto>($"/api/projects/{Arg(cliArgs, 1)}/metrics");
        PrintMetrics(one);
        return 0;
    }

    var all = await client.GetAsync<List<ProjectMetricsDto>>("/api/metrics");
    if (all.Count == 0)
    {
        Console.WriteLine("还没有项目数据。");
        return 0;
    }

    foreach (var m in all)
    {
        PrintMetrics(m);
        Console.WriteLine();
    }

    return 0;
}

static void PrintMetrics(ProjectMetricsDto m)
{
    Console.WriteLine($"{m.Name}");
    Console.WriteLine($"  进度 {m.Progress * 100:0.#}%  完成 {m.DoneTasks}/{m.TotalTasks}  阻塞 {m.BlockedTasks}  停滞 {m.StaleTasks}  待审 {m.OpenApprovals}");
    Console.WriteLine($"  平均周期 {(m.AvgCycleHours is null ? "—" : $"{m.AvgCycleHours:0.#} 小时")}  测试通过率 {(m.TestPassRate is null ? "—" : $"{m.TestPassRate * 100:0.#}%")}  覆盖率 {(m.AvgTestCoverage is null ? "—" : $"{m.AvgTestCoverage * 100:0.#}%")}");
    Console.WriteLine($"  状态: {string.Join(" ", m.StatusCounts.Select(s => $"{UiText.Status(s.Status)}={s.Count}"))}");
    Console.WriteLine($"  流速: {string.Join(" ", m.Throughput.Select(t => $"{t.Week[5..]}={t.Completed}"))}");
}

static async Task<int> WorkspaceCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "";

    switch (sub)
    {
        case "export":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu workspace export <目录>");
                return 1;
            }

            var result = await client.PostAsync<WorkspaceArchiveResult>(
                "/api/workspace/export", new { targetDirectory = Path.GetFullPath(Arg(cliArgs, 1)) });
            Console.WriteLine(result.Message);
            return 0;
        }

        case "import":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu workspace import <目录> --home <目标数据目录>");
                return 1;
            }

            var home = GetOption(cliArgs, "--home");
            if (string.IsNullOrWhiteSpace(home))
            {
                Console.Error.WriteLine("必须用 --home 指定目标 数据目录。");
                return 1;
            }

            // 导入只是搬文件，不碰数据库 —— 所以本地做，不必等服务在跑。
            // （换机场景恰恰是服务还没起的时候。）
            var target = Path.GetFullPath(home);
            var (fileCount, totalBytes) = WorkspaceArchiveFiles.Import(Path.GetFullPath(Arg(cliArgs, 1)), target);

            Console.WriteLine($"已恢复到 {target}（{fileCount} 个文件，{totalBytes} 字节）。");
            Console.WriteLine("把 数据目录 指到这个目录再启动服务即可。");
            return 0;
        }

        default:
            Console.Error.WriteLine("用法: guanfu workspace export <目录> | guanfu workspace import <目录> --home <目录>");
            return 1;
    }
}

static async Task<int> ReviewCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "start":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu review start <任务ID>");
                return 1;
            }

            var round = await client.PostAsync<ReviewRoundDto>(
                $"/api/tasks/{Arg(cliArgs, 1)}/review", new { reviewer = "cli" });

            Console.WriteLine($"已开始第 {round.Round} 轮评审，共 {round.Items.Count} 个维度。");
            foreach (var item in round.Items)
            {
                Console.WriteLine($"  - {item.Dimension,-24}{item.DimensionName}");
            }

            return 0;
        }

        case "list":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu review list <任务ID>");
                return 1;
            }

            var rounds = await client.GetAsync<List<ReviewRoundDto>>($"/api/tasks/{Arg(cliArgs, 1)}/reviews");
            if (rounds.Count == 0)
            {
                Console.WriteLine("还没有评审记录。");
                return 0;
            }

            foreach (var round in rounds)
            {
                var verdict = round.Verdict == "Passed" ? "通过" : round.Verdict == "Failed" ? "不通过" : "评审中";
                Console.WriteLine($"第 {round.Round} 轮 · {verdict}");

                foreach (var item in round.Items)
                {
                    var mark = item.Status == "Passed" ? "✓" : item.Status == "Failed" ? "×" : "·";
                    var findings = string.IsNullOrWhiteSpace(item.Findings) ? "" : $"  {item.Findings}";
                    Console.WriteLine($"  {mark} {item.DimensionName,-24}{findings}");
                }

                Console.WriteLine();
            }

            return 0;
        }

        case "submit":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: guanfu review submit <任务ID> <维度> --pass|--fail [--findings 说明] [--evidence 证据]");
                return 1;
            }

            var passed = cliArgs.Contains("--pass");
            var failed = cliArgs.Contains("--fail");

            if (passed == failed)
            {
                Console.Error.WriteLine("必须指定 --pass 或 --fail 其中之一。");
                return 1;
            }

            var item = await client.PostAsync<ReviewItemDto>(
                $"/api/tasks/{Arg(cliArgs, 1)}/review/{Arg(cliArgs, 2)}",
                new
                {
                    passed,
                    findings = GetOption(cliArgs, "--findings"),
                    evidence = GetOption(cliArgs, "--evidence"),
                    recommendation = GetOption(cliArgs, "--suggestion"),
                    reviewer = "cli"
                });

            Console.WriteLine($"[{item.DimensionName}] 已回写：{(item.Status == "Passed" ? "通过" : "不通过")}");
            return 0;
        }

        case "export":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu review export <任务ID> [--round 轮次] [--format markdown|json]");
                return 1;
            }

            var round = GetOption(cliArgs, "--round");
            var format = GetOption(cliArgs, "--format") ?? "markdown";

            var query = new List<string> { GuanfuClient.Query("format", format) };
            if (round is not null)
            {
                query.Add(GuanfuClient.Query("round", round));
            }

            var text = await client.GetRawAsync(
                $"/api/tasks/{Arg(cliArgs, 1)}/export/review?{string.Join('&', query)}");

            Console.WriteLine(text);
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: guanfu review {sub}");
            return 1;
    }
}

static async Task<int> ToolCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var tools = await client.GetAsync<List<McpToolInfo>>("/api/mcp/tools");
            var byCategory = tools.GroupBy(t => t.Category);

            foreach (var group in byCategory)
            {
                Console.WriteLine($"── {group.Key} ──");
                foreach (var t in group)
                {
                    var mark = t.Enabled ? "✓" : "×";
                    var approval = t.RequiresApproval ? "  [需审批]" : string.Empty;
                    Console.WriteLine($"  {mark} {t.Name,-34}{approval}");
                }
            }

            var off = tools.Count(t => !t.Enabled);
            Console.WriteLine($"\n{tools.Count - off}/{tools.Count} 开启");
            return 0;
        }

        case "enable":
        case "disable":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine($"用法: guanfu tool {sub} <工具名>");
                return 1;
            }

            var updated = await client.PutAsync<McpToolInfo>(
                $"/api/mcp/tools/{Arg(cliArgs, 1)}", new { enabled = sub == "enable" });

            Console.WriteLine(sub == "enable"
                ? $"已开启 {updated.Name}。"
                : $"已关闭 {updated.Name}。agent 的 tools/list 里不再有它。");
            return 0;
        }

        case "reset":
        {
            var tools = await client.PostAsync<List<McpToolInfo>>("/api/mcp/tools/reset", new { });
            Console.WriteLine($"已恢复默认（{tools.Count} 个工具全部开启）。");
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: guanfu tool {sub}");
            return 1;
    }
}


/// <summary>解析整数，失败时给一句人话。</summary>
static int ParseIntArg(string value, string what, int fallback)
{
    if (!int.TryParse(value, out var parsed))
    {
        throw new GuanfuCliException($"{what} 不是合法的整数: {value}");
    }

    return parsed;
}

static double ParseDoubleArg(string value, string what)
{
    if (!double.TryParse(value, out var parsed))
    {
        throw new GuanfuCliException($"{what} 不是合法的数字: {value}");
    }

    return parsed;
}

/// <summary>
/// 解析「实际发生时间」（补报用）。没给就 null —— 由观复按「现在」算。
/// 认 ISO 8601（2026-11-17T06:30:00Z）与本地写法（2026-11-17 14:30）。
/// </summary>
static DateTimeOffset? ParseOccurredAt(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (DateTimeOffset.TryParse(value, out var parsed))
    {
        return parsed;
    }

    throw new GuanfuCliException($"实际发生时间不是合法的时间: {value}（用 2026-11-17T06:30:00Z 或 2026-11-17 14:30）");
}

/// <summary>工具审计查询。agent 干了什么，这里能查。</summary>
static async Task<int> AuditCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var query = new List<string>();
            var tool = GetOption(cliArgs, "--tool");
            var project = GetOption(cliArgs, "--project");
            var task = GetOption(cliArgs, "--task");
            var limit = GetOption(cliArgs, "--limit");
            if (tool is not null) query.Add(GuanfuClient.Query("toolName", tool));
            if (project is not null) query.Add(GuanfuClient.Query("projectId", project));
            if (task is not null) query.Add(GuanfuClient.Query("taskId", task));
            if (limit is not null) query.Add(GuanfuClient.Query("limit", limit));

            var path = "/api/audit" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
            Console.WriteLine(await client.GetRawAsync(path));
            return 0;
        }

        default:
            Console.Error.WriteLine("用法: guanfu audit list [--tool 工具名] [--project 项目ID] [--task 任务ID] [--limit 条数]");
            return 1;
    }
}

/// <summary>数据库连接与表级规则。</summary>
static async Task<int> DbCommandAsync(string url, List<string> cliArgs)
{
    var client = new GuanfuClient(url, CliOptions.Token);
    var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "connections";

    switch (sub)
    {
        case "connections":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu db connections <项目ID>");
                return 1;
            }

            Console.WriteLine(await client.GetRawAsync($"/api/projects/{Arg(cliArgs, 1)}/db-connections"));
            return 0;
        }

        case "rules":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu db rules <连接ID>");
                return 1;
            }

            Console.WriteLine(await client.GetRawAsync($"/api/db-connections/{Arg(cliArgs, 1)}/rules"));
            return 0;
        }

        case "test":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: guanfu db test <连接ID>");
                return 1;
            }

            Console.WriteLine(await client.PostRawAsync($"/api/db-connections/{Arg(cliArgs, 1)}/test", new { }));
            return 0;
        }

        default:
            Console.Error.WriteLine("用法: guanfu db connections|rules|test ...");
            return 1;
    }
}

/// <summary>
/// 第 n 个位置参数（0 起）。
///
/// 选项和它的值不占位置 —— 以前直接用 <c>cliArgs[1]</c>，
/// 选项写在位置参数前面就会读错（`guanfu task show --project X REQ-001` 读成 "--project"）。
/// 选项值统一写成 <c>--name value</c>；布尔开关放最后，或用 <c>--name=value</c>。
/// </summary>
static string Truncate(string value, int length) =>
    value.Length <= length ? value : value[..(length - 1)] + "…";

/// <summary>类型的中文简称，列表里一眼能看出是需求/缺陷/优化。</summary>
static string TaskTypeLabel(Guanfu.Domain.TaskType type) => type switch
{
    Guanfu.Domain.TaskType.Bug => "缺陷",
    Guanfu.Domain.TaskType.Optimization => "优化",
    _ => "需求"
};

/// <summary>批量裁决的返回。</summary>
public sealed record BatchDecisionResponse(int Total, int Failed, IReadOnlyList<BatchDecisionItem> Results);

public sealed record BatchDecisionItem(Guid Id, bool Ok, string? Status, string? Error);
