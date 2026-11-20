using System.Text.Json;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Microsoft.EntityFrameworkCore;

namespace Guanfu.Mcp;

/// <summary>用资源实际归属校验 MCP 凭证的项目范围，避免只校验调用方自报的项目。</summary>
internal sealed class McpProjectAuthorization(
    IAppDbContext db,
    IAgentSessionAccessor sessions,
    IProjectScopeResolver projects)
{
    public async Task EnsureAllowedAsync(
        string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        if (sessions.CurrentCredential is not { ProjectScope.Count: > 0 } credential)
        {
            return;
        }

        // 项目列表自行过滤。会话入口在查找、创建及复用时校验归属。
        if (toolName is "guanfu_list_projects" or "doc_list_formats" or
            "guanfu_bind_session" or "guanfu_get_session")
        {
            return;
        }

        if (toolName is "guanfu_create_project" or "guanfu_verify_audit_chain" or "guanfu_verify_audit_archive")
        {
            throw new GuanfuException("这项操作需要不受项目范围限制的凭证。");
        }

        Guid? projectId = null;
        foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
        {
            var field = name.ToLowerInvariant();
            if (value.ValueKind == JsonValueKind.Null || !IsProjectReference(field))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id))
            {
                throw new GuanfuException($"参数 {name} 需要有效的 ID。");
            }

            var ownerProject = await FindProjectAsync(field, id, cancellationToken);
            if (ownerProject is not Guid owner || !credential.AllowsProject(owner))
            {
                throw new GuanfuException("资源不存在或当前凭证无权访问该资源。");
            }

            if (projectId is Guid prior && prior != owner)
            {
                throw new GuanfuException("同一次工具调用的资源必须属于同一个项目。");
            }

            projectId = owner;
        }

        await projects.ResolveAsync(projectId, cancellationToken);

        // 这些工具允许全局查询。范围内有多个项目时，必须给定查询项目或已绑定会话。
        if ((toolName is "guanfu_list_tasks" or "guanfu_list_approvals" or
            "guanfu_list_audit" or "guanfu_list_milestones" or "guanfu_get_metrics") &&
            await projects.ResolveOrNullAsync(ExplicitProject(arguments), cancellationToken) is null)
        {
            throw new GuanfuException("请传 projectId 选择要查询的项目。");
        }
    }

    private static Guid? ExplicitProject(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null && arguments.TryGetValue("projectId", out var value) &&
        value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;

    private static bool IsProjectReference(string field) => field is
        "projectid" or "taskid" or "parenttaskid" or "dependsontaskid" or
        "documentid" or "milestoneid" or "parentid" or "approvalid" or "connectionid";

    private Task<Guid?> FindProjectAsync(string field, Guid id, CancellationToken ct) => field switch
    {
        "projectid" => Task.FromResult<Guid?>(id),
        "taskid" or "parenttaskid" or "dependsontaskid" => db.Tasks
            .Where(t => t.Id == id).Select(t => (Guid?)t.ProjectId).SingleOrDefaultAsync(ct),
        "documentid" => db.Documents.Where(d => d.Id == id).Join(db.Tasks,
            d => d.TaskId, t => t.Id, (_, t) => (Guid?)t.ProjectId).SingleOrDefaultAsync(ct),
        "milestoneid" or "parentid" => db.Milestones
            .Where(m => m.Id == id).Select(m => (Guid?)m.ProjectId).SingleOrDefaultAsync(ct),
        "approvalid" => db.Approvals
            .Where(a => a.Id == id).Select(a => a.ProjectId ??
                db.Tasks.Where(t => t.Id == a.TaskId).Select(t => (Guid?)t.ProjectId).FirstOrDefault())
            .SingleOrDefaultAsync(ct),
        "connectionid" => db.DbConnections
            .Where(c => c.Id == id).Select(c => (Guid?)c.ProjectId).SingleOrDefaultAsync(ct),
        _ => throw new InvalidOperationException("未知的项目资源参数。")
    };
}
