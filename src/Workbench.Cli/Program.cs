using System.Text.Json;
using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Cli;

// wb —— 工作台命令行。
// 约定：不直连数据库，全部走 REST API，保持单一数据通路。
//   wb serve                              启动 Web（UI + REST + MCP）
//   wb project list|add|show
//   wb task list|create|show|claim
//   wb status set <taskId> <status> [--note ...]
//   wb doc upload <taskId> <file> [--kind prd]
//   wb approval list|approve|reject
//   wb mcp                                stdio ↔ HTTP 的 MCP 桥接

var url = Environment.GetEnvironmentVariable("WORKBENCH_URL") ?? "http://127.0.0.1:5177";
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

var command = cliArgs[0].ToLowerInvariant();
cliArgs.RemoveAt(0);

try
{
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
        default:
            Console.Error.WriteLine($"未知命令: {command}");
            PrintUsage();
            return 1;
    }
}
catch (WorkbenchCliException ex)
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
        wb —— 开发工作台命令行

        用法:
          wb serve                              启动 Web（UI + REST + MCP 端点）
          wb mcp                                stdio ↔ HTTP 的 MCP 桥接
          wb project list                       列出项目
          wb project add <名称> [--desc 描述]
          wb project show <项目ID>
          wb task list [--project <项目ID>] [--status <状态>] [--key 编号] [--type 类型]
          wb task create <项目ID> <标题> [--desc 描述] [--type requirement|bug|optimization]
                                       [--branch 分支]
          wb task show <任务ID或编号> [--project <项目ID>]
          wb task claim <任务ID> --agent <名称> [--lease 30]
          wb status set <任务ID> <状态> [--note 说明]
          wb doc upload <任务ID> <文件路径> [--kind prd|testcase|apidoc|other]
          wb approval list
          wb approval approve <审批ID> [--by 决策人]
          wb approval reject <审批ID> --reason <理由>
          wb milestone list <项目ID>
          wb milestone create <项目ID> <名称> [--desc 描述]
          wb milestone delete <里程碑ID>
          wb milestone assign <任务ID> <里程碑ID>     传 0 表示摘出来
          wb metrics [<项目ID>]
          wb workspace export <目录>
          wb workspace import <目录> --home <目标WORKBENCH_HOME>
          wb tool list
          wb tool enable <工具名>
          wb tool disable <工具名>
          wb tool reset
          wb review start <任务ID>                              开一轮评审（7 个维度）
          wb review list <任务ID>                               所有轮次与逐维度结论
          wb review submit <任务ID> <维度> --pass|--fail [--findings 说明] [--evidence 证据]
          wb project config <项目ID> [--repo 路径] [--agent 命令] [--allow 命令1,命令2]
                                 [--webhook 地址] [--format generic|dingtalk|feishu]
          wb task launch <任务ID>

        全局参数:
          --url <地址>        服务地址，默认 http://127.0.0.1:5177
                              也可用环境变量 WORKBENCH_URL 指定
        """);
}

static async Task<int> ServeAsync(List<string> cliArgs)
{
    var projectPath = ResolveWebProject();
    if (projectPath is null)
    {
        Console.Error.WriteLine(
            "找不到 Workbench.Web 工程。请用 --project <路径> 指定，或设置环境变量 WORKBENCH_WEB_PROJECT。");
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

    Console.WriteLine($"启动工作台: dotnet run --project {projectPath}");

    using var process = System.Diagnostics.Process.Start(startInfo)
                        ?? throw new WorkbenchCliException("无法启动 dotnet 进程。");

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
        var candidate = Path.Combine(dir, "src", "Workbench.Web", "Workbench.Web.csproj");
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
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "list";

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
                Console.Error.WriteLine("用法: wb project add <名称> [--desc 描述]");
                return 1;
            }

            var name = cliArgs[1];
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
                Console.Error.WriteLine("用法: wb project show <项目ID>");
                return 1;
            }

            var project = await client.GetAsync<ProjectSummaryDto>($"/api/projects/{cliArgs[1]}");
            Console.WriteLine(JsonSerializer.Serialize(project, CliJson.Pretty));
            return 0;
        }

        case "config":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine(
                    "用法: wb project config <项目ID> [--repo 路径] [--agent 命令] [--allow 命令1,命令2] [--webhook 地址] [--format 格式]");
                return 1;
            }

            var projectId = cliArgs[1];
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
            Console.Error.WriteLine($"未知子命令: wb project {sub}");
            return 1;
    }
}

static async Task<int> TaskCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var query = new List<string>();
            var project = GetOption(cliArgs, "--project");
            var status = GetOption(cliArgs, "--status");
            var key = GetOption(cliArgs, "--key");
            var type = GetOption(cliArgs, "--type");
            if (project is not null) query.Add($"projectId={project}");
            if (status is not null) query.Add($"status={status}");
            if (key is not null) query.Add($"key={key}");
            if (type is not null) query.Add($"type={type}");

            var path = "/api/tasks" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
            var tasks = await client.GetAsync<List<TaskSummaryDto>>(path);
            if (tasks.Count == 0)
            {
                Console.WriteLine("没有任务。");
                return 0;
            }

            Console.WriteLine($"{"编号",-12} {"类型",-6} {"ID",-38} {"状态",-12} {"分支",-18} 标题");
            foreach (var t in tasks)
            {
                Console.WriteLine(
                    $"{Truncate(t.Key ?? "-", 12),-12} {TaskTypeLabel(t.Type),-6} {t.Id,-38} {t.Status,-12} " +
                    $"{Truncate(t.Branch ?? "-", 18),-18} {t.Title}");
            }

            return 0;
        }

        case "create":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: wb task create <项目ID> <标题> [--desc 描述]");
                return 1;
            }

            var created = await client.PostAsync<TaskSummaryDto>(
                $"/api/projects/{cliArgs[1]}/tasks",
                new
                {
                    title = cliArgs[2],
                    description = GetOption(cliArgs, "--desc"),
                    type = GetOption(cliArgs, "--type"),
                    branch = GetOption(cliArgs, "--branch")
                });
            Console.WriteLine($"已创建任务 {created.Id} · {created.Key} · {created.Title}"
                + (string.IsNullOrWhiteSpace(created.Branch) ? "" : $" · 分支 {created.Branch}"));
            return 0;
        }

        case "show":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb task show <任务ID或编号>");
                return 1;
            }

            // 传编号（REQ-001）也能找到 —— 人说话是「看下 req001」
            if (Guid.TryParse(cliArgs[1], out var taskId))
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

            var byKey = await client.GetAsync<List<TaskSummaryDto>>($"/api/tasks?projectId={projectId}&key={cliArgs[1]}");
            if (byKey.Count == 0)
            {
                Console.Error.WriteLine($"项目 {projectId} 里没有编号 {cliArgs[1]} 的任务。");
                return 1;
            }

            Console.WriteLine(JsonSerializer.Serialize(byKey[0], CliJson.Pretty));
            return 0;
        }

        case "claim":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb task claim <任务ID> --agent <名称> [--lease 30]");
                return 1;
            }

            var agent = GetOption(cliArgs, "--agent") ?? "cli";
            var lease = int.Parse(GetOption(cliArgs, "--lease") ?? "30");
            var task = await client.PostAsync<TaskSummaryDto>(
                $"/api/tasks/{cliArgs[1]}/claim", new { agentId = agent, leaseMinutes = lease });
            Console.WriteLine($"已认领 {task.Id}，租约至 {task.LeaseExpiresAt}");
            return 0;
        }

        case "launch":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb task launch <任务ID>");
                return 1;
            }

            var result = await client.PostAsync<AgentLaunchResult>($"/api/tasks/{cliArgs[1]}/launch-agent", new { });
            Console.WriteLine($"已拉起外部 agent（PID {result.ProcessId}）：{result.Command}");
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: wb task {sub}");
            return 1;
    }
}

static async Task<int> StatusCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);

    if (cliArgs.Count < 3 || !string.Equals(cliArgs[0], "set", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("用法: wb status set <任务ID> <状态> [--note 说明]");
        return 1;
    }

    var task = await client.PostAsync<TaskSummaryDto>(
        $"/api/tasks/{cliArgs[1]}/status",
        new { status = cliArgs[2], note = GetOption(cliArgs, "--note"), actor = "cli", source = "Human" });

    Console.WriteLine($"状态已更新为 {task.Status}");
    return 0;
}

static async Task<int> DocCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);

    if (cliArgs.Count < 3 || !string.Equals(cliArgs[0], "upload", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("用法: wb doc upload <任务ID> <文件路径> [--kind prd|testcase|apidoc|other]");
        return 1;
    }

    var taskId = cliArgs[1];
    var filePath = Path.GetFullPath(cliArgs[2]);
    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"文件不存在: {filePath}");
        return 1;
    }

    var kind = GetOption(cliArgs, "--kind") ?? "other";
    var fileName = Path.GetFileName(filePath);

    using var content = new MultipartFormDataContent();
    var bytes = await File.ReadAllBytesAsync(filePath);
    var fileContent = new ByteArrayContent(bytes);
    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    content.Add(fileContent, "file", fileName);
    content.Add(new StringContent(kind), "kind");

    var response = await client.PostRawAsync($"/api/tasks/{taskId}/documents", content);
    Console.WriteLine($"已上传 {fileName}: {response}");
    return 0;
}

static async Task<int> ApprovalCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "list";

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
                Console.WriteLine($"{a.Id}  {a.ToolName}  {a.Summary}");
            }

            return 0;
        }

        case "approve":
        case "reject":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine($"用法: wb approval {sub} <审批ID> [--by 决策人] [--reason 理由]");
                return 1;
            }

            var reason = GetOption(cliArgs, "--reason");
            if (sub == "reject" && string.IsNullOrWhiteSpace(reason))
            {
                Console.Error.WriteLine("驳回必须提供 --reason。");
                return 1;
            }

            await client.PostAsync<ApprovalDto>(
                $"/api/approvals/{cliArgs[1]}/decide",
                new { approve = sub == "approve", decidedBy = GetOption(cliArgs, "--by") ?? "cli", reason });

            Console.WriteLine(sub == "approve" ? "已批准。" : "已驳回。");
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: wb approval {sub}");
            return 1;
    }
}

static async Task<int> MilestoneCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb milestone list <项目ID>");
                return 1;
            }

            var milestones = await client.GetAsync<List<MilestoneDto>>($"/api/projects/{cliArgs[1]}/milestones");
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
                Console.Error.WriteLine("用法: wb milestone create <项目ID> <名称> [--desc 描述]");
                return 1;
            }

            var created = await client.PostAsync<MilestoneDto>(
                $"/api/projects/{cliArgs[1]}/milestones",
                new { name = cliArgs[2], description = GetOption(cliArgs, "--desc") });

            Console.WriteLine($"已创建里程碑 {created.Id} · {created.Name}");
            return 0;
        }

        case "delete":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb milestone delete <里程碑ID>");
                return 1;
            }

            await client.DeleteAsync($"/api/milestones/{cliArgs[1]}");
            Console.WriteLine("已删除里程碑（任务保留，只是不再归属任何里程碑）。");
            return 0;
        }

        case "assign":
        {
            if (cliArgs.Count < 3)
            {
                Console.Error.WriteLine("用法: wb milestone assign <任务ID> <里程碑ID>（传 0 表示摘出来）");
                return 1;
            }

            Guid? milestoneId = cliArgs[2] == "0" ? null : Guid.Parse(cliArgs[2]);
            var task = await client.PostAsync<TaskSummaryDto>(
                $"/api/tasks/{cliArgs[1]}/milestone", new { milestoneId });
            Console.WriteLine($"已更新任务 {task.Id} 的里程碑。");
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知子命令: wb milestone {sub}");
            return 1;
    }
}

static async Task<int> MetricsCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);

    if (cliArgs.Count >= 2)
    {
        var one = await client.GetAsync<ProjectMetricsDto>($"/api/projects/{cliArgs[1]}/metrics");
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
    Console.WriteLine($"  状态: {string.Join(" ", m.StatusCounts.Select(s => $"{s.Status}={s.Count}"))}");
    Console.WriteLine($"  流速: {string.Join(" ", m.Throughput.Select(t => $"{t.Week[5..]}={t.Completed}"))}");
}

static async Task<int> WorkspaceCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "";

    switch (sub)
    {
        case "export":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb workspace export <目录>");
                return 1;
            }

            var result = await client.PostAsync<WorkspaceArchiveResult>(
                "/api/workspace/export", new { targetDirectory = Path.GetFullPath(cliArgs[1]) });
            Console.WriteLine(result.Message);
            return 0;
        }

        case "import":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb workspace import <目录> --home <目标WORKBENCH_HOME>");
                return 1;
            }

            var home = GetOption(cliArgs, "--home");
            if (string.IsNullOrWhiteSpace(home))
            {
                Console.Error.WriteLine("必须用 --home 指定目标 WORKBENCH_HOME。");
                return 1;
            }

            // 导入只是搬文件，不碰数据库 —— 所以本地做，不必等服务在跑。
            // （换机场景恰恰是服务还没起的时候。）
            var target = Path.GetFullPath(home);
            var (fileCount, totalBytes) = WorkspaceArchiveFiles.Import(Path.GetFullPath(cliArgs[1]), target);

            Console.WriteLine($"已恢复到 {target}（{fileCount} 个文件，{totalBytes} 字节）。");
            Console.WriteLine("把 WORKBENCH_HOME 指到这个目录再启动服务即可。");
            return 0;
        }

        default:
            Console.Error.WriteLine("用法: wb workspace export <目录> | wb workspace import <目录> --home <目录>");
            return 1;
    }
}

static async Task<int> ReviewCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "list";

    switch (sub)
    {
        case "start":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb review start <任务ID>");
                return 1;
            }

            var round = await client.PostAsync<ReviewRoundDto>(
                $"/api/tasks/{cliArgs[1]}/review", new { reviewer = "cli" });

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
                Console.Error.WriteLine("用法: wb review list <任务ID>");
                return 1;
            }

            var rounds = await client.GetAsync<List<ReviewRoundDto>>($"/api/tasks/{cliArgs[1]}/reviews");
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
                Console.Error.WriteLine("用法: wb review submit <任务ID> <维度> --pass|--fail [--findings 说明] [--evidence 证据]");
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
                $"/api/tasks/{cliArgs[1]}/review/{cliArgs[2]}",
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

        default:
            Console.Error.WriteLine($"未知子命令: wb review {sub}");
            return 1;
    }
}

static async Task<int> ToolCommandAsync(string url, List<string> cliArgs)
{
    var client = new WorkbenchClient(url);
    var sub = cliArgs.Count > 0 ? cliArgs[0].ToLowerInvariant() : "list";

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
                Console.Error.WriteLine($"用法: wb tool {sub} <工具名>");
                return 1;
            }

            var updated = await client.PutAsync<McpToolInfo>(
                $"/api/mcp/tools/{cliArgs[1]}", new { enabled = sub == "enable" });

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
            Console.Error.WriteLine($"未知子命令: wb tool {sub}");
            return 1;
    }
}

static string? GetOption(List<string> cliArgs, string name)
{
    var index = cliArgs.IndexOf(name);
    return index >= 0 && index + 1 < cliArgs.Count ? cliArgs[index + 1] : null;
}

static string Truncate(string value, int length) =>
    value.Length <= length ? value : value[..(length - 1)] + "…";

/// <summary>类型的中文简称，列表里一眼能看出是需求/缺陷/优化。</summary>
static string TaskTypeLabel(Workbench.Domain.TaskType type) => type switch
{
    Workbench.Domain.TaskType.Bug => "缺陷",
    Workbench.Domain.TaskType.Optimization => "优化",
    _ => "需求"
};
