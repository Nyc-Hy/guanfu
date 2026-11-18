using Microsoft.EntityFrameworkCore;

namespace Guanfu.Application.Services;

/// <summary>
/// 工位解析：一次工具调用该在哪个目录上干活。
///
/// 这是 v0.9「工位」这层的落地点。工位登记了不代表工具认它 ——
/// 认它的意思��：文件工具、git 工具、shell、产出采集，全都按工位走，
/// 而不是一律指着项目的仓库根。
///
/// 不认的后果很具体：agent 在自己的 worktree 里写代码，观复却去读主工作区，
/// 产出采集把**主工作区**的改动记成这个任务的产出 —— 审计记的是假的。
/// </summary>
public interface IWorkstationResolver
{
    /// <summary>
    /// 算出这次调用该用的工作目录。
    ///
    /// 规则：
    /// - 任务有**活着的租约**且登记了工位 → 用工位；
    /// - 否则 → 项目仓库根（没绑仓库就是 null，没有文件系统面）。
    ///
    /// 工位目录被删了、或者不再是本仓库的 worktree → 退回仓库根，
    /// 别拿着一个失效目录去读文件。
    /// </summary>
    /// <param name="taskId">
    /// 这次调用归属的任务（会话认领过任务时才有）。
    /// </param>
    /// <param name="agentId">
    /// 这次调用的 agent。会话没挂任务时靠它找回工位 ——
    /// 工位本来是「这个 agent 在这个项目里的活」的属性，不是「这个任务」的属性。
    /// </param>
    Task<string?> ResolveAsync(
        Guid projectId,
        Guid? taskId,
        string? agentId = null,
        CancellationToken cancellationToken = default);
}

public sealed class WorkstationResolver(IAppDbContext db, IClock clock) : IWorkstationResolver
{
    public async Task<string?> ResolveAsync(
        Guid projectId,
        Guid? taskId,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        var root = string.IsNullOrWhiteSpace(project?.RepoPath)
            ? null
            : Path.GetFullPath(project.RepoPath);

        var now = clock.UtcNow;

        // 先按任务找：会话认领过任务时，任务上的租约就是最准的工位。
        // 再按 agent 找：会话没挂任务（比如活是走 REST 领的）时，
        // 「这个 agent 在这个项目里手上那个活」的工位也得认 ——
        // 不然 agent 在自己的 worktree 里干活，工具却去读主工作区。
        string? workPath = null;

        if (taskId is Guid id)
        {
            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            // 租约过期 = 活干完了 = 工位还回去了 —— 别拿一个过期工位继续读写
            if (task is not null &&
                task.LeaseExpiresAt is not null &&
                task.LeaseExpiresAt.Value > now &&
                !string.IsNullOrWhiteSpace(task.WorkPath))
            {
                workPath = task.WorkPath;
            }
        }

        if (workPath is null && !string.IsNullOrWhiteSpace(agentId))
        {
            var agent = agentId;
            workPath = await db.Tasks.AsNoTracking()
                .Where(t => t.ProjectId == projectId &&
                            t.ClaimedByAgentId == agent &&
                            t.LeaseExpiresAt != null &&
                            t.LeaseExpiresAt > now &&
                            t.WorkPath != null)
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => t.WorkPath)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (workPath is null)
        {
            return root;
        }

        var station = Path.GetFullPath(workPath);

        // 主工作区就是仓库根，直接用
        if (root is not null && string.Equals(station, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        // 工位目录可能已经被删了 —— 用之前确认一下还在
        if (!Directory.Exists(station))
        {
            return root;
        }

        // 非主工作区的工位，必须**仍然**是本仓库的 worktree。
        // 登记时校验过一次，但那是登记那一刻 —— 中间目录被换了contents照样有风险。
        var repoRoot = project?.RepoRoot ?? root ?? station;
        var check = WorkPathPolicy.ValidateWorktree(repoRoot, station);

        return check.Ok ? station : root;
    }
}
