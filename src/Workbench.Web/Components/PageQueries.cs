namespace Workbench.Web.Components;

/// <summary>
/// 全局搜索页的纯匹配逻辑。不碰 IO，方便单测。
///
/// 编号归一化匹配不在这里重写：搜索页把编号交给 TaskService.ListAsync(key:)，
/// 归一化（只留字母数字、转小写，规矩 11）由应用层那一份负责，避免第二套口径。
/// 这里只做页面自己的部分：文本包含匹配与两路结果合并。
/// </summary>
public static class SearchMatcher
{
    /// <summary>文本包含匹配：大小写不敏感，查询两边去空白。空查询不命中。</summary>
    public static bool Contains(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 合并两路搜索结果并按 Id 去重：first 的顺序优先，second 里没出现过的补在后面，
    /// 最后截断到 limit。编号命中排前面，标题命中补充 —— 找编号的人要的是精确那一条。
    /// </summary>
    public static IReadOnlyList<T> MergeById<T>(
        IEnumerable<T> first, IEnumerable<T> second, Func<T, Guid> idOf, int limit)
    {
        var merged = new List<T>();
        var seen = new HashSet<Guid>();

        foreach (var item in first.Concat(second))
        {
            if (merged.Count >= limit)
            {
                break;
            }

            if (seen.Add(idOf(item)))
            {
                merged.Add(item);
            }
        }

        return merged;
    }
}

/// <summary>
/// 审计页的分页算术。IToolAuditService.ListAsync 只有 limit 没有 offset，
/// 所以用「多取一条探针」的窗口法翻页：第 N 页取 (N+1)*pageSize+1 条，
/// 多出来的那条只用来判断有没有下一页。服务端上限 1000 条，翻到头就停。
/// 「结果」过滤是内存里做的，这时一次取满窗口再对过滤后的列表常规分页。
/// </summary>
public static class AuditPaging
{
    /// <summary>服务端 ListAsync 的硬上限（Math.Clamp(limit, 1, 1000)）。</summary>
    public const int MaxWindow = 1000;

    /// <summary>这一页要向服务端请求多少条。</summary>
    public static int RequestLimit(int pageIndex, int pageSize, bool outcomeFilter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return outcomeFilter
            ? MaxWindow
            : Math.Min(MaxWindow, ((pageIndex + 1) * pageSize) + 1);
    }

    /// <summary>
    /// 有没有下一页。fetchedCount 是过滤后实际拿到的行数：
    /// 无结果过滤时靠探针行（取到的比本页末尾多）判断，且不能翻过 1000 条窗口；
    /// 有结果过滤时窗口已取满，直接看过滤后的总量。
    /// </summary>
    public static bool HasNextPage(int fetchedCount, int pageIndex, int pageSize, bool outcomeFilter)
    {
        var shownEnd = (pageIndex + 1) * pageSize;

        return outcomeFilter
            ? shownEnd < fetchedCount
            : fetchedCount > shownEnd && shownEnd < MaxWindow;
    }

    /// <summary>取当前页的行。</summary>
    public static IReadOnlyList<T> PageSlice<T>(IReadOnlyList<T> rows, int pageIndex, int pageSize)
    {
        return rows.Skip(pageIndex * pageSize).Take(pageSize).ToList();
    }
}