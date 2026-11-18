using System.ComponentModel;
using ModelContextProtocol.Server;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Mcp;

public sealed record FsListToolResult(
    bool Ok,
    string Root,
    string RelativePath,
    IReadOnlyList<FileEntryDto> Entries,
    string? Error = null);

public sealed record FsReadToolResult(
    bool Ok,
    string RelativePath,
    string Content,
    long Size,
    bool Truncated,
    string? Binary,
    string? Error = null);

/// <summary>写操作类工具（git / shell）的统一返回。</summary>
public sealed record WriteToolResult(
    bool Ok,
    Guid ApprovalId,
    string ApprovalStatus,
    string Message,
    string? Output = null)
{
    /// <summary>
    /// 失败原因。写工具失败时 Message 本身就是原因，这里再落一个 error 字段 ——
    /// agent 读所有工具的结果都用同一套规则：ok=false 就看 error。
    /// </summary>
    public string? Error => Ok ? null : Message;
}

/// <summary>
/// 通用工具：文件系统 + git 写操作 + shell 执行。
///
/// 两条硬规矩：
/// 1. 文件系统只能访问项目仓库目录内，越界直接拒绝；
/// 2. git 写操作与 shell 执行都必须人工审批通过后才真正执行。
///
/// 审批走的是应用层那**一套**闸门（<see cref="IApprovalGate"/>）——
/// 四个写工具共用，不各写各的。闸门会把审批绑在具体操作上：
/// 批过「提交 A」的那条审批，拿去提交 B 会被拒。
/// </summary>
[McpServerToolType]
public sealed class GeneralMcpTools(
    IFileSystemService files,
    IProjectService projects,
    IGitWriter git,
    IShellRunner shell,
    IApprovalGate approvalGate,
    IToolAuditService audit,
    IClock clock,
    IGitInspector? gitInspector = null,
    IProjectScopeResolver? scopeResolver = null,
    IFileChangeService? fileChanges = null,
    IWorkstationResolver? workstations = null,
    IAgentSessionAccessor? sessions = null)
{
    /// <summary>
    /// 算出这次调用算在哪个项目上。
    ///
    /// 优先用会话绑定的项目（agent 在哪个仓库干活就落到哪个项目），
    /// 没有会话时才看调用方显式传的 projectId。两边都对不上就拒 ——
    /// 不能让 agent 拿着 A 项目的会话去改 B 项目。
    /// </summary>
    private async Task<ProjectScope> ResolveScopeAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (scopeResolver is not null)
        {
            return await scopeResolver.ResolveAsync(projectId, cancellationToken);
        }

        if (projectId is Guid pid)
        {
            return new ProjectScope(pid, string.Empty, null, null, null);
        }

        throw new GuanfuException(
            "当前调用没有绑定项目，也没有传 projectId。请先调用 guanfu_bind_session。");
    }
    [McpServerTool(Name = "fs_list")]
    [Description("列出项目仓库目录下的文件与子目录。根目录就是项目的仓库路径，不能访问仓库之外。")]
    public async Task<FsListToolResult> ListFilesAsync(
        Guid? projectId = null,
        string? path = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var scope = await ResolveScopeAsync(projectId, cancellationToken);
            var result = await files.ListAsync(
                scope.ProjectId, path, cancellationToken, scope.TaskId, scope.AgentId);

            await audit.RecordAsync(new ToolAuditEntry(
                "fs_list", callerId ?? scope.AgentId, scope.ProjectId, scope.TaskId, path ?? ".",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"{result.Entries.Count} 项",
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new FsListToolResult(true, result.Root, result.RelativePath, result.Entries);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "fs_list", callerId, projectId, null, path ?? ".",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new FsListToolResult(false, string.Empty, path ?? string.Empty, [], ex.Message);
        }
    }

    [McpServerTool(Name = "fs_read")]
    [Description("读取项目仓库里的一个文本文件。二进制文件会返回 binary 标记而不是乱码。")]
    public async Task<FsReadToolResult> ReadFileAsync(
        Guid? projectId = null,
        string? path = null,
        int maxBytes = 512 * 1024,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new GuanfuException("文件路径不能为空。");
            }

            var scope = await ResolveScopeAsync(projectId, cancellationToken);
            var result = await files.ReadAsync(
                scope.ProjectId, path, maxBytes, cancellationToken, scope.TaskId, scope.AgentId);

            await audit.RecordAsync(new ToolAuditEntry(
                "fs_read", callerId ?? scope.AgentId, scope.ProjectId, scope.TaskId, path ?? string.Empty,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success",
                ResultSummary: $"{result.Size} 字节",
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new FsReadToolResult(
                true, result.RelativePath, result.Content, result.Size, result.Truncated, result.Binary);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new ToolAuditEntry(
                "fs_read", callerId, projectId, null, path ?? string.Empty,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new FsReadToolResult(false, path ?? string.Empty, string.Empty, 0, false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "git_commit")]
    [McpRequiresApproval]
    [Description(
        "在项目仓库里提交改动（自动 git add -A 后 git commit）。属于写操作，必须人工审批通过后才真正执行。" +
        "首次调用会生成审批请求并等待人工决定；批准之后带 approvalId 再次调用即可直接执行。" +
        "审批绑定的是「待提交内容」：批准之后工作区又变了，续跑会被拒，需要重新申请。")]
    public async Task<WriteToolResult> CommitAsync(
        Guid? projectId = null,
        string? message = null,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new GuanfuException("提交信息不能为空。");
            }

            // 先把作用域定下来：项目从会话解析，任务与 agent 跟着会话走。
            // 后面的代码一律用解析后的值，别再拿原始形参。
            var scope = await ResolveScopeAsync(projectId, cancellationToken);
            projectId = scope.ProjectId;
            taskId ??= scope.TaskId;
            callerId ??= scope.AgentId;

            var config = await projects.GetConfigAsync(scope.ProjectId, cancellationToken);
            if (string.IsNullOrWhiteSpace(config.RepoPath))
            {
                return new WriteToolResult(false, Guid.Empty, "Rejected", "该项目没有配置仓库路径，无法提交。");
            }

            // 工作目录 = 这次任务的工位（自己的 worktree）；没有工位就是主工作区。
            var workDir = await ResolveWorkDirAsync(scope, config.RepoPath!, cancellationToken);

            // 审批绑内容（P0-3）：先取待提交内容的指纹。读不到仓库状态就拒 ——
            // 不能让人批一个连内容都看不见的提交。gitInspector 缺省时跳过绑定（兼容旧宿主）。
            string? fingerprint = null;
            if (gitInspector is not null)
            {
                fingerprint = gitInspector.GetPendingCommitFingerprint(workDir);
                if (fingerprint is null)
                {
                    await AuditAsync("git_commit", callerId, projectId, taskId, message ?? string.Empty,
                        "denied", "读不到仓库状态，无法提交。", started, approvalId, cancellationToken);

                    return new WriteToolResult(
                        false, approvalId ?? Guid.Empty, nameof(ApprovalStatus.Rejected),
                        "读不到仓库状态，无法提交。请确认仓库路径有效、仓库可读后重试。");
                }
            }

            var payload = GuanfuJson.Serialize(new
            {
                project = config.Name,
                repoPath = config.RepoPath,
                // 绑实际要写的目录 —— 批了「在 worktree A 提交」不能拿去提交 worktree B
                workDir,
                message,
                fingerprint
            });

            var gate = await approvalGate.AcquireAsync(
                "git_commit", "git.write", payload,
                BuildCommitSummary(config.Name, message, config.RepoPath!),
                callerId, taskId, scope.ProjectId, approvalId, waitSeconds, cancellationToken,
                sessions?.CurrentCredential?.MemberId);

            if (!gate.Approved)
            {
                await AuditAsync("git_commit", callerId, projectId, taskId, payload,
                    gate.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                    gate.Message, started, gate.ApprovalId, cancellationToken);

                return new WriteToolResult(false, gate.ApprovalId, gate.Status, gate.Message);
            }

            return await RunApprovedAsync(
                gate.ApprovalId,
                async () =>
                {
                    // 批准之后、真正提交之前再算一次指纹：等待人工期间工作区可能又变了。
                    // 变了就拒 —— 人批的是「那些内容」，不是「提交这个动作」。
                    if (gitInspector is not null)
                    {
                        var current = gitInspector.GetPendingCommitFingerprint(config.RepoPath!);
                        if (current is null || current != fingerprint)
                        {
                            await AuditAsync("git_commit", callerId, projectId, taskId, payload,
                                "denied", "工作区已变化，请重新申请。", started, gate.ApprovalId, cancellationToken);

                            return new WriteToolResult(
                                false, gate.ApprovalId, nameof(ApprovalStatus.Executed),
                                "工作区已变化，请重新申请。（审批已消费，请重新申请）");
                        }
                    }

                    var result = await git.CommitAsync(workDir, message, cancellationToken);

                    await AuditAsync("git_commit", callerId, projectId, taskId, payload,
                        result.Ok ? "success" : "error",
                        result.Ok ? result.Output : result.Error,
                        started, gate.ApprovalId, cancellationToken);

                    // 提交成功之后，把这次提交改了什么文件采下来。
                    // 这是「产出可追溯」的关键点：观复自己去 git 里数出来，
                    // 不靠 agent 自报 —— agent 绕不过去。
                    //
                    // 放在审计之后：审计记的是「这次提交花了多久」，
                    // 不该把顺手记流水的耗时算进去。
                    if (result.Ok)
                    {
                        await CaptureCommitFilesAsync(
                            taskId, scope, workDir, callerId, cancellationToken);
                    }

                    return new WriteToolResult(
                        result.Ok,
                        gate.ApprovalId,
                        nameof(ApprovalStatus.Executed),
                        result.Ok ? "已审批并提交。" : $"提交失败: {result.Error}（审批已消费，请重新申请）",
                        result.Ok ? result.Output : result.Error);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            await AuditAsync("git_commit", callerId, projectId, taskId, message ?? string.Empty, "error", ex.Message, started, approvalId, cancellationToken);
            return new WriteToolResult(false, approvalId ?? Guid.Empty, "Error", $"提交失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 把刚提交的那次 commit 改动的文件采下来，落到任务名下。
    ///
    /// 采集失败（读不到仓库 / git 挂了）**不抛异常** ——
    /// 提交已经成功了，不能因为「顺手记个流水」失败就把 agent 的活带崩。
    /// </summary>
    private async Task CaptureCommitFilesAsync(
        Guid? taskId,
        ProjectScope scope,
        string repoPath,
        string? callerId,
        CancellationToken cancellationToken)
    {
        // 没有任务归属就无处可挂 —— 跳过采集，不报错。
        if (fileChanges is null || gitInspector is null || taskId is null)
        {
            return;
        }

        try
        {
            var sha = gitInspector.GetHeadSha(repoPath);
            if (string.IsNullOrWhiteSpace(sha))
            {
                return;
            }

            await fileChanges.CaptureCommitAsync(
                taskId.Value,
                scope.ProjectId,
                repoPath,
                sha,
                scope.SessionId,
                callerId,
                cancellationToken);
        }
        catch (Exception)
        {
            // 采集是「锦上添花」，失败就算了 —— 提交已经落地。
            //
            // 这里连取消也不往上抛：提交已经成功了，再抛异常的话工具会返回
            // ok=false，agent 会以为提交失败而重试 —— 那是真的把事情搞坏。
        }
    }

    /// <summary>
    /// 这次调用该在哪个目录上干活 —— 任务登记了工位就用工位，没有就是项目主工作区。
    ///
    /// 工具认工位是必须的：agent 在自己的 worktree 里写代码，
    /// 观复却去读主工作区的话，产出采集会把**主工作区**的改动记成这个任务的产出。
    /// </summary>
    private async Task<string> ResolveWorkDirAsync(
        ProjectScope scope,
        string fallback,
        CancellationToken cancellationToken)
    {
        if (workstations is null)
        {
            return fallback;
        }

        var station = await workstations.ResolveAsync(
            scope.ProjectId, scope.TaskId, scope.AgentId, cancellationToken);
        return string.IsNullOrWhiteSpace(station) ? fallback : station;
    }

    /// <summary>提交审批摘要：带上改动文件数与清单，人批的时候看得见到底要提交什么。</summary>
    private string BuildCommitSummary(string projectName, string message, string repoPath)
    {
        var summary = $"在 {projectName} 提交：{message}";
        if (gitInspector is null)
        {
            return summary;
        }

        try
        {
            var state = gitInspector.Inspect(repoPath);
            summary += $"；改动 {state.ChangedFiles} 个文件";

            // 清单别全列：一次改动上百个文件时，摘要会长到没法看 ——
            // 列前几个 + 总数，人要细看还有别的入口。
            if (state.ChangedFilePaths.Count > 0)
            {
                var head = state.ChangedFilePaths.Take(MaxFilesInSummary).ToList();
                var more = state.ChangedFilePaths.Count - head.Count;
                summary += $"：{string.Join(", ", head)}";
                if (more > 0)
                {
                    summary += $" 等 {state.ChangedFilePaths.Count} 个";
                }
            }
        }
        catch (Exception)
        {
            // 摘要里列不出文件不影响审批本身（指纹已经绑了内容），退回基础摘要即可。
        }

        return summary;
    }

    /// <summary>审批摘要里最多列几个文件名。</summary>
    private const int MaxFilesInSummary = 8;

    [McpServerTool(Name = "git_push")]
    [McpRequiresApproval]
    [Description(
        "把项目仓库推送到远端（git push）。属于写操作，必须人工审批通过后才真正执行。" +
        "首次调用会生成审批请求并等待人工决定；批准之后带 approvalId 再次调用即可直接执行。")]
    public async Task<WriteToolResult> PushAsync(
        Guid? projectId = null,
        string? branch = null,
        string? remote = null,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var scope = await ResolveScopeAsync(projectId, cancellationToken);
            projectId = scope.ProjectId;
            taskId ??= scope.TaskId;
            callerId ??= scope.AgentId;

            var config = await projects.GetConfigAsync(scope.ProjectId, cancellationToken);
            if (string.IsNullOrWhiteSpace(config.RepoPath))
            {
                return new WriteToolResult(false, Guid.Empty, "Rejected", "该项目没有配置仓库路径，无法推送。");
            }

            var target = string.IsNullOrWhiteSpace(remote)
                ? (string.IsNullOrWhiteSpace(branch) ? "默认远端与分支" : $"分支 {branch}")
                : $"远端 {remote}" + (string.IsNullOrWhiteSpace(branch) ? "" : $" 分支 {branch}");

            // 工作目录 = 工位（跟提交一致：在哪个 worktree 提交，就推哪个 worktree 的分支）
            var workDir = await ResolveWorkDirAsync(scope, config.RepoPath!, cancellationToken);

            // 审批绑内容：把 HEAD 一起算进指纹。
            // 只绑「推分支 X 到远端 Y」不够 —— 等待人工期间又跑了一次被批准的
            // git_commit，推出去的 commit 就比人当时看到的多了。
            string? headSha = null;
            if (gitInspector is not null)
            {
                headSha = gitInspector.GetHeadSha(workDir);
                if (string.IsNullOrWhiteSpace(headSha))
                {
                    // 读不到 HEAD（仓库还没有提交 / 不是 git 仓库）：推不出东西去，
                    // 直接拒，别让人批一条注定失败的推送。
                    await AuditAsync("git_push", callerId, projectId, taskId, branch ?? string.Empty,
                        "denied", "读不到仓库状态，无法推送。", started, approvalId, cancellationToken);

                    return new WriteToolResult(
                        false, approvalId ?? Guid.Empty, nameof(ApprovalStatus.Rejected),
                        "读不到仓库状态（仓库可能还没有任何提交），无法推送。");
                }
            }

            var payload = GuanfuJson.Serialize(new
            {
                project = config.Name,
                repoPath = config.RepoPath,
                workDir,
                branch,
                remote,
                headSha
            });

            var gate = await approvalGate.AcquireAsync(
                "git_push", "git.write", payload,
                $"在 {config.Name} 推送：{target}",
                callerId, taskId, scope.ProjectId, approvalId, waitSeconds, cancellationToken,
                sessions?.CurrentCredential?.MemberId);

            if (!gate.Approved)
            {
                await AuditAsync("git_push", callerId, projectId, taskId, payload,
                    gate.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                    gate.Message, started, gate.ApprovalId, cancellationToken);

                return new WriteToolResult(false, gate.ApprovalId, gate.Status, gate.Message);
            }

            return await RunApprovedAsync(
                gate.ApprovalId,
                async () =>
                {
                    // 批准之后、推送之前再核一次 HEAD：等待期间多了提交就拒 ——
                    // 人批的是「那些 commit」，不是「推送这个动作」。
                    if (gitInspector is not null && !string.IsNullOrWhiteSpace(headSha))
                    {
                        var currentHead = gitInspector.GetHeadSha(workDir);
                        if (currentHead != headSha)
                        {
                            await AuditAsync("git_push", callerId, projectId, taskId, payload,
                                "denied", "仓库 HEAD 已变化，请重新申请。", started, gate.ApprovalId, cancellationToken);

                            return new WriteToolResult(
                                false, gate.ApprovalId, nameof(ApprovalStatus.Executed),
                                "仓库 HEAD 已变化，要推送的内容跟当初审批的不一致，请重新申请。（审批已消费）");
                        }
                    }

                    GitCommandResult result;
                    try
                    {
                        result = await git.PushAsync(workDir, branch, remote, cancellationToken);
                    }
                    catch (ArgumentException ex)
                    {
                        // 非法 branch/remote（--force / --delete / ext:: 等注入）：
                        // 转结构化错误带回给 agent，不让异常被 MCP 吞成一句泛化信息。
                        await AuditAsync("git_push", callerId, projectId, taskId, payload,
                            "error", ex.Message, started, gate.ApprovalId, cancellationToken);

                        return new WriteToolResult(
                            false, gate.ApprovalId, nameof(ApprovalStatus.Executed),
                            $"推送参数非法: {ex.Message}（审批已消费，请重新申请）", ex.Message);
                    }

                    await AuditAsync("git_push", callerId, projectId, taskId, payload,
                        result.Ok ? "success" : "error",
                        result.Ok ? result.Output : result.Error,
                        started, gate.ApprovalId, cancellationToken);

                    return new WriteToolResult(
                        result.Ok,
                        gate.ApprovalId,
                        nameof(ApprovalStatus.Executed),
                        result.Ok ? "已审批并推送。" : $"推送失败: {result.Error}（审批已消费，请重新申请）",
                        result.Ok ? result.Output : result.Error);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            await AuditAsync("git_push", callerId, projectId, taskId, branch ?? string.Empty, "error", ex.Message, started, approvalId, cancellationToken);
            return new WriteToolResult(false, approvalId ?? Guid.Empty, "Error", $"推送失败: {ex.Message}");
        }
    }

    [McpServerTool(Name = "shell_run")]
    [McpRequiresApproval]
    [Description(
        "在项目仓库里执行一条命令。命令必须命中项目的白名单（项目设置里配置），且必须人工审批通过后才真正执行。" +
        "命令不走 shell 解析，参数按空格切分，不支持管道与重定向。")]
    public async Task<WriteToolResult> RunShellAsync(
        Guid? projectId = null,
        string? command = null,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new GuanfuException("命令不能为空。");
            }

            var scope = await ResolveScopeAsync(projectId, cancellationToken);
            projectId = scope.ProjectId;
            taskId ??= scope.TaskId;
            callerId ??= scope.AgentId;

            var config = await projects.GetConfigAsync(scope.ProjectId, cancellationToken);

            // 白名单不命中直接拒，不进审批 —— 没理由让人批一条根本不该跑的命令
            if (!shell.IsAllowed(config.AllowedCommands, command))
            {
                var allowedText = config.AllowedCommands.Count == 0
                    ? "（该项目的白名单为空）"
                    : string.Join("、", config.AllowedCommands);

                await AuditAsync("shell_run", callerId, projectId, taskId, command, "denied",
                    $"命令不在白名单内。当前白名单: {allowedText}", started, approvalId, cancellationToken);

                return new WriteToolResult(
                    false, approvalId ?? Guid.Empty, nameof(ApprovalStatus.Rejected),
                    $"命令不在项目白名单内，已拒绝。当前白名单: {allowedText}");
            }

            if (string.IsNullOrWhiteSpace(config.RepoPath))
            {
                return new WriteToolResult(false, Guid.Empty, "Rejected", "该项目没有配置仓库路径，无法执行命令。");
            }

            // 工作目录 = 工位：agent 的命令要在它自己的 worktree 里跑
            var workDir = await ResolveWorkDirAsync(scope, config.RepoPath!, cancellationToken);

            var gate = await approvalGate.AcquireAsync(
                "shell_run", "shell.write", command,
                $"在 {config.Name} 执行：{command}",
                callerId, taskId, scope.ProjectId, approvalId, waitSeconds, cancellationToken,
                sessions?.CurrentCredential?.MemberId);

            if (!gate.Approved)
            {
                await AuditAsync("shell_run", callerId, projectId, taskId, command,
                    gate.Status == nameof(ApprovalStatus.Rejected) ? "denied" : "pending",
                    gate.Message, started, gate.ApprovalId, cancellationToken);

                return new WriteToolResult(false, gate.ApprovalId, gate.Status, gate.Message);
            }

            return await RunApprovedAsync(
                gate.ApprovalId,
                async () =>
                {
                    var result = await shell.RunAsync(workDir, command, cancellationToken);

                    await AuditAsync("shell_run", callerId, projectId, taskId, command,
                        result.Ok ? "success" : "error",
                        result.Ok ? result.StandardOutput : result.StandardError,
                        started, gate.ApprovalId, cancellationToken);

                    var output = string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : $"{result.StandardOutput}\n{result.StandardError}";

                    return new WriteToolResult(
                        result.Ok,
                        gate.ApprovalId,
                        nameof(ApprovalStatus.Executed),
                        result.Ok ? "已审批并执行。" : $"执行失败，退出码 {result.ExitCode}。（审批已消费，请重新申请）",
                        output);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            await AuditAsync("shell_run", callerId, projectId, taskId, command ?? string.Empty, "error", ex.Message, started, approvalId, cancellationToken);
            return new WriteToolResult(false, approvalId ?? Guid.Empty, "Error", $"执行失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 拿到许可就跑，跑完（成或败）都要把审批收尾。
    ///
    /// 收尾很重要：审批被占住之后状态是 Executing，不收尾它就永远卡在那儿 ——
    /// 既不能被复用，界面上也看不出到底成没成。
    /// </summary>
    private async Task<WriteToolResult> RunApprovedAsync(
        Guid approvalId,
        Func<Task<WriteToolResult>> action,
        CancellationToken cancellationToken)
    {
        WriteToolResult result;
        string? summary = null;

        try
        {
            result = await action();
            summary = result.Message;
            return result;
        }
        finally
        {
            try
            {
                // 收尾用**不可取消**的 token：调用方断开（客户端取消 / HTTP 断开）时
                // 审批已经占住了，这时候最需要把它收干净。
                // 用原来的 token 的话，取消一旦发生，收尾必然失败，
                // 审批就卡在 Executing 直到 30 分钟超时回收。
                await approvalGate.CompleteAsync(approvalId, summary, CancellationToken.None);
            }
            catch (Exception)
            {
                // 收尾失败不能盖掉真正的执行结果
            }
        }
    }

    private Task AuditAsync(
        string toolName,
        string? callerId,
        Guid? projectId,
        Guid? taskId,
        string arguments,
        string outcome,
        string? summary,
        DateTimeOffset started,
        Guid? approvalId,
        CancellationToken cancellationToken)
        => audit.RecordAsync(new ToolAuditEntry(
            toolName, callerId, projectId, taskId, arguments,
            RequiredApproval: true, ApprovalId: approvalId,
            Outcome: outcome,
            ResultSummary: summary,
            RowsAffected: null,
            DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);
}
