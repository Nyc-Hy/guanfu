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
}
