using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;
using TaskStatus = Workbench.Domain.TaskStatus;
using Workbench.Infrastructure;

namespace Workbench.Web.Api;

/// <summary>
/// REST API。给 CI、webhook、外部脚本用；
/// agent 主要走 MCP，但这套接口是同一批应用服务，保证行为一致。
/// </summary>
public static class WorkbenchApi
{
    public static IEndpointRouteBuilder MapWorkbenchApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // 统一错误出口：领域异常（非法迁移、资源不存在等）转成结构化 4xx，
        // 而不是漏成 500 的 HTML 错误页 —— agent 和脚本都靠这个读懂发生了什么。
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (InvalidStatusTransitionException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    from = ex.From.ToString(),
                    to = ex.To.ToString(),
                    allowed = ex.Allowed.Select(s => s.ToString()).ToArray()
                });
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (WorkbenchException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- 项目 ----
        api.MapGet("/projects", async (IProjectService projects, CancellationToken ct) =>
            Results.Ok(await projects.ListAsync(ct)));

        api.MapPost("/projects", async (IProjectService projects, CreateProjectRequest request, CancellationToken ct) =>
        {
            var project = await projects.CreateAsync(request.Name, request.Description, ct);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        api.MapGet("/projects/{projectId:guid}", async (Guid projectId, IProjectService projects, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(projectId, ct);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        // ---- 任务 ----
        api.MapGet("/tasks", async (ITaskService tasks, Guid? projectId, string? status, string? keyword, Guid? milestoneId, string? key, string? type, CancellationToken ct) =>
        {
            TaskStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<TaskStatus>(status, ignoreCase: true, out var value))
                {
                    return Results.BadRequest($"未知状态: {status}");
                }

                parsed = value;
            }

            TaskType? parsedType = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (!TaskTypes.TryParse(type, out var value))
                {
                    return Results.BadRequest($"未知任务类型: {type}。合法值: {string.Join("、", TaskTypes.Names)}");
                }

                parsedType = value;
            }

            return Results.Ok(await tasks.ListAsync(projectId, parsed, keyword, milestoneId, key, parsedType, ct));
        });

        api.MapPost("/projects/{projectId:guid}/tasks", async (
            Guid projectId, CreateTaskRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            TaskType type = TaskType.Requirement;
            if (!string.IsNullOrWhiteSpace(request.Type) && !TaskTypes.TryParse(request.Type, out type))
            {
                return Results.BadRequest($"未知任务类型: {request.Type}。合法值: {string.Join("、", TaskTypes.Names)}");
            }

            var task = await tasks.CreateAsync(
                projectId, request.Title, request.Description, request.ParentTaskId, request.MilestoneId,
                type, request.Branch, request.Key, ct);
            return Results.Created($"/api/tasks/{task.Id}", task);
        });

        // 改类型 / 绑分支。编号不变 —— 编号已经被引用了，不能说变就变。
        api.MapPatch("/tasks/{taskId:guid}", async (
            Guid taskId, UpdateTaskRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Type) && !TaskTypes.TryParse(request.Type, out _))
            {
                return Results.BadRequest($"未知任务类型: {request.Type}。合法值: {string.Join("、", TaskTypes.Names)}");
            }

            var updated = await tasks.GetAsync(taskId, ct);
            if (updated is null)
            {
                return Results.NotFound();
            }

            if (!string.IsNullOrWhiteSpace(request.Type) && TaskTypes.TryParse(request.Type, out var type))
            {
                updated = await tasks.SetTypeAsync(taskId, type, ct);
            }

            if (request.Branch is not null)
            {
                updated = await tasks.BindBranchAsync(taskId, request.Branch, ct);
            }

            return Results.Ok(updated);
        });

        api.MapGet("/tasks/{taskId:guid}", async (Guid taskId, ITaskService tasks, CancellationToken ct) =>
        {
            var task = await tasks.GetAsync(taskId, ct);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        api.MapGet("/tasks/{taskId:guid}/context", async (
            Guid taskId, ITaskService tasks, int? eventLimit, CancellationToken ct) =>
            Results.Ok(await tasks.GetContextAsync(taskId, eventLimit ?? 20, ct)));

        api.MapPost("/tasks/{taskId:guid}/status", async (
            Guid taskId, ChangeStatusRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            if (!Enum.TryParse<TaskStatus>(request.Status, ignoreCase: true, out var target))
            {
                return Results.BadRequest($"未知状态: {request.Status}");
            }

            var source = Enum.TryParse<TaskEventSource>(request.Source, ignoreCase: true, out var parsedSource)
                ? parsedSource
                : TaskEventSource.Ci;

            var task = await tasks.ChangeStatusAsync(taskId, target, request.Note, source, request.Actor, ct);
            return Results.Ok(task);
        });

        api.MapPost("/tasks/{taskId:guid}/claim", async (
            Guid taskId, ClaimTaskRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ClaimAsync(taskId, request.AgentId, request.LeaseMinutes ?? 30, ct)));

        api.MapPost("/tasks/{taskId:guid}/plan", async (
            Guid taskId, SetPlanRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.SetPlanAsync(
                taskId, request.Background, request.Understanding, request.Breakdown,
                request.AcceptanceCriteria, request.Risks, request.Author, ct)));

        api.MapPost("/tasks/{taskId:guid}/artifacts", async (
            Guid taskId, AddArtifactRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ArtifactType>(request.Type, ignoreCase: true, out var type))
            {
                return Results.BadRequest($"未知产物类型: {request.Type}");
            }

            return Results.Ok(await tasks.AddArtifactAsync(
                taskId, type, request.Title, request.Content, request.ExternalPath,
                request.CommitSha, request.Branch, request.Author, ct));
        });

        api.MapPost("/tasks/{taskId:guid}/test-result", async (
            Guid taskId, TestResultRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ReportTestResultAsync(
                taskId, request.Total, request.Passed, request.Failures, request.Coverage, request.Author, ct)));

        api.MapPost("/tasks/{taskId:guid}/events", async (
            Guid taskId, PostEventRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.PostEventAsync(taskId, request.Message, TaskEventSource.Ci, request.Actor, ct)));

        // ---- 文档 ----
        api.MapGet("/tasks/{taskId:guid}/documents", async (
            Guid taskId, IDocumentService documents, CancellationToken ct) =>
            Results.Ok(await documents.ListAsync(taskId, ct)));

        api.MapPost("/tasks/{taskId:guid}/documents", async (
            Guid taskId, HttpRequest request, IDocumentService documents, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("需要 multipart/form-data。");
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.BadRequest("缺少 file。");
            }

            var kind = form["kind"].ToString();
            await using var stream = file.OpenReadStream();
            var document = await documents.UploadAsync(
                taskId, file.FileName, stream, string.IsNullOrWhiteSpace(kind) ? null : kind, ct);

            return Results.Created($"/api/documents/{document.Id}", document);
        });

        api.MapGet("/documents/{documentId:guid}/outline", async (
            Guid documentId, IDocumentService documents, CancellationToken ct) =>
            Results.Ok(await documents.GetOutlineAsync(documentId, ct)));

        api.MapGet("/documents/{documentId:guid}/revisions", async (
            Guid documentId, IDocumentService documents, CancellationToken ct) =>
            Results.Ok(await documents.GetRevisionsAsync(documentId, ct)));

        api.MapGet("/documents/{documentId:guid}/struck", async (
            Guid documentId, IDocumentService documents, CancellationToken ct) =>
            Results.Ok(await documents.ListStruckAsync(documentId, ct)));

        // ---- 数据库连接 ----
        api.MapGet("/projects/{projectId:guid}/db-connections", async (
            Guid projectId, IDbConnectionService connections, CancellationToken ct) =>
        {
            var list = await connections.ListAsync(projectId, ct);
            return Results.Ok(list.Select(c => new
            {
                c.Id,
                c.Name,
                c.Server,
                c.Database,
                c.UserId,
                c.IsDefault,
                HasPassword = !string.IsNullOrEmpty(c.EncryptedPassword)
            }));
        });

        api.MapPost("/projects/{projectId:guid}/db-connections", async (
            Guid projectId, CreateDbConnectionRequest request, IDbConnectionService connections, CancellationToken ct) =>
        {
            var created = await connections.CreateAsync(
                projectId, request.Name, request.Server, request.Database,
                request.UserId, request.Password, request.IsDefault, ct);

            return Results.Created($"/api/projects/{projectId}/db-connections", new
            {
                created.Id,
                created.Name,
                created.Server,
                created.Database,
                created.UserId,
                created.IsDefault,
                HasPassword = !string.IsNullOrEmpty(created.EncryptedPassword)
            });
        });

        // ---- 审批 ----
        api.MapGet("/approvals", async (
            IApprovalService approvals, string? status, Guid? projectId, CancellationToken ct) =>
        {
            ApprovalStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var value))
                {
                    return Results.BadRequest($"未知审批状态: {status}");
                }

                parsed = value;
            }

            return Results.Ok(await approvals.ListAsync(parsed, projectId, ct));
        });

        api.MapPost("/approvals/{approvalId:guid}/decide", async (
            Guid approvalId, DecideApprovalRequest request, IApprovalService approvals, CancellationToken ct) =>
        {
            try
            {
                var approval = await approvals.DecideAsync(
                    approvalId, request.Approve, request.DecidedBy, request.Reason, ct);
                return Results.Ok(approval);
            }
            catch (WorkbenchException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // ---- 项目配置 ----
        api.MapGet("/projects/{projectId:guid}/config", async (
            Guid projectId, IProjectService projects, CancellationToken ct) =>
            Results.Ok(await projects.GetConfigAsync(projectId, ct)));

        api.MapPut("/projects/{projectId:guid}/config", async (
            Guid projectId, UpdateProjectConfigRequest request, IProjectService projects, CancellationToken ct) =>
            Results.Ok(await projects.UpdateConfigAsync(
                projectId,
                request.Name, request.Description, request.RepoPath, request.RepoRemote, request.DefaultBranch,
                request.AllowedCommands, request.AgentCommand,
                request.NotificationWebhookUrl, request.NotificationFormat, ct)));

        // ---- 里程碑 ----
        api.MapGet("/projects/{projectId:guid}/milestones", async (
            Guid projectId, IMilestoneService milestones, CancellationToken ct) =>
            Results.Ok(await milestones.ListAsync(projectId, ct)));

        api.MapPost("/projects/{projectId:guid}/milestones", async (
            Guid projectId, CreateMilestoneRequest request, IMilestoneService milestones, CancellationToken ct) =>
        {
            var kind = MilestoneKind.Release;
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                if (!Enum.TryParse<MilestoneKind>(request.Kind, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest($"未知里程碑种类: {request.Kind}");
                }

                kind = parsed;
            }

            var created = await milestones.CreateAsync(
                projectId, request.Name, request.Description, request.DueAt, ct, request.ParentId, kind);
            return Results.Created($"/api/milestones/{created.Id}", created);
        });

        api.MapPut("/milestones/{milestoneId:guid}", async (
            Guid milestoneId, UpdateMilestoneRequest request, IMilestoneService milestones, CancellationToken ct) =>
        {
            MilestoneKind? kind = null;
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                if (!Enum.TryParse<MilestoneKind>(request.Kind, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest($"未知里程碑种类: {request.Kind}");
                }

                kind = parsed;
            }

            return Results.Ok(await milestones.UpdateAsync(
                milestoneId, request.Name, request.Description, request.DueAt, request.Order, ct,
                request.ParentId, kind));
        });

        api.MapDelete("/milestones/{milestoneId:guid}", async (
            Guid milestoneId, IMilestoneService milestones, CancellationToken ct) =>
        {
            await milestones.DeleteAsync(milestoneId, ct);
            return Results.NoContent();
        });

        api.MapPost("/tasks/{taskId:guid}/milestone", async (
            Guid taskId, AssignMilestoneRequest request, IMilestoneService milestones, ITaskService tasks, CancellationToken ct) =>
        {
            await milestones.AssignTaskAsync(taskId, request.MilestoneId, ct);
            var task = await tasks.GetAsync(taskId, ct);
            return task is null ? Results.NotFound() : Results.Ok(task);
        });

        // ---- 度量 ----
        api.MapGet("/metrics", async (IMetricsService metrics, CancellationToken ct) =>
            Results.Ok(await metrics.GetAllAsync(ct)));

        api.MapGet("/projects/{projectId:guid}/metrics", async (
            Guid projectId, IMetricsService metrics, CancellationToken ct) =>
            Results.Ok(await metrics.GetProjectMetricsAsync(projectId, ct)));

        // ---- 启动外部 agent ----
        api.MapPost("/tasks/{taskId:guid}/launch-agent", async (
            Guid taskId, IAgentLauncher launcher, CancellationToken ct) =>
        {
            var result = await launcher.LaunchAsync(taskId, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(new { error = result.Error });
        });

        // ---- 工作区搬迁（换机）----
        api.MapPost("/workspace/export", async (
            ExportWorkspaceRequest request, IWorkspaceArchiver archiver, CancellationToken ct) =>
        {
            var result = await archiver.ExportAsync(request.TargetDirectory, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(new { error = result.Message });
        });

        api.MapPost("/workspace/import", async (
            ImportWorkspaceRequest request, IWorkspaceArchiver archiver, CancellationToken ct) =>
        {
            var result = await archiver.ImportAsync(request.SourceDirectory, request.TargetHomeDirectory, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(new { error = result.Message });
        });

        // ---- 评审 ----
        api.MapPost("/tasks/{taskId:guid}/review", async (
            Guid taskId, StartReviewRequest request, IReviewService reviews, CancellationToken ct) =>
            Results.Ok(await reviews.StartAsync(taskId, request.Reviewer, ct)));

        api.MapGet("/tasks/{taskId:guid}/review", async (
            Guid taskId, IReviewService reviews, CancellationToken ct) =>
        {
            var current = await reviews.GetCurrentAsync(taskId, ct);
            return current is null ? Results.NotFound() : Results.Ok(current);
        });

        api.MapGet("/tasks/{taskId:guid}/reviews", async (
            Guid taskId, IReviewService reviews, CancellationToken ct) =>
            Results.Ok(await reviews.ListAsync(taskId, ct)));

        api.MapPost("/tasks/{taskId:guid}/review/{dimension}", async (
            Guid taskId, string dimension, SubmitReviewRequest request, IReviewService reviews, CancellationToken ct) =>
            Results.Ok(await reviews.SubmitAsync(
                taskId, dimension, request.Passed, request.Findings, request.Evidence,
                request.Recommendation, request.Reviewer, request.Round, ct)));

        // ---- MCP 工具开关 ----
        api.MapGet("/mcp/tools", async (IMcpToolService tools, CancellationToken ct) =>
            Results.Ok(await tools.ListAsync(ct)));

        api.MapPut("/mcp/tools/{toolName}", async (
            string toolName, SetMcpToolRequest request, IMcpToolService tools, CancellationToken ct) =>
            Results.Ok(await tools.SetEnabledAsync(toolName, request.Enabled, ct)));

        api.MapPost("/mcp/tools/reset", async (IMcpToolService tools, CancellationToken ct) =>
            Results.Ok(await tools.ResetAsync(ct)));

        // ---- 工具调用审计 ----
        // 审计表一直在写，但没有查询入口 —— 出了事没法复盘。补上。
        api.MapGet("/audit", async (
            IToolAuditService audit, string? toolName, Guid? taskId, Guid? projectId, int? limit, CancellationToken ct) =>
        {
            var take = limit ?? 100;
            if (take is < 1 or > 1000)
            {
                return Results.BadRequest("limit 必须在 1~1000 之间。");
            }

            return Results.Ok(await audit.ListAsync(toolName, taskId, projectId, take, ct));
        });

        return app;
    }
}

public sealed record CreateProjectRequest(string Name, string? Description);
public sealed record CreateTaskRequest(
    string Title, string? Description, Guid? ParentTaskId, Guid? MilestoneId = null,
    string? Type = null, string? Branch = null, string? Key = null);

/// <summary>改任务。Type / Branch 传了才改；Branch 传空串表示解绑。</summary>
public sealed record UpdateTaskRequest(string? Type = null, string? Branch = null);
public sealed record ChangeStatusRequest(string Status, string? Note, string? Actor, string? Source);
public sealed record ClaimTaskRequest(string AgentId, int? LeaseMinutes);
public sealed record SetPlanRequest(
    string? Background, string? Understanding, string? Breakdown,
    string? AcceptanceCriteria, string? Risks, string? Author);
public sealed record AddArtifactRequest(
    string Title, string Type, string? Content, string? ExternalPath,
    string? CommitSha, string? Branch, string? Author);
public sealed record TestResultRequest(
    int Total, int Passed, string? Failures, double? Coverage, string? Author);
public sealed record PostEventRequest(string Message, string? Actor);
public sealed record DecideApprovalRequest(bool Approve, string? DecidedBy, string? Reason);
public sealed record CreateDbConnectionRequest(
    string Name, string Server, string Database, string? UserId, string? Password, bool IsDefault = true);

public sealed record UpdateProjectConfigRequest(
    string? Name,
    string? Description,
    string? RepoPath,
    string? RepoRemote,
    string? DefaultBranch,
    IReadOnlyList<string>? AllowedCommands,
    string? AgentCommand,
    string? NotificationWebhookUrl,
    string? NotificationFormat);

public sealed record CreateMilestoneRequest(
    string Name, string? Description, DateTimeOffset? DueAt,
    Guid? ParentId = null, string? Kind = null);

public sealed record UpdateMilestoneRequest(
    string? Name, string? Description, DateTimeOffset? DueAt, int? Order,
    Guid? ParentId = null, string? Kind = null);

public sealed record AssignMilestoneRequest(Guid? MilestoneId);

public sealed record ExportWorkspaceRequest(string TargetDirectory);

public sealed record ImportWorkspaceRequest(string SourceDirectory, string TargetHomeDirectory);

public sealed record SetMcpToolRequest(bool Enabled);

public sealed record StartReviewRequest(string? Reviewer);

public sealed record SubmitReviewRequest(
    bool Passed, string? Findings, string? Evidence, string? Recommendation, string? Reviewer, int? Round);
