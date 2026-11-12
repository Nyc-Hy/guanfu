using System.Text.Json;
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
          wb task list [--project <项目ID>] [--status <状态>]
          wb task create <项目ID> <标题> [--desc 描述]
          wb task show <任务ID>
          wb task claim <任务ID> --agent <名称> [--lease 30]
          wb status set <任务ID> <状态> [--note 说明]
          wb doc upload <任务ID> <文件路径> [--kind prd|testcase|apidoc|other]
          wb approval list
          wb approval approve <审批ID> [--by 决策人]
          wb approval reject <审批ID> --reason <理由>

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
            var projects = await client.GetAsync<List<ProjectView>>("/api/projects");
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
            var created = await client.PostAsync<ProjectView>(
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

            var project = await client.GetAsync<ProjectView>($"/api/projects/{cliArgs[1]}");
            Console.WriteLine(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
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
            if (project is not null) query.Add($"projectId={project}");
            if (status is not null) query.Add($"status={status}");

            var path = "/api/tasks" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
            var tasks = await client.GetAsync<List<TaskView>>(path);
            if (tasks.Count == 0)
            {
                Console.WriteLine("没有任务。");
                return 0;
            }

            Console.WriteLine($"{"ID",-38} {"状态",-12} {"项目",-16} 标题");
            foreach (var t in tasks)
            {
                Console.WriteLine($"{t.Id,-38} {t.Status,-12} {Truncate(t.ProjectName, 16),-16} {t.Title}");
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

            var created = await client.PostAsync<TaskView>(
                $"/api/projects/{cliArgs[1]}/tasks",
                new { title = cliArgs[2], description = GetOption(cliArgs, "--desc") });
            Console.WriteLine($"已创建任务 {created.Id} · {created.Title}");
            return 0;
        }

        case "show":
        {
            if (cliArgs.Count < 2)
            {
                Console.Error.WriteLine("用法: wb task show <任务ID>");
                return 1;
            }

            var task = await client.GetAsync<TaskView>($"/api/tasks/{cliArgs[1]}");
            Console.WriteLine(JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
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
            var task = await client.PostAsync<TaskView>(
                $"/api/tasks/{cliArgs[1]}/claim", new { agentId = agent, leaseMinutes = lease });
            Console.WriteLine($"已认领 {task.Id}，租约至 {task.LeaseExpiresAt}");
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

    var task = await client.PostAsync<TaskView>(
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
            var approvals = await client.GetAsync<List<ApprovalView>>("/api/approvals?status=Pending");
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

            await client.PostAsync<ApprovalView>(
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

static string? GetOption(List<string> cliArgs, string name)
{
    var index = cliArgs.IndexOf(name);
    return index >= 0 && index + 1 < cliArgs.Count ? cliArgs[index + 1] : null;
}

static string Truncate(string value, int length) =>
    value.Length <= length ? value : value[..(length - 1)] + "…";

public sealed record ProjectView(
    Guid Id, string Name, string? Description, double Progress,
    int TotalTasks, int DoneTasks, int BlockedTasks, int ActiveTasks,
    DateTimeOffset? LastActivityAt, bool Stale, string? RepoPath, string? Branch, string? RepoRemote);

public sealed record TaskView(
    Guid Id, Guid ProjectId, string ProjectName, string Title, string Status,
    string? ClaimedBy, DateTimeOffset? LeaseExpiresAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? LastActivityAt, bool Stale);

public sealed record ApprovalView(
    Guid Id, string ToolName, string Kind, string Summary, string Status,
    string? RequestedBy, string? DecidedBy, string? DecisionReason, string? ResultSummary,
    Guid? TaskId, Guid? ProjectId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DecidedAt);
