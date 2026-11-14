using Workbench.Documents;
using Workbench.Domain;

namespace Workbench.Tests;

public class MarkdownDocumentParserTests
{
    private readonly MarkdownDocumentParser _parser = new();

    [Fact]
    public void 删除线被识别为废弃内容()
    {
        var parsed = _parser.Parse("正常内容，~~这段已经废弃~~，后续内容。");

        var inlines = parsed.Blocks
            .SelectMany(b => b.Inlines ?? [])
            .ToList();

        var struck = inlines.Where(i => i.Strike).ToList();

        Assert.Single(struck);
        Assert.Contains("已经废弃", struck[0].Text);

        // 普通内容不能被误判成删除线
        Assert.False(inlines.First(i => i.Text.Contains("正常内容")).Strike);
    }

    [Fact]
    public void 标题与大纲被还原()
    {
        var parsed = _parser.Parse("""
            # 需求标题

            ## 子章节

            内容
            """);

        Assert.Equal("需求标题", parsed.Title);
        Assert.Equal(2, parsed.Outline.Count);
        Assert.Equal(1, parsed.Outline[0].Level);
        Assert.Equal("子章节", parsed.Outline[1].Title);
    }

    [Fact]
    public void 任务清单的勾选状态被还原()
    {
        var parsed = _parser.Parse("""
            - [x] 已完成的事
            - [ ] 还没做的事
            """);

        var list = parsed.Blocks.FirstOrDefault(b => b.Kind == BlockKind.List);
        Assert.NotNull(list);
        Assert.Equal("task", list.ListKind);

        var items = list.Children!;
        Assert.Equal(2, items.Count);
        Assert.True(items[0].Checked);
        Assert.False(items[1].Checked);
    }

    [Fact]
    public void 表格被还原()
    {
        var parsed = _parser.Parse("""
            | 字段 | 类型 |
            | --- | --- |
            | id | int |
            | name | string |
            """);

        var table = parsed.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Table);
        Assert.NotNull(table);
        Assert.Equal(2, table.Headers!.Count);
        Assert.Equal("字段", table.Headers[0]);

