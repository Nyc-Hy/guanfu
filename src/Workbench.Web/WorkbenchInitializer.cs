using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure;
using Workbench.Mcp;

namespace Workbench.Web;

/// <summary>启动时初始化数据库（建表 + 默认工作区），并加载 MCP 工具开关。</summary>
public sealed class WorkbenchInitializer(WorkbenchDbContext db, McpToolState toolState)
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

        // 工具开关要先加载好：不然服务刚起来的那一下，工具清单是「全开」的
        await toolState.RefreshAsync(cancellationToken);
    }
}
