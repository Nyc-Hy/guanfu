using Microsoft.Extensions.Options;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;
using TaskStatus = Guanfu.Domain.TaskStatus;
using Guanfu.Infrastructure;

namespace Guanfu.Web.Api;

/// <summary>
/// REST API。给 CI、webhook、外部脚本用；
/// agent 主要走 MCP，但这套接口是同一批应用服务，保证行为一致。
/// </summary>
public static class GuanfuApi
{
    /// <summary>搜索时最多扫多少个任务（找文档命中用）。</summary>
    private const int SearchTaskScanLimit = 200;

    /// <summary>搜索时最多解析多少篇文档。文档搜索要真去解析内容。</summary>
    private const int SearchDocScanLimit = 200;

    public static IEndpointRouteBuilder MapGuanfuApi(this IEndpointRouteBuilder app)
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
            catch (GuanfuException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ---- 项目 ----
        api.MapGet("/projects", async (IProjectService projects, CancellationToken ct) =>
            Results.Ok(await projects.ListAsync(ct)));

        api.MapPost("/projects", async (IProjectService projects, CreateProjectRequest request, CancellationToken ct) =>
        {
            var project = await projects.CreateAsync(request.Name, request.Description, ct, request.RepoPath);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        api.MapGet("/projects/{projectId:guid}", async (Guid projectId, IProjectService projects, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(projectId, ct);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        // ---- 任务 ----
        api.MapGet("/tasks", async (
            ITaskService tasks, Guid? projectId, string? status, string? keyword, Guid? milestoneId,
            string? key, string? type, int? limit, int? offset, bool? includeArchived,
            CancellationToken ct) =>
        {
            TaskStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!UiText.TryParseStatus(status, out var value))
                {
                    return Results.BadRequest($"未知状态「{status}」。合法值: {UiText.StatusNames()}");
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

            // limit/offset 给 agent 用：不带就全量返回，任务一多就把上下文撑爆了。
            // 上限压在 500，防止有人拿它当导出接口用。
            if (limit is < 1 or > 500)
            {
                return Results.BadRequest("limit 必须在 1~500 之间。");
            }

            if (offset is < 0)
            {
                return Results.BadRequest("offset 不能为负。");
            }

            return Results.Ok(await tasks.ListAsync(
                projectId, parsed, keyword, milestoneId, key, parsedType, ct,
                limit, offset, includeArchived ?? false));
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
                type, request.Key, ct);
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

        // 交接包：接手一个别人放下的活时，一次拿全（含上一个是谁、为什么交出来）。
        api.MapGet("/tasks/{taskId:guid}/handoff-pack", async (
            Guid taskId, IHandoffService handoff, CancellationToken ct) =>
            Results.Ok(await handoff.GetAsync(taskId, ct)));

        // 事件流水：账本不能只写不看。倒序（新的在前），limit 上限 500。
        api.MapGet("/tasks/{taskId:guid}/events", async (
            Guid taskId, ITaskService tasks, int? limit, CancellationToken ct) =>
        {
            var take = limit ?? 50;
            if (take is < 1 or > 500)
            {
                return Results.BadRequest("limit 必须在 1~500 之间。");
            }

            var events = await tasks.ListEventsAsync(taskId, take, ct);
            return Results.Ok(events);
        });

        // 会话：谁在什么时候接的这个任务。工作目录、agent 标识都在里头。
        api.MapGet("/tasks/{taskId:guid}/sessions", async (
            Guid taskId, IAgentSessionService sessions, CancellationToken ct) =>
            Results.Ok(await sessions.ListByTaskAsync(taskId, ct)));

        // 数据库调用记录：SQL 原文、目标库、目标表摊成列，能筛能查。
        api.MapGet("/db-calls", async (
            IDbCallService dbCalls,
            Guid? projectId,
            Guid? connectionId,
            string? database,
            string? table,
            string? kind,
            Guid? taskId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            int? offset,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var (rows, total) = await dbCalls.ListAsync(
                projectId, connectionId, database, table, kind, taskId, from, to, take, skip, ct);

            return Results.Ok(new { rows, total, limit = take, offset = skip });
        });

        // 归档 / 取消归档。归档不是删除：流水、产物、评审全都留着。
        // 排期干预：人把活排进计划（或移出去）。**要人工凭证** ——
        // 排期是人的决定，agent 不该自己改「先干哪个」。
        api.MapPost("/tasks/{taskId:guid}/pin", async (
            Guid taskId, PinTaskRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.SetPinnedAsync(taskId, request.Pinned, ct)));

        api.MapPost("/tasks/{taskId:guid}/archive", async (
            Guid taskId, ArchiveTaskRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.SetArchivedAsync(taskId, request.Archived, ct)));

        // 分派：负责人 / 优先级 / 截止日 / 权重
        api.MapPost("/tasks/{taskId:guid}/assign", async (
            Guid taskId, AssignTaskRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            TaskPriority? priority = null;
            if (!string.IsNullOrWhiteSpace(request.Priority))
            {
                if (!Enum.TryParse<TaskPriority>(request.Priority, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest($"未知优先级: {request.Priority}");
                }

                priority = parsed;
            }

            return Results.Ok(await tasks.SetAssignmentAsync(
                taskId, request.Assignee, priority, request.DueAt, request.Weight, ct,
                request.AssigneeMemberId, request.ClearAssigneeMember));
        });

        // ---- 任务依赖 ----
        api.MapGet("/tasks/{taskId:guid}/dependencies", async (
            Guid taskId, ITaskDependencyService dependencies, CancellationToken ct) =>
            Results.Ok(await dependencies.ListAsync(taskId, ct)));

        api.MapPost("/tasks/{taskId:guid}/dependencies", async (
            Guid taskId, AddDependencyRequest request, ITaskDependencyService dependencies, CancellationToken ct) =>
        {
            var type = DependencyType.BlockedBy;
            if (!string.IsNullOrWhiteSpace(request.Type))
            {
                if (!Enum.TryParse<DependencyType>(request.Type, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest($"未知依赖方向: {request.Type}");
                }

                type = parsed;
            }

            var created = await dependencies.AddAsync(
                taskId, request.DependsOnTaskId, type, request.Note, ct);

            return Results.Created($"/api/tasks/{taskId}/dependencies", created);
        });

        api.MapDelete("/tasks/{taskId:guid}/dependencies/{dependsOnTaskId:guid}", async (
            Guid taskId, Guid dependsOnTaskId, ITaskDependencyService dependencies, CancellationToken ct) =>
        {
            await dependencies.RemoveAsync(taskId, dependsOnTaskId, ct);
            return Results.NoContent();
        });

        // ---- 导出 ----
        api.MapGet("/tasks/{taskId:guid}/export/events", async (
            Guid taskId, IExportService exports, string? format, CancellationToken ct) =>
        {
            var text = await exports.ExportTaskEventsAsync(taskId, format ?? "markdown", ct);
            return Results.Content(text, "text/markdown; charset=utf-8");
        });

        api.MapGet("/tasks/{taskId:guid}/export/review", async (
            Guid taskId, IExportService exports, int? round, string? format, CancellationToken ct) =>
        {
            var text = await exports.ExportReviewReportAsync(taskId, round, format ?? "markdown", ct);
            return Results.Content(text, "text/markdown; charset=utf-8");
        });

        api.MapPost("/tasks/{taskId:guid}/status", async (
            Guid taskId, ChangeStatusRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            if (!UiText.TryParseStatus(request.Status, out var target))
            {
                return Results.BadRequest($"未知状态「{request.Status}」。合法值: {UiText.StatusNames()}");
            }

            var source = Enum.TryParse<TaskEventSource>(request.Source, ignoreCase: true, out var parsedSource)
                ? parsedSource
                : TaskEventSource.Ci;

            var task = await tasks.ChangeStatusAsync(taskId, target, request.Note, source, request.Actor, ct);
            return Results.Ok(task);
        });

        api.MapPost("/tasks/{taskId:guid}/claim", async (
            Guid taskId, ClaimTaskRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            // 返回形状不变（CLI 依赖扁平的任务摘要）；
            // 协作上下文（上一个是谁、为什么交）走事件流与 MCP 的领取结果。
            var outcome = await tasks.ClaimAsync(
                taskId, request.AgentId, request.LeaseMinutes ?? 30, ct, request.Note);

            return Results.Ok(outcome.Task);
        });

        api.MapPost("/tasks/{taskId:guid}/plan", async (
            Guid taskId, SetPlanRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.SetPlanAsync(
                taskId, request.Background, request.Understanding, request.Breakdown,
                request.AcceptanceCriteria, request.Risks, request.Author, request.OccurredAt, ct)));

        api.MapPost("/tasks/{taskId:guid}/artifacts", async (
            Guid taskId, AddArtifactRequest request, ITaskService tasks, CancellationToken ct) =>
        {
            // 产物类型认枚举名（SqlScript）也认中文说法（SQL 脚本）——
            // 人敲命令用中文，agent 用枚举名，两种都得能落地。
            if (!UiText.TryParseArtifactType(request.Type, out var type))
            {
                return Results.BadRequest(
                    $"未知产物类型: {request.Type}。可用类型：{UiText.ArtifactTypeNames()}");
            }

            return Results.Ok(await tasks.AddArtifactAsync(
                taskId, type, request.Title, request.Content, request.ExternalPath,
                request.CommitSha, request.Branch, request.Author, request.OccurredAt, ct));
        });

        // 改产物：人会改 AI 写出来的脚本。改完留版本（历史在 revisions 里）。
        api.MapPut("/tasks/{taskId:guid}/artifacts/{artifactId:guid}", async (
            Guid taskId, Guid artifactId, UpdateArtifactRequest request, IArtifactService artifacts,
            CancellationToken ct) =>
            Results.Ok(await artifacts.UpdateAsync(
                taskId, artifactId, request.Title, request.Content, request.Editor, request.Note, ct)));

        api.MapGet("/tasks/{taskId:guid}/artifacts/{artifactId:guid}/revisions", async (
            Guid taskId, Guid artifactId, IArtifactService artifacts, CancellationToken ct) =>
            Results.Ok(await artifacts.ListRevisionsAsync(taskId, artifactId, ct)));

        api.MapPost("/tasks/{taskId:guid}/test-result", async (
            Guid taskId, TestResultRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ReportTestResultAsync(
                taskId, request.Total, request.Passed, request.Failures, request.Coverage, request.Author,
                request.OccurredAt, ct)));

        api.MapPost("/tasks/{taskId:guid}/events", async (
            Guid taskId, PostEventRequest request, ITaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.PostEventAsync(
                taskId, request.Message, TaskEventSource.Ci, request.Actor, request.OccurredAt, ct)));

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

        // 批量导入：一次给多个文件，一个不行不牵连其它的。
        // 需求文档常常是「PRD + 接口文档 + 用例」一起来的，
        // 一个一个传就得让人守着上传按钮。
        api.MapPost("/tasks/{taskId:guid}/documents/batch", async (
            Guid taskId, HttpRequest request, IDocumentService documents, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("需要 multipart/form-data。");
            }

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest("缺少文件。");
            }

            var defaultKind = form["kind"].ToString();
            var items = new List<DocumentUploadInput>(form.Files.Count);
            var streams = new List<Stream>(form.Files.Count);

            try
            {
                foreach (var file in form.Files)
                {
                    // 单个文件可以带自己的类别（字段名与文件名同名），没带就用全局的
                    var kind = form[file.Name].ToString();
                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        kind = defaultKind;
                    }

                    var stream = file.OpenReadStream();
                    streams.Add(stream);
                    items.Add(new DocumentUploadInput(
                        file.FileName,
                        stream,
                        string.IsNullOrWhiteSpace(kind) ? null : kind));
                }

                var result = await documents.UploadBatchAsync(taskId, items, ct);

                return Results.Ok(new
                {
                    uploaded = result.Uploaded,
                    failed = result.Failed
                });
            }
            finally
            {
                foreach (var stream in streams)
                {
                    await stream.DisposeAsync();
                }
            }
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
            Guid approvalId, DecideApprovalRequest request, IApprovalService approvals, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var approval = await approvals.DecideAsync(
                    approvalId, request.Approve, request.DecidedBy, request.Reason,
                    ct, decidedByCredential: CredentialFingerprint(http));
                return Results.Ok(approval);
            }
            catch (GuanfuException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // 人工撤销（Pending / Approved → Rejected）。「批错了但还没执行」的后悔药。
        // 敏感端点：撤销改变的是「哪条写操作能执行」，与裁决同权。
        api.MapPost("/approvals/{approvalId:guid}/revoke", async (
            Guid approvalId, RevokeApprovalRequest? request, IApprovalService approvals, CancellationToken ct) =>
            Results.Ok(await approvals.RevokeAsync(approvalId, request?.Reason, ct)));

        // 批量批准。一条一条点太慢了 —— 人工审核通常是「这批都同意」。
        // 部分失败不吞掉：哪些批了、哪些没批，逐条回。
        api.MapPost("/approvals/batch-decide", async (
            BatchDecideApprovalRequest request, IApprovalService approvals, HttpContext http, CancellationToken ct) =>
        {
            if (request.ApprovalIds.Count == 0)
            {
                return Results.BadRequest("审批 id 列表不能为空。");
            }

            if (request.ApprovalIds.Count > 200)
            {
                return Results.BadRequest("一次最多批 200 条。");
            }

            var results = new List<BatchDecisionResult>();
            var failed = 0;

            foreach (var id in request.ApprovalIds.Distinct())
            {
                try
                {
                    var approval = await approvals.DecideAsync(
                        id, request.Approve, request.DecidedBy, request.Reason,
                        ct, decidedByCredential: CredentialFingerprint(http));
                    results.Add(new BatchDecisionResult(id, true, approval.Status, null));
                }
                catch (GuanfuException ex)
                {
                    failed++;
                    results.Add(new BatchDecisionResult(id, false, null, ex.Message));
                }
            }

            return Results.Ok(new { total = results.Count, failed, results });
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
                request.NotificationWebhookUrl, request.NotificationFormat, ct,
                request.MaxConcurrentAgents, request.MaxToolCallsPerTask)));

        // ---- 项目级规约 ----
        // 读：agent 也要读（跟 MCP 工具同一条信息），所以开放。
        // 写：**要人工凭证** —— 规矩是人的决定，agent 不该自己改「什么算做完」。
        api.MapGet("/projects/{projectId:guid}/spec", async (
            Guid projectId, IProjectSpecService specs, CancellationToken ct) =>
            Results.Ok(await specs.GetAsync(projectId, ct)));

        // 里程碑级规约：叠加在项目级之上。改它同样要人工凭证（规矩是人的决定）。
        api.MapGet("/milestones/{milestoneId:guid}/spec", async (
            Guid milestoneId, IProjectSpecService specs, IMilestoneService milestones, CancellationToken ct) =>
        {
            var milestone = await milestones.GetAsync(milestoneId, ct);
            return Results.Ok(await specs.GetForMilestoneAsync(milestone.ProjectId, milestoneId, ct));
        });

        api.MapPut("/milestones/{milestoneId:guid}/spec", async (
            Guid milestoneId, UpdateProjectSpecRequest request, IProjectSpecService specs,
            IMilestoneService milestones, CancellationToken ct) =>
        {
            var milestone = await milestones.GetAsync(milestoneId, ct);
            var input = new ProjectSpecInput(
                request.Purpose,
                SplitLines(request.DefinitionOfDone),
                request.CommitConvention,
                request.TestCommand,
                request.BranchConvention,
                request.Guardrails);

            return Results.Ok(await specs.UpdateForMilestoneAsync(
                milestone.ProjectId, milestoneId, input, "human", ct));
        });

        // 工作区标准规约：所有项目通用的底线（公司/团队的规矩）。
        // 读：开放（agent 也要读）。写：要人工凭证 —— 规矩是人的决定。
        api.MapGet("/workspaces/{workspaceId:guid}/spec", async (
            Guid workspaceId, IProjectSpecService specs, CancellationToken ct) =>
            Results.Ok(await specs.GetForWorkspaceAsync(workspaceId, ct)));

        api.MapPut("/workspaces/{workspaceId:guid}/spec", async (
            Guid workspaceId, UpdateProjectSpecRequest request, IProjectSpecService specs, CancellationToken ct) =>
        {
            var input = new ProjectSpecInput(
                request.Purpose,
                SplitLines(request.DefinitionOfDone),
                request.CommitConvention,
                request.TestCommand,
                request.BranchConvention,
                request.Guardrails,
                request.MaxConcurrentAgents ?? 0);

            return Results.Ok(await specs.UpdateForWorkspaceAsync(workspaceId, input, "human", ct));
        });

        api.MapPut("/projects/{projectId:guid}/spec", async (
            Guid projectId, UpdateProjectSpecRequest request, IProjectSpecService specs, CancellationToken ct) =>
        {
            var input = new ProjectSpecInput(
                request.Purpose,
                SplitLines(request.DefinitionOfDone),
                request.CommitConvention,
                request.TestCommand,
                request.BranchConvention,
                request.Guardrails);

            return Results.Ok(await specs.UpdateAsync(projectId, input, "human", ct));
        });

        // ---- 用量与开销 ----
        api.MapGet("/tasks/{taskId:guid}/usage", async (
            Guid taskId, ITaskUsageService usage, CancellationToken ct) =>
            Results.Ok(await usage.GetTaskUsageAsync(taskId, ct)));

        api.MapGet("/projects/{projectId:guid}/usage", async (
            Guid projectId, ITaskUsageService usage, int? limit, CancellationToken ct) =>
            Results.Ok(await usage.GetProjectUsageAsync(projectId, limit ?? 20, ct)));

        // ---- 调度视图 ----
        // 读：agent 与人都要看（选活、看板）。开放。
        api.MapGet("/projects/{projectId:guid}/suggestions", async (
            Guid projectId, IDispatchService dispatch, int? limit, CancellationToken ct) =>
            Results.Ok(await dispatch.SuggestAsync(projectId, limit ?? 10, null, ct)));

        api.MapGet("/projects/{projectId:guid}/dispatch", async (
            Guid projectId, IDispatchService dispatch, int? limit, CancellationToken ct) =>
            Results.Ok(await dispatch.GetAsync(projectId, limit ?? 20, ct)));

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
                request.ParentId, kind, request.ClearParent, request.ClearDue));
        });

        // 验收是**人的签字**：版本做完了由人确认。
        // 要人工凭证 —— agent 不该自己宣布「这个版本做完了」。
        api.MapPost("/milestones/{milestoneId:guid}/accept", async (
            Guid milestoneId, AcceptMilestoneRequest? request, IMilestoneService milestones, CancellationToken ct) =>
            Results.Ok(await milestones.AcceptAsync(milestoneId, request?.AcceptedBy, ct)));

        api.MapPost("/milestones/{milestoneId:guid}/unaccept", async (
            Guid milestoneId, IMilestoneService milestones, CancellationToken ct) =>
            Results.Ok(await milestones.UnacceptAsync(milestoneId, ct)));

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

        // ---- 交付打包 ----
        // 候选池：这个迭代版本下的文档与产物。人勾选要发出去哪些，打成一个 zip + 清单。
        api.MapGet("/milestones/{milestoneId:guid}/delivery/candidates", async (
            Guid milestoneId, IDeliveryPackageService delivery, CancellationToken ct) =>
            Results.Ok(await delivery.ListCandidatesAsync(milestoneId, ct)));

        api.MapPost("/milestones/{milestoneId:guid}/delivery/packages", async (
            Guid milestoneId, CreateDeliveryPackageRequest request, IDeliveryPackageService delivery,
            CancellationToken ct) =>
        {
            var package = await delivery.CreateAsync(
                milestoneId, request.ItemIds, request.CreatedBy, ct);

            return Results.Created($"/api/delivery-packages/{package.Id}", package);
        });

        api.MapGet("/milestones/{milestoneId:guid}/delivery/packages", async (
            Guid milestoneId, IDeliveryPackageService delivery, CancellationToken ct) =>
            Results.Ok(await delivery.ListAsync(milestoneId, ct)));

        api.MapGet("/delivery-packages/{packageId:guid}", async (
            Guid packageId, IDeliveryPackageService delivery, CancellationToken ct) =>
            Results.Ok(await delivery.GetAsync(packageId, ct)));

        api.MapGet("/delivery-packages/{packageId:guid}/download", async (
            Guid packageId, IDeliveryPackageService delivery, CancellationToken ct) =>
        {
            var package = await delivery.GetAsync(packageId, ct);
            var path = await delivery.ResolvePathAsync(package, ct);

            if (!File.Exists(path))
            {
                return Results.NotFound(new { error = "交付包文件不在了（数据目录被清理过？）。" });
            }

            return Results.File(path, "application/zip", package.FileName);
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

        // ---- 表级规则 ----
        // 表级规则以前在库里却没人读它，语义等于不存在。补上入口，
        // 免审批写的表现在真的管事了。
        api.MapGet("/db-connections/{connectionId:guid}/rules", async (
            Guid connectionId, IDbConnectionService connections, CancellationToken ct) =>
            Results.Ok(await connections.ListRulesAsync(connectionId, ct)));

        api.MapPost("/db-connections/{connectionId:guid}/rules", async (
            Guid connectionId, CreateDbTableRuleRequest request, IDbConnectionService connections, CancellationToken ct) =>
        {
            var created = await connections.AddRuleAsync(
                connectionId, request.SchemaName, request.TableName,
                request.AllowRead, request.WriteRequiresApproval, request.WriteForbidden, ct);

            return Results.Created($"/api/db-connections/{connectionId}/rules/{created.Id}", created);
        });

        api.MapDelete("/db-connections/{connectionId:guid}/rules/{ruleId:guid}", async (
            Guid connectionId, Guid ruleId, IDbConnectionService connections, CancellationToken ct) =>
        {
            await connections.DeleteRuleAsync(connectionId, ruleId, ct);
            return Results.NoContent();
        });

        // 连接探活：拿存好的密码试连一次。敏感端点 —— 密码只有人配过，
        // 探活结果还会暴露内网可达性，不能让 agent 自己随便试。
        api.MapPost("/db-connections/{connectionId:guid}/test", async (
            Guid connectionId, IDbConnectionService connections, CancellationToken ct) =>
        {
            var (ok, error) = await connections.TestAsync(connectionId, ct);
            return Results.Ok(new { ok, error });
        });

        // ---- 全局搜索 ----
        // 任务按「归一化编号 + 标题/描述」两路找（归一化在服务层内存里做，
        // 人嘴里的 req001 和库里的 REQ-001 是同一个编号）；项目按名；
        // 文档走 DocumentService.SearchAsync 逐篇搜内容，文件名命中也算。
        //
        // **每一路都有上限**：文档搜索要真去解析文档内容，
        // 不限量就是「全库解析」—— 任务一多这个接口就会把服务拖垮。
        // 界面的搜索页早就有限量了（Search.razor），REST 这条也得有。
        api.MapGet("/search", async (
            ITaskService tasks, IProjectService projects, IDocumentService documents,
            string? q, int? limit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest("q 不能为空。");
            }

            var take = limit ?? 20;
            if (take is < 1 or > 100)
            {
                return Results.BadRequest("limit 必须在 1~100 之间。");
            }

            var keyword = q.Trim();

            var byKeyword = await tasks.ListAsync(keyword: keyword, limit: take, cancellationToken: ct);
            var byKey = await tasks.ListAsync(key: keyword, limit: take, cancellationToken: ct);
            var taskHits = byKeyword.Concat(byKey)
                .GroupBy(t => t.Id).Select(g => g.First())
                .Take(take).ToList();

            var projectHits = (await projects.ListAsync(ct))
                .Where(p => p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Take(take).ToList();

            var documentHits = new List<object>();
            var scannedTasks = 0;
            var scannedDocs = 0;
            var allTasks = await tasks.ListAsync(limit: SearchTaskScanLimit, includeArchived: true, cancellationToken: ct);
            foreach (var task in allTasks)
            {
                if (documentHits.Count >= take || scannedTasks >= SearchTaskScanLimit)
                {
                    break;
                }

                scannedTasks++;

                foreach (var doc in await documents.ListAsync(task.Id, ct))
                {
                    if (documentHits.Count >= take || scannedDocs >= SearchDocScanLimit)
                    {
                        break;
                    }

                    scannedDocs++;

                    var hits = await documents.SearchAsync(doc.Id, keyword, ct);
                    var nameMatch = doc.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                    if (hits.Count > 0 || nameMatch)
                    {
                        documentHits.Add(new
                        {
                            id = doc.Id,
                            fileName = doc.FileName,
                            taskId = task.Id,
                            kind = doc.Kind,
                            hits
                        });
                    }
                }
            }

            return Results.Ok(new { tasks = taskHits, projects = projectHits, documents = documentHits });
        });

        // ---- 人工凭证 ----
        // 只有「直接从本机发出来」的请求才能拿到 token：内网远程攻击者拿不到，
        // 也就没法自己批准自己的写操作。顺带把 Cookie 种下 ——
        // UI 首次引导只要调这一个接口就够了。
        api.MapGet("/human-token", (HttpContext context, HumanTokenAccessor tokens) =>
        {
            if (!tokens.IsDirectLoopbackRequest(context))
            {
                return Results.Json(
                    new { ok = false, error = "人工凭证只能在观复本机获取。请从启动服务的控制台复制 token。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            context.Response.Cookies.Append(HumanTokenPolicy.CookieName, tokens.Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Secure = context.Request.IsHttps
            });

            return Results.Ok(new
            {
                ok = true,
                token = tokens.Token,
                headerName = HumanTokenPolicy.HeaderName,
                cookieName = HumanTokenPolicy.CookieName
            });
        });

        // ---- 工作区搬迁（换机）----
        api.MapPost("/workspace/export", async (
            ExportWorkspaceRequest request, IWorkspaceArchiver archiver, IOptions<GuanfuOptions> options, CancellationToken ct) =>
        {
            // 入口侧先挡一道：导出目录等于 数据目录 会把正在用的库删掉，
            // 落在文档目录里会把文档拷进它自己（无限递归）。
            var invalid = WorkspacePathGuard.ValidateExportTarget(request.TargetDirectory, options.Value);
            if (invalid is not null)
            {
                return Results.BadRequest(new { error = invalid });
            }

            var result = await archiver.ExportAsync(request.TargetDirectory, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(new { error = result.Message });
        });

        api.MapPost("/workspace/import", async (
            ImportWorkspaceRequest request, IWorkspaceArchiver archiver, IOptions<GuanfuOptions> options, CancellationToken ct) =>
        {
            // 导入到正在跑的 数据目录 = 把正在用的库覆盖掉
            var invalid = WorkspacePathGuard.ValidateImportTarget(request.TargetHomeDirectory, options.Value);
            if (invalid is not null)
            {
                return Results.BadRequest(new { error = invalid });
            }

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
                request.Recommendation, request.Reviewer, request.Round, ct, request.Skipped)));

        // 规约逐条判定：评审结论的一种，不是状态迁移 —— 不需要人工凭证
        //（跟逐维度回写评审结论同一性质）。
        api.MapPost("/tasks/{taskId:guid}/review/spec-verdict", async (
            Guid taskId, SubmitSpecVerdictRequest request, IReviewService reviews, CancellationToken ct) =>
            Results.Ok(await reviews.SubmitSpecVerdictAsync(
                taskId, request.Item, request.Satisfied, request.Note, request.Reviewer,
                request.Round, ct, request.Skipped)));

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
        // ---- 访问凭证 ----
        // 「给人发钥匙」跟裁决审批同权：拿到钥匙的人能顶替身份操作。
        // 所以这三个端点要人工凭证（界面上那页也是同一道门）。
        api.MapGet("/credentials", async (ICredentialService credentials, CancellationToken ct) =>
            Results.Ok(await credentials.ListAsync(ct)));

        api.MapPost("/credentials", async (
            IssueCredentialRequest request, ICredentialService credentials, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CredentialKind>(request.Kind, ignoreCase: true, out var kind))
            {
                return Results.BadRequest($"未知凭证种类: {request.Kind}");
            }

            var issued = await credentials.IssueAsync(
                kind,
                request.Subject,
                request.ProjectScope,
                request.Note,
                request.ExpiresAt,
                "owner",
                request.MemberId);

            return Results.Created($"/api/credentials/{issued.Id}", issued);
        });

        api.MapDelete("/credentials/{credentialId:guid}", async (
            Guid credentialId, ICredentialService credentials, CancellationToken ct) =>
        {
            var revoked = await credentials.RevokeAsync(credentialId, ct);
            return revoked ? Results.NoContent() : Results.NotFound(new { error = "凭证不存在或已吊销。" });
        });

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

        // 验哈希链：审计有没有被人在库文件层面改过。改一条、删一条，链都会断。
        api.MapGet("/audit/verify", async (IToolAuditService audit, CancellationToken ct) =>
            Results.Ok(await audit.VerifyChainAsync(ct)));

        // 链头归档：把链头写到观复的数据目录之外的文件里。
        // 库里自己算的链挡不住「连哈希一起重算」 —— 归档到外面才多一道对上的机会。
        api.MapGet("/audit/archive", async (IAuditChainArchiveService archive, CancellationToken ct) =>
            Results.Ok(await archive.VerifyAsync(ct)));

        // 归档是往观复外面写文件，归档位置由部署方决定 ——
        // 跟改项目配置同权，要人工在场。
        api.MapPost("/audit/archive", async (IAuditChainArchiveService archive, CancellationToken ct) =>
        {
            var result = await archive.ArchiveAsync(ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        return app;
    }

    /// <summary>
    /// 人工凭证的指纹（哈希前 12 位）。
    ///
    /// 共享 token 的现状下系统认不出真人 —— 但记下凭证指纹，好歹能区分
    /// 「同一个操作员」和「换了个人在操作」，而且不用把 token 明文写进审计。
    /// </summary>
    /// <summary>多行文本拆成条目（空行丢掉）。</summary>
    private static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string? CredentialFingerprint(HttpContext http)
    {
        var token = http.Request.Headers[HumanTokenPolicy.HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(token))
        {
            token = http.Request.Cookies[HumanTokenPolicy.CookieName] ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return HumanTokenPolicy.Fingerprint(token);
    }
}

public sealed record CreateProjectRequest(string Name, string? Description, string? RepoPath = null);
public sealed record CreateTaskRequest(
    string Title, string? Description, Guid? ParentTaskId, Guid? MilestoneId = null,
    string? Type = null, string? Key = null);

/// <summary>改任务。Type 传了才改。</summary>
public sealed record UpdateTaskRequest(string? Type = null);
public sealed record ChangeStatusRequest(string Status, string? Note, string? Actor, string? Source);
/// <summary>领任务。</summary>
/// <summary>签发一把凭证。</summary>
public sealed record IssueCredentialRequest(
    string Kind,
    string Subject,
    IReadOnlyList<Guid>? ProjectScope = null,
    string? Note = null,
    DateTimeOffset? ExpiresAt = null,
    /// <summary>归属的成员（agent 凭证背后站着的那个人）。</summary>
    Guid? MemberId = null);

/// <summary>领任务。</summary>
public sealed record ClaimTaskRequest(
    string AgentId,
    int? LeaseMinutes,
    /// <summary>领活时留的上下文（为什么接这个活）。</summary>
    string? Note = null);
/// <summary>
/// 回写计划。OccurredAt 是**实际发生时间** —— 离线补报时给「真的那会儿」，
/// 不给就是「现在」。观复会拒绝未来的时间。
/// </summary>
public sealed record SetPlanRequest(
    string? Background, string? Understanding, string? Breakdown,
    string? AcceptanceCriteria, string? Risks, string? Author,
    DateTimeOffset? OccurredAt = null);

/// <summary>登记产物。OccurredAt 同上（实际发生时间）。</summary>
public sealed record AddArtifactRequest(
    string Title, string Type, string? Content, string? ExternalPath,
    string? CommitSha, string? Branch, string? Author,
    DateTimeOffset? OccurredAt = null);

/// <summary>改产物。Title / Content 传 null 表示不动那个字段。</summary>
public sealed record UpdateArtifactRequest(
    string? Title = null,
    string? Content = null,
    string? Editor = null,
    string? Note = null);

/// <summary>打一个交付包。ItemIds 是候选池里的条目 id（文档 id 或产物 id）。</summary>
public sealed record CreateDeliveryPackageRequest(
    IReadOnlyList<Guid> ItemIds,
    string? CreatedBy = null);

/// <summary>回写测试结果。OccurredAt 同上（实际跑测试的时间）。</summary>
public sealed record TestResultRequest(
    int Total, int Passed, string? Failures, double? Coverage, string? Author,
    DateTimeOffset? OccurredAt = null);

/// <summary>写一条流水。OccurredAt 同上（实际发生时间）。</summary>
public sealed record PostEventRequest(string Message, string? Actor, DateTimeOffset? OccurredAt = null);
public sealed record DecideApprovalRequest(bool Approve, string? DecidedBy, string? Reason);

/// <summary>人工撤销审批。理由可选，但建议写 —— 账本里要能看懂为什么撤。</summary>
public sealed record RevokeApprovalRequest(string? Reason = null);
public sealed record CreateDbConnectionRequest(
    string Name, string Server, string Database, string? UserId, string? Password, bool IsDefault = true);

/// <summary>表级规则。WriteRequiresApproval=false 表示这张表的写操作免审批。</summary>
public sealed record CreateDbTableRuleRequest(
    string TableName,
    string SchemaName = "dbo",
    bool AllowRead = true,
    bool WriteRequiresApproval = true,
    /// <summary>true = 这张表一律不许写（连人工审批都不放行）。</summary>
    bool WriteForbidden = false);

/// <summary>
/// 项目级规约。完成定义用**多行文本**（一行一条）——
/// 界面上就是一个 textarea，agent 读到的也是逐条。
/// </summary>
public sealed record UpdateProjectSpecRequest(
    string? Purpose = null,
    string? DefinitionOfDone = null,
    string? CommitConvention = null,
    string? TestCommand = null,
    string? BranchConvention = null,
    string? Guardrails = null,
    /// <summary>并行上限（同时干活的 agent 数）。0 = 不限。只有工作区级用得上。</summary>
    int? MaxConcurrentAgents = null);

public sealed record UpdateProjectConfigRequest(
    string? Name,
    string? Description,
    string? RepoPath,
    string? RepoRemote,
    string? DefaultBranch,
    IReadOnlyList<string>? AllowedCommands,
    string? AgentCommand,
    string? NotificationWebhookUrl,
    string? NotificationFormat,
    /// <summary>同时最多几个 agent 能领活（0 = 不限）。</summary>
    int? MaxConcurrentAgents = null,
    /// <summary>每个任务最多能调多少次工具（0 = 不限）。</summary>
    int? MaxToolCallsPerTask = null);

public sealed record CreateMilestoneRequest(
    string Name, string? Description, DateTimeOffset? DueAt,
    Guid? ParentId = null, string? Kind = null);

/// <summary>验收里程碑（人的签字）。</summary>
public sealed record AcceptMilestoneRequest(string? AcceptedBy = null);

public sealed record UpdateMilestoneRequest(
    string? Name, string? Description, DateTimeOffset? DueAt, int? Order,
    Guid? ParentId = null, string? Kind = null,
    /// <summary>显式摘掉父里程碑（ParentId 传 null 是「不动」，清空要走这里）。</summary>
    bool ClearParent = false,
    /// <summary>显式清掉截止日。</summary>
    bool ClearDue = false);

public sealed record AssignMilestoneRequest(Guid? MilestoneId);

public sealed record ExportWorkspaceRequest(string TargetDirectory);

public sealed record ImportWorkspaceRequest(string SourceDirectory, string TargetHomeDirectory);

public sealed record SetMcpToolRequest(bool Enabled);

public sealed record StartReviewRequest(string? Reviewer);

/// <summary>
/// 回写某个评审维度的结论。
/// Skipped = true 表示该维度**不适用**（跳过）——枚举里一直有 Skipped，
/// 但以前没有入口能写出来，"本次改动不涉及安全维度"只能硬编一个通过。
/// </summary>
/// <summary>规约某一条的判定。</summary>
public sealed record SubmitSpecVerdictRequest(
    string Item,
    bool Satisfied,
    string? Note = null,
    string? Reviewer = null,
    int? Round = null,
    bool Skipped = false);

public sealed record SubmitReviewRequest(
    bool Passed, string? Findings, string? Evidence, string? Recommendation, string? Reviewer,
    int? Round, bool Skipped = false);

/// <summary>归档 / 取消归档。归档不是删除。</summary>
public sealed record ArchiveTaskRequest(bool Archived = true);

/// <summary>排期干预：排进人工计划 / 移出。</summary>
public sealed record PinTaskRequest(bool Pinned = true);

/// <summary>分派任务。传 null 的字段不动。</summary>
public sealed record AssignTaskRequest(
    string? Assignee = null,
    string? Priority = null,
    DateTimeOffset? DueAt = null,
    double? Weight = null,
    /// <summary>负责人是哪个成员（可见性按它算）。</summary>
    Guid? AssigneeMemberId = null,
    /// <summary>取消归属（放回「未分派」）。</summary>
    bool ClearAssigneeMember = false);

/// <summary>加一条任务依赖。</summary>
public sealed record AddDependencyRequest(
    Guid DependsOnTaskId,
    string? Type = null,
    string? Note = null);

/// <summary>批量裁决审批。部分失败不吞掉 —— 逐条回。</summary>
public sealed record BatchDecideApprovalRequest(
    IReadOnlyList<Guid> ApprovalIds,
    bool Approve = true,
    string? DecidedBy = null,
    string? Reason = null);

/// <summary>批量裁决的单条结果。</summary>
public sealed record BatchDecisionResult(
    Guid Id,
    bool Ok,
    string? Status = null,
    string? Error = null);
