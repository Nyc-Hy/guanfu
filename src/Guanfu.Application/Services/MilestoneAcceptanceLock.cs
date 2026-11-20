using Guanfu.Domain;
using Microsoft.EntityFrameworkCore;

namespace Guanfu.Application.Services;

/// <summary>版本一旦验收，其任务范围和归档状态须先撤销验收才能修改。</summary>
internal static class MilestoneAcceptanceLock
{
    internal static async Task<string?> FindAsync(
        IAppDbContext db, Guid projectId, Guid? milestoneId, CancellationToken cancellationToken)
    {
        if (milestoneId is null)
        {
            return null;
        }

        var milestones = await db.Milestones.AsNoTracking().Where(m => m.ProjectId == projectId)
            .Select(m => new { m.Id, m.ParentId, m.Name, m.AcceptedAt })
            .ToDictionaryAsync(m => m.Id, cancellationToken);
        var visited = new HashSet<Guid>();
        while (milestoneId is Guid current && visited.Add(current) && milestones.TryGetValue(current, out var milestone))
        {
            if (milestone.AcceptedAt is not null)
            {
                return milestone.Name;
            }

            milestoneId = milestone.ParentId;
        }

        return null;
    }

    internal static async Task EnsureEditableAsync(
        IAppDbContext db, Guid projectId, Guid? milestoneId, CancellationToken cancellationToken)
    {
        if (await FindAsync(db, projectId, milestoneId, cancellationToken) is { } name)
        {
            throw new GuanfuException($"「{name}」已经验收。请撤销验收后再调整版本范围。");
        }
    }
}
