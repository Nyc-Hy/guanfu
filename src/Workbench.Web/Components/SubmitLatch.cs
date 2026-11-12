namespace Workbench.Web.Components;

/// <summary>
/// 防重复提交的闩锁。
///
/// 为什么光靠「正在提交」标志不够：Blazor Server 对同一条电路的事件是排队串行处理的，
/// 连点两次时，第二个事件要等第一个 handler 整个跑完才被处理 ——
/// 那时标志已经在 finally 里复位了，第二次提交照样发出去
/// （两个 agent 进程 / 两份任务 / 上传两份文件）。
///
/// 所以：已经做过的操作把 key 记下来，直到页面重新加载都不再重复做。
/// key 一般用业务对象的 id；失败的要把 key 放掉，好让人改了再试。
/// </summary>
public sealed class SubmitLatch
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>这个 key 是不是已经做过（或者正在做）。</summary>
    public bool IsLatched(string key) => _keys.Contains(key);

    /// <summary>占位。返回 true 表示之前没做过，这次可以继续。</summary>
    public bool TryLatch(string key) => _keys.Add(key);

    /// <summary>放掉占位：操作失败时调用，让人能改了再试。</summary>
    public void Release(string key) => _keys.Remove(key);
}
