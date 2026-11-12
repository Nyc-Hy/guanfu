using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure;

namespace Workbench.Web;

/// <summary>启动时初始化数据库（建表 + 默认工作区）。</summary>
public sealed class WorkbenchInitializer(WorkbenchDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (!await db.Workspaces.AnyAsync(cancellationToken))
        {
            db.Workspaces.Add(new Workbench.Domain.Workspace
            {
                Name = "默认工作区",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
