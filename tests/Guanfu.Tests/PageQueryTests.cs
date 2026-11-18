using Guanfu.Web.Components;

namespace Guanfu.Tests;

/// <summary>
/// 新页面的纯查询逻辑：搜索匹配 / 合并、审计分页算术。
/// 编号归一化本身由 TaskService 负责（见 TaskKeyTests），这里不重复测。
/// </summary>
public class PageQueryTests
{
    // ---- SearchMatcher.Contains ----

    [Theory]
    [InlineData("写登录接口", "登录")]
    [InlineData("Login API", "login")]
    [InlineData("Login API", "LOGIN")]
    [InlineData("Login API", "  login  ")]
    public void 文本包含匹配大小写不敏感且去查询空白(string text, string query)
    {
        Assert.True(SearchMatcher.Contains(text, query));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("abc", null)]
    [InlineData("abc", "")]
    [InlineData("abc", "   ")]
    [InlineData("abc", "xyz")]
    public void 空文本或空查询不命中(string? text, string? query)
    {
        Assert.False(SearchMatcher.Contains(text, query));
    }

    // ---- SearchMatcher.MergeById ----

    private sealed record Item(Guid Id, string Name);

    [Fact]
    public void 合并两路结果按Id去重且第一路优先()
    {
        var a = new Item(Guid.NewGuid(), "编号命中");
        var b = new Item(Guid.NewGuid(), "标题命中");

        var merged = SearchMatcher.MergeById(
            new[] { a }, new[] { b, a }, x => x.Id, limit: 10);

        Assert.Equal(new[] { a, b }, merged);
    }

    [Fact]
    public void 合并结果截断到上限()
    {
        var items = Enumerable.Range(0, 5).Select(_ => new Item(Guid.NewGuid(), "x")).ToList();

        var merged = SearchMatcher.MergeById(items, items, x => x.Id, limit: 3);

        Assert.Equal(3, merged.Count);
        Assert.Equal(items.Take(3), merged);
    }

    // ---- AuditPaging：limit 窗口法翻页（服务端没有 offset） ----

    [Theory]
    [InlineData(0, 20, false, 21)]   // 第 1 页：20 条 + 1 条探针
    [InlineData(2, 20, false, 61)]   // 第 3 页：取到 60 条 + 探针
    [InlineData(0, 20, true, 1000)]  // 结果过滤：一次取满窗口
    [InlineData(49, 20, false, 1000)] // 窗口封顶 1000
    [InlineData(99, 20, false, 1000)]
    public void 请求条数按页码与过滤模式计算(int page, int size, bool outcomeFilter, int expected)
    {
        Assert.Equal(expected, AuditPaging.RequestLimit(page, size, outcomeFilter));
    }

    [Fact]
    public void 探针行存在才有下一页()
    {
        // 取回 21 条（20 + 探针）→ 有下一页
        Assert.True(AuditPaging.HasNextPage(21, pageIndex: 0, pageSize: 20, outcomeFilter: false));
        // 正好 20 条 → 到底了
        Assert.False(AuditPaging.HasNextPage(20, pageIndex: 0, pageSize: 20, outcomeFilter: false));
    }

    [Fact]
    public void 翻到一千条窗口尽头就停()
    {
        // 第 50 页（0 起）展示 981~1000 条，即使探针存在也不许再翻
        Assert.False(AuditPaging.HasNextPage(1000, pageIndex: 49, pageSize: 20, outcomeFilter: false));
        // 第 49 页展示到 980，窗口内还有余量
        Assert.True(AuditPaging.HasNextPage(981, pageIndex: 48, pageSize: 20, outcomeFilter: false));
    }

    [Fact]
    public void 结果过滤时按过滤后的总量分页()
    {
        Assert.True(AuditPaging.HasNextPage(45, pageIndex: 0, pageSize: 20, outcomeFilter: true));
        Assert.False(AuditPaging.HasNextPage(20, pageIndex: 0, pageSize: 20, outcomeFilter: true));
        Assert.False(AuditPaging.HasNextPage(40, pageIndex: 1, pageSize: 20, outcomeFilter: true));
    }

    [Fact]
    public void 取页切片不越界()
    {
        var rows = Enumerable.Range(0, 45).ToList();

        Assert.Equal(Enumerable.Range(20, 20), AuditPaging.PageSlice(rows, 1, 20));
        // 最后一页不满
        Assert.Equal(Enumerable.Range(40, 5), AuditPaging.PageSlice(rows, 2, 20));
        // 超出范围给空页，不抛异常
        Assert.Empty(AuditPaging.PageSlice(rows, 5, 20));
    }
}