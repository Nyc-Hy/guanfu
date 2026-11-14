using Microsoft.Extensions.Options;
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
            string? key, string? type, string? branch, int? limit, int? offset, bool? includeArchived,
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
                branch, limit, offset, includeArchived ?? false));
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

        // 产出文件：这个任务实际改了哪些文件。工作台自己从 git 数的，
        // 不是 agent 自报的 —— 所以这条是可查的证据，不是自我声明。
        api.MapGet("/tasks/{taskId:guid}/file-changes", async (
            Guid taskId, IFileChangeService fileChanges, CancellationToken ct) =>
            Results.Ok(await fileChanges.ListAsync(taskId, ct)));

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
                taskId, request.Assignee, priority, request.DueAt, request.Weight, ct));
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
            Guid approvalId, DecideApprovalRequest request, IApprovalService approvals, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var approval = await approvals.DecideAsync(
                    approvalId, request.Approve, request.DecidedBy, request.Reason,
                    ct, decidedByCredential: CredentialFingerprint(http));
                return Results.Ok(approval);
            }
            catch (WorkbenchException ex)
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
                catch (WorkbenchException ex)
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
                request.ParentId, kind, request.ClearParent, request.ClearDue));
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
                request.AllowRead, request.WriteRequiresApproval, ct);

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

            var byKeyword = await tasks.ListAsync(keyword: keyword, cancellationToken: ct);
            var byKey = await tasks.ListAsync(key: keyword, cancellationToken: ct);
            var taskHits = byKeyword.Concat(byKey)
                .GroupBy(t => t.Id).Select(g => g.First())
                .Take(take).ToList();

            var projectHits = (await projects.ListAsync(ct))
                .Where(p => p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Take(take).ToList();

            var documentHits = new List<object>();
            var allTasks = await tasks.ListAsync(includeArchived: true, cancellationToken: ct);
            foreach (var task in allTasks)
            {
                if (documentHits.Count >= take)
                {
                    break;
                }

                foreach (var doc in await documents.ListAsync(task.Id, ct))
                {
                    if (documentHits.Count >= take)
                    {
                        break;
                    }

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
                    new { ok = false, error = "人工凭证只能在工作台本机获取。请从启动服务的控制台复制 token。" },
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
            ExportWorkspaceRequest request, IWorkspaceArchiver archiver, IOptions<WorkbenchOptions> options, CancellationToken ct) =>
        {
            // 入口侧先挡一道：导出目录等于 WORKBENCH_HOME 会把正在用的库删掉，
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
            ImportWorkspaceRequest request, IWorkspaceArchiver archiver, IOptions<WorkbenchOptions> options, CancellationToken ct) =>
        {
            // 导入到正在跑的 WORKBENCH_HOME = 把正在用的库覆盖掉
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

        // 验哈希链：审计有没有被人在库文件层面改过。改一条、删一条，链都会断。
        api.MapGet("/audit/verify", async (IToolAuditService audit, CancellationToken ct) =>
            Results.Ok(await audit.VerifyChainAsync(ct)));

        return app;
    }

    /// <summary>
    /// 人工凭证的指纹（哈希前 12 位）。
    ///
    /// 共享 token 的现状下系统认不出真人 —— 但记下凭证指纹，好歹能区分
    /// 「同一个操作员」和「换了个人在操作」，而且不用把 token 明文写进审计。
    /// </summary>
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

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token));

        return Convert.ToHexString(hash)[..12];
    }
}

public sealed record CreateProjectRequest(string Name, string? Description, string? RepoPath = null);
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

/// <summary>人工撤销审批。理由可选，但建议写 —— 账本里要能看懂为什么撤。</summary>
public sealed record RevokeApprovalRequest(string? Reason = null);
public sealed record CreateDbConnectionRequest(
    string Name, string Server, string Database, string? UserId, string? Password, bool IsDefault = true);

/// <summary>表级规则。WriteRequiresApproval=false 表示这张表的写操作免审批。</summary>
public sealed record CreateDbTableRuleRequest(
    string TableName,
    string SchemaName = "dbo",
    bool AllowRead = true,
    bool WriteRequiresApproval = true);

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
public sealed record SubmitReviewRequest(
    bool Passed, string? Findings, string? Evidence, string? Recommendation, string? Reviewer,
    int? Round, bool Skipped = false);

/// <summary>归档 / 取消归档。归档不是删除。</summary>
public sealed record ArchiveTaskRequest(bool Archived = true);

/// <summary>分派任务。传 null 的字段不动。</summary>
public sealed record AssignTaskRequest(
    string? Assignee = null,
    string? Priority = null,
    DateTimeOffset? DueAt = null,
    double? Weight = null);

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