        // 1 行表头 + 2 行数据
        Assert.Equal(3, table.Rows!.Count);
        Assert.Equal("id", table.Rows![1][0][0].Text);
        Assert.Equal("string", table.Rows[2][1][0].Text);
    }

    [Fact]
    public void diff代码块被还原为补丁行()
    {
        var parsed = _parser.Parse("""
            ```diff
            --- a/x.cs
            +++ b/x.cs
            @@ -1,2 +1,2 @@
             context line
            -removed line
            +added line
            ```
            """);

        var diff = parsed.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Diff);
        Assert.NotNull(diff);
        Assert.True(diff.IsDiff);

        Assert.Contains(diff.Lines!, l => l.Kind == DiffTextParser.Removed && l.Text == "removed line");
        Assert.Contains(diff.Lines!, l => l.Kind == DiffTextParser.Added && l.Text == "added line");

        // diff 里的增删也会进修订清单
        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.Delete && r.Text == "removed line");
        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.Insert && r.Text == "added line");
    }

    [Fact]
    public void 加粗与行内代码被还原()
    {
        var parsed = _parser.Parse("这是 **重点** 还有 `code`。");

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        Assert.Contains(inlines, i => i.Bold && i.Text.Contains("重点"));
        Assert.Contains(inlines, i => i.Code && i.Text.Contains("code"));
    }

    // ------------------------------------------------------------------
    // 容器型 inline 必须递归全部子节点：以前只转 FirstChild，兄弟节点直接消失
    // ------------------------------------------------------------------

    [Fact]
    public void 混排强调的兄弟节点不会丢()
    {
        var parsed = _parser.Parse("**a *b* c**");

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        // 修复前只剩 "a "，"*b* c" 整段没了
        Assert.Equal("a b c", string.Concat(inlines.Select(i => i.Text)));
        Assert.Contains(inlines, i => i is { Text: "b", Bold: true, Italic: true });
        Assert.All(inlines, i => Assert.True(i.Bold));
        Assert.Equal("a b c\n", parsed.PlainText);
    }

    [Fact]
    public void 嵌套删除线里的强调不会丢()
    {
        var parsed = _parser.Parse("~~x **y** z~~");

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        // 修复前只剩 "x "，"y z" 没了 —— 废弃内容的范围都被改小了
        Assert.Equal("x y z", string.Concat(inlines.Select(i => i.Text)));
        Assert.All(inlines, i => Assert.True(i.Strike));
        Assert.Contains(inlines, i => i is { Text: "y", Bold: true, Strike: true });

        // 纯文本通道也要看得出整段都是废弃内容
        Assert.Equal("~~x y z~~\n", parsed.PlainText);
    }

    [Fact]
    public void 列表项里的强调兄弟节点不会丢()
    {
        var parsed = _parser.Parse("- 一级 **粗** 尾巴");

        var item = parsed.Blocks.Single(b => b.Kind == BlockKind.List).Children!.Single();

        Assert.Equal("一级 粗 尾巴", string.Concat(item.Inlines!.Select(i => i.Text)));
        Assert.Contains(item.Inlines!, i => i is { Text: "粗", Bold: true });
    }

    [Fact]
    public void 嵌套列表的纯文本是父项在前()
    {
        var parsed = _parser.Parse("- 一级\n  - 二级");

        Assert.Equal("一级\n二级\n", parsed.PlainText);
    }

    // ------------------------------------------------------------------
    // 表格：单元格走与正文相同的 inline 递归，且内容必须进 PlainText
    // ------------------------------------------------------------------

    [Fact]
    public void 表格单元格里的删除线被保留()
    {
        var parsed = _parser.Parse("""
            | 字段 | 说明 |
            | --- | --- |
            | id | ~~旧字段~~ 新字段 |
            """);

        var table = parsed.Blocks.Single(b => b.Kind == BlockKind.Table);
        var cell = table.Rows![1][1];

        Assert.Contains(cell, i => i is { Text: "旧字段", Strike: true });
        Assert.Contains(cell, i => i.Text.Contains("新字段") && !i.Strike);
    }

    [Fact]
    public void 表格内容进纯文本通道()
    {
        var parsed = _parser.Parse("""
            | 字段 | 说明 |
            | --- | --- |
            | id | ~~旧字段~~ 新字段 |
            | name | 名字 |
            """);

        // GetTextAsync 是 agent 的主读入口：以前这里只有「2 列表头, 3 行」，表格内容全丢
        Assert.DoesNotContain("列表头", parsed.PlainText);
        Assert.Contains("字段\t说明", parsed.PlainText);
        Assert.Contains("id\t~~旧字段~~ 新字段", parsed.PlainText);
        Assert.Contains("name\t名字", parsed.PlainText);
    }

    [Fact]
    public void 表格单元格里的链接与强调被保留()
    {
        var parsed = _parser.Parse("""
            | 字段 | 说明 |
            | --- | --- |
            | id | [**文档**](https://ok.com/a) |
            """);

        var cell = parsed.Blocks.Single(b => b.Kind == BlockKind.Table).Rows![1][1];

        Assert.Contains(cell, i => i is { Text: "文档", Bold: true, Link: "https://ok.com/a" });
    }

    // ------------------------------------------------------------------
    // 兜底：未知 inline 不能把类型名当正文吐出去
    // ------------------------------------------------------------------

    [Fact]
    public void 未知行内节点不再把类型名写进正文()
    {
        var parsed = _parser.Parse("a <b>c</b> d &amp; e");

        // 修复前这里会出现 "Markdig.Syntax.Inlines.HtmlInline" 这种类型名
        Assert.DoesNotContain("Markdig", parsed.PlainText);
        Assert.Contains("a <b>c</b> d & e", parsed.PlainText);
    }

    [Fact]
    public void 脚注正文降级成带标号的段落()
    {
        var parsed = _parser.Parse("正文[^1]。\n\n[^1]: 脚注内容 **加粗**");

        Assert.Contains("[^1]", parsed.PlainText);
        Assert.Contains("[^1]: 脚注内容 加粗", parsed.PlainText);
        Assert.DoesNotContain("Markdig", parsed.PlainText);

        var note = parsed.Blocks.Last();
        Assert.Contains(note.Inlines!, i => i is { Text: "加粗", Bold: true });
    }

    [Fact]
    public void 行内数学与缩写不会丢()
    {
        var parsed = _parser.Parse("公式 $x^2$ 与术语。\n\n*[术语]: 解释");

        Assert.Contains("x^2", parsed.PlainText);
        Assert.Contains("术语", parsed.PlainText);
        Assert.DoesNotContain("Markdig", parsed.PlainText);
    }

    [Fact]
    public void 链接文字带格式时链接仍然保留()
    {
        var parsed = _parser.Parse("[**粗** 链接](https://ok.com/a)");

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        Assert.Equal("粗 链接", string.Concat(inlines.Select(i => i.Text)));
        Assert.All(inlines, i => Assert.Equal("https://ok.com/a", i.Link));
        Assert.Contains(inlines, i => i.Bold);
    }

    [Fact]
    public void 危险链接降级后子节点仍被消毒()
    {
        var parsed = _parser.Parse("[<javascript:alert(1)>](javascript:void(0))");

        var inlines = parsed.Blocks.SelectMany(b => b.Inlines ?? []).ToList();

        Assert.All(inlines, i => Assert.Null(i.Link));
        Assert.DoesNotContain(inlines, i => i.Text.Contains("javascript:void(0)"));
    }
}

