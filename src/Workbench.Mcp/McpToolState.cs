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
///
/// 线程安全（P1-5）：快照用「写时复制 + volatile 引用」——读路径无锁，
/// 所有改动都在 <see cref="_gate"/> 里串行化，先建好新字典再一次性换引用。
/// 加载失败**不**置 _loaded（下次访问重试），期间 IsEnabled 一律 fail-closed，
/// 绝不把没确认过开关的工具放出去。
/// </summary>
public sealed class McpToolState(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Dictionary<string, bool> _overrides = new(StringComparer.Ordinal);
    private volatile bool _loaded;

    /// <summary>没被显式关掉的工具一律算开启（升级加新工具不用改数据）。</summary>
    public bool IsEnabled(string toolName)
    {
        EnsureLoaded();

        // 加载失败：fail-closed。没拿到开关配置就不放行，
        // 免得把本该关掉的工具暴露出去。下次访问会重试加载。
        if (!_loaded)
        {
            return false;
        }

        var snapshot = _overrides;
        return !snapshot.TryGetValue(toolName, out var enabled) || enabled;
    }

    /// <summary>覆盖某个工具的开关状态。</summary>
    public void SetOverride(string toolName, bool enabled)
    {
        _gate.Wait();
        try
        {
            LoadIfNeeded();

            // 写时复制：在闸门里读-改-写，并发的 SetOverride 不会互相丢更新。
            var next = new Dictionary<string, bool>(_overrides, StringComparer.Ordinal)
            {
                [toolName] = enabled
            };
            _overrides = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ClearOverrides()
    {
        _gate.Wait();
        try
        {
            LoadIfNeeded();
            _overrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
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

            var next = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                next[row.ToolName] = row.Enabled;
            }

            // 原子替换：先建好新字典再一次性换引用。并发读要么看到旧的完整快照，
            // 要么看到新的完整快照，不会看到「清空到一半」的中间态。
            _overrides = next;
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
            LoadIfNeeded();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>调用方必须已持有 <see cref="_gate"/>。加载失败不置 _loaded，留给下次重试。</summary>
    private void LoadIfNeeded()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rows = db.McpToolSettings.AsNoTracking().ToList();

            var next = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                next[row.ToolName] = row.Enabled;
            }

            _overrides = next;
            _loaded = true;
        }
        catch (Exception)
        {
            // 加载不了**不**置 _loaded —— 下次访问重试。
            // 期间 IsEnabled 一律 fail-closed，别把没确认过开关的工具放出去。
        }
    }
}
