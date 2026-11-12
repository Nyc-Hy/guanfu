using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;

namespace Workbench.Mcp;

/// <summary>
/// 工具开关的内存态。
///
/// 为什么是内存态而不是每次查库：MCP 的 tools/list 与 tools/call 在请求热路径上，
/// 每个工具都查一次库太浪费；而 tools/list 是**会话级**的，没法按项目返回不同清单，
/// 所以开关本来就是工作台级的，进程内一份就够。
///
/// 落库在 <see cref="McpToolSettingsService"/>：启动时加载一次，之后每次改动写穿。
/// </summary>
public sealed class McpToolState(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, bool> _overrides = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _loaded;

    /// <summary>没被显式关掉的工具一律算开启（升级加新工具不用改数据）。</summary>
    public bool IsEnabled(string toolName)
    {
        EnsureLoaded();
        return !_overrides.TryGetValue(toolName, out var enabled) || enabled;
    }

    /// <summary>覆盖某个工具的开关状态。</summary>
    public void SetOverride(string toolName, bool enabled)
    {
        EnsureLoaded();
        _overrides[toolName] = enabled;
    }

    public void ClearOverrides()
    {
        EnsureLoaded();
        _overrides.Clear();
    }

    public IReadOnlyDictionary<string, bool> Snapshot()
    {
        EnsureLoaded();
        return new Dictionary<string, bool>(_overrides, StringComparer.Ordinal);
    }

    /// <summary>从库里重新加载（启动时调用）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rows = await db.McpToolSettings.AsNoTracking().ToListAsync(cancellationToken);

            _overrides.Clear();
            foreach (var row in rows)
            {
                _overrides[row.ToolName] = row.Enabled;
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 首次访问时兜底加载。正常流程下启动初始化器已经加载过了，
    /// 这里只是防止「初始化没跑成就开始服务」的边缘情况。
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_loaded)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rows = db.McpToolSettings.AsNoTracking().ToList();

            foreach (var row in rows)
            {
                _overrides[row.ToolName] = row.Enabled;
            }

            _loaded = true;
        }
        catch (Exception)
        {
            // 加载不了就按「全部开启」处理，别把服务卡死
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
