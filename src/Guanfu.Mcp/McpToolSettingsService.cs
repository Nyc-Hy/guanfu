using Microsoft.EntityFrameworkCore;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Mcp;

/// <summary>
/// 工具开关的读写。
///
/// 落库的只是「被显式改过的」那些 —— 没记录的一律按开启处理。
/// 这样以后往 MCP 里加新工具，不需要写迁移脚本把它打开。
/// </summary>
public sealed class McpToolSettingsService(
    IAppDbContext db,
    IClock clock,
    McpToolState state,
    IReadOnlyList<McpToolDescriptor> catalog) : IMcpToolService
{
    public async Task<IReadOnlyList<McpToolInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.McpToolSettings.AsNoTracking().ToListAsync(cancellationToken);
        var byName = rows.ToDictionary(r => r.ToolName, StringComparer.Ordinal);

        return catalog
            .Select(d =>
            {
                var hasOverride = byName.TryGetValue(d.Name, out var setting);
                return new McpToolInfo(
                    d.Name,
                    d.Category,
                    d.Description,
                    !hasOverride || setting!.Enabled,
                    d.RequiresApproval,
                    hasOverride ? setting!.UpdatedAt : null);
            })
            .ToList();
    }

    public async Task<McpToolInfo> SetEnabledAsync(
        string toolName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var descriptor = catalog.FirstOrDefault(c => string.Equals(c.Name, toolName, StringComparison.Ordinal))
                         ?? throw new NotFoundException("MCP 工具", toolName);

        var row = await db.McpToolSettings.FirstOrDefaultAsync(s => s.ToolName == toolName, cancellationToken);

        if (row is null)
        {
            row = new McpToolSetting { ToolName = toolName, Enabled = enabled, UpdatedAt = clock.UtcNow };
            db.McpToolSettings.Add(row);
        }
        else
        {
            row.Enabled = enabled;
            row.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        // 写穿内存态：改完立刻生效，不用重启服务
        state.SetOverride(toolName, enabled);

        return new McpToolInfo(
            descriptor.Name,
            descriptor.Category,
            descriptor.Description,
            enabled,
            descriptor.RequiresApproval,
            row.UpdatedAt);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ResetAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.McpToolSettings.ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            db.McpToolSettings.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken);
        }

        state.ClearOverrides();

        return await ListAsync(cancellationToken);
    }
}
