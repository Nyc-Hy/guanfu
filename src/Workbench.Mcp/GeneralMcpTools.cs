using System.ComponentModel;
using ModelContextProtocol.Server;
using Workbench.Application;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Mcp;

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
    string? Output = null);

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
    IClock clock)
{
    [McpServerTool(Name = "fs_list")]
    [Description("列出项目仓库目录下的文件与子目录。根目录就是项目的仓库路径，不能访问仓库之外。")]
    public async Task<FsListToolResult> ListFilesAsync(
        Guid projectId,
        string? path = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var result = await files.ListAsync(projectId, path, cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "fs_list", callerId, projectId, null, path ?? ".",
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
        Guid projectId,
        string path,
        int maxBytes = 512 * 1024,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var result = await files.ReadAsync(projectId, path, maxBytes, cancellationToken);

            await audit.RecordAsync(new ToolAuditEntry(
                "fs_read", callerId, projectId, null, path,
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
                "fs_read", callerId, projectId, null, path,
                RequiredApproval: false, ApprovalId: null,
                Outcome: "error",
                ResultSummary: ex.Message,
                RowsAffected: null,
                DurationMs: (long)(clock.UtcNow - started).TotalMilliseconds), cancellationToken);

            return new FsReadToolResult(false, path, string.Empty, 0, false, null, ex.Message);
        }
    }

    [McpServerTool(Name = "git_commit")]
    [McpRequiresApproval]
    [Description(
        "在项目仓库里提交改动（自动 git add -A 后 git commit）。属于写操作，必须人工审批通过后才真正执行。" +
        "首次调用会生成审批请求并等待人工决定；批准之后带 approvalId 再次调用即可直接执行。")]
    public async Task<WriteToolResult> CommitAsync(
        Guid projectId,
        string message,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var config = await projects.GetConfigAsync(projectId, cancellationToken);
            if (string.IsNullOrWhiteSpace(config.RepoPath))
            {
                return new WriteToolResult(false, Guid.Empty, "Rejected", "该项目没有配置仓库路径，无法提交。");
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                project = config.Name,
                repoPath = config.RepoPath,
                message
            });

            var gate = await approvalGate.AcquireAsync(
                "git_commit", "git.write", payload,
                $"在 {config.Name} 提交：{message}",
                callerId, taskId, projectId, approvalId, waitSeconds, cancellationToken);

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
                    var result = await git.CommitAsync(config.RepoPath!, message, cancellationToken);

                    await AuditAsync("git_commit", callerId, projectId, taskId, payload,
                        result.Ok ? "success" : "error",
                        result.Ok ? result.Output : result.Error,
                        started, gate.ApprovalId, cancellationToken);

                    return new WriteToolResult(
                        result.Ok,
                        gate.ApprovalId,
                        result.Ok ? nameof(ApprovalStatus.Executed) : nameof(ApprovalStatus.Approved),
                        result.Ok ? "已审批并提交。" : $"提交失败: {result.Error}",
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

    [McpServerTool(Name = "git_push")]
    [McpRequiresApproval]
    [Description(
        "把项目仓库推送到远端（git push）。属于写操作，必须人工审批通过后才真正执行。" +
        "首次调用会生成审批请求并等待人工决定；批准之后带 approvalId 再次调用即可直接执行。")]
    public async Task<WriteToolResult> PushAsync(
        Guid projectId,
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
            var config = await projects.GetConfigAsync(projectId, cancellationToken);
            if (string.IsNullOrWhiteSpace(config.RepoPath))
            {
                return new WriteToolResult(false, Guid.Empty, "Rejected", "该项目没有配置仓库路径，无法推送。");
            }

            var target = string.IsNullOrWhiteSpace(remote)
                ? (string.IsNullOrWhiteSpace(branch) ? "默认远端与分支" : $"分支 {branch}")
                : $"远端 {remote}" + (string.IsNullOrWhiteSpace(branch) ? "" : $" 分支 {branch}");

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                project = config.Name,
                repoPath = config.RepoPath,
                branch,
                remote
            });

            var gate = await approvalGate.AcquireAsync(
                "git_push", "git.write", payload,
                $"在 {config.Name} 推送：{target}",
                callerId, taskId, projectId, approvalId, waitSeconds, cancellationToken);

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
                    var result = await git.PushAsync(config.RepoPath!, branch, remote, cancellationToken);

                    await AuditAsync("git_push", callerId, projectId, taskId, payload,
                        result.Ok ? "success" : "error",
                        result.Ok ? result.Output : result.Error,
                        started, gate.ApprovalId, cancellationToken);

                    return new WriteToolResult(
                        result.Ok,
                        gate.ApprovalId,
                        result.Ok ? nameof(ApprovalStatus.Executed) : nameof(ApprovalStatus.Approved),
                        result.Ok ? "已审批并推送。" : $"推送失败: {result.Error}",
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
        Guid projectId,
        string command,
        string? callerId = null,
        int waitSeconds = 60,
        Guid? taskId = null,
        Guid? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;

        try
        {
            var config = await projects.GetConfigAsync(projectId, cancellationToken);

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

            var gate = await approvalGate.AcquireAsync(
                "shell_run", "shell.write", command,
                $"在 {config.Name} 执行：{command}",
                callerId, taskId, projectId, approvalId, waitSeconds, cancellationToken);

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
                    var result = await shell.RunAsync(config.RepoPath!, command, cancellationToken);

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
                        result.Ok ? "已审批并执行。" : $"执行失败，退出码 {result.ExitCode}。",
                        output);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            await AuditAsync("shell_run", callerId, projectId, taskId, command, "error", ex.Message, started, approvalId, cancellationToken);
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
                await approvalGate.CompleteAsync(approvalId, summary, cancellationToken);
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