public class DiffDocumentParserTests
{
    [Fact]
    public async Task 补丁文件被解析成增删行与修订()
    {
        var parser = new DiffDocumentParser();
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             keep
            -old
            +new
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(diff));
        var parsed = await parser.ParseAsync(stream, "change.patch", CancellationToken.None);

        Assert.Equal(DocFormat.UnifiedDiff.ToString(), parsed.Format);
        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.Delete && r.Text == "old");
        Assert.Contains(parsed.Revisions, r => r.Kind == RevisionKindName.Insert && r.Text == "new");
    }

    [Fact]
    public void hunk内以三横线开头的删除行不是文件头()
    {
        // 删掉一行 Markdown 分隔线 / YAML 文档起始符时，删除行长这样："---- xxx"
        var lines = DiffTextParser.ParseLines("""
            --- a/doc.md
            +++ b/doc.md
            @@ -1,3 +1,2 @@
             # 标题
            ---- 分隔线被删
             尾部
            """);

        var removed = lines.Single(l => l.Kind == DiffTextParser.Removed);
        Assert.Equal("--- 分隔线被删", removed.Text);
        Assert.Equal(2, removed.OldLine);

        // 真正的文件头还是文件头，而且不能被算成删除修订
        Assert.Equal(1, lines.Count(l => l.Kind == DiffTextParser.Header && l.Text.StartsWith("--- ")));
        Assert.Equal("--- a/doc.md", lines.First(l => l.Kind == DiffTextParser.Header).Text);

        // 后续行号不能被带偏
        var tail = lines.Single(l => l.Kind == DiffTextParser.Context && l.Text == "尾部");
        Assert.Equal(3, tail.OldLine);
        Assert.Equal(2, tail.NewLine);
    }

    [Fact]
    public void hunk内以三加号开头的新增行不是文件头()
    {
        var lines = DiffTextParser.ParseLines("""
            --- a/doc.md
            +++ b/doc.md
            @@ -1,1 +1,2 @@
             keep
            +++ 新增的内容以加号开头
            """);

        var added = lines.Single(l => l.Kind == DiffTextParser.Added);
        Assert.Equal("++ 新增的内容以加号开头", added.Text);
        Assert.Equal(2, added.NewLine);
    }

    [Fact]
    public void 多文件补丁的文件头仍然被识别()
    {
        var lines = DiffTextParser.ParseLines("""
            diff --git a/a.txt b/a.txt
            index 1111111..2222222 100644
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            +new
            diff --git a/b.txt b/b.txt
            --- a/b.txt
            +++ b/b.txt
            @@ -1 +1 @@
            -旧
            +新
            """);

        // 2 个文件头 + 2 个 diff + 1 个 index + 2 个 hunk 头
        Assert.Equal(2, lines.Count(l => l.Kind == DiffTextParser.Header && l.Text.StartsWith("--- ")));
        Assert.Equal(2, lines.Count(l => l.Kind == DiffTextParser.Header && l.Text.StartsWith("+++ ")));
        Assert.Equal(2, lines.Count(l => l.Kind == DiffTextParser.Removed));
        Assert.Equal(2, lines.Count(l => l.Kind == DiffTextParser.Added));
    }

    [Fact]
    public async Task hunk内的三横线删除行进修订清单()
    {
        var diff = """
            --- a/doc.md
            +++ b/doc.md
            @@ -1,2 +1,1 @@
             # 标题
            ---- 分隔线被删
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(diff));
        var parsed = await new DiffDocumentParser().ParseAsync(stream, "change.patch", CancellationToken.None);

        Assert.Contains(parsed.Revisions, r =>
            r.Kind == RevisionKindName.Delete && r.Text == "--- 分隔线被删");
    }
}
