using System.Text.Json;
using Workbench.Application;
using Workbench.Documents;

namespace Workbench.Tests;

/// <summary>
/// 解析产物的 JSON 往返。
/// 这一层出问题的话，文档缓存、MCP 结构化输出、页面渲染会一起挂，
/// 所以单独盯住。
/// </summary>
public class ParsedDocumentSerializationTests
{
    [Fact]
    public void 解析结果可以序列化并还原()
    {
        var parser = new MarkdownDocumentParser();
        var parsed = parser.Parse("""
            # 标题

            正常内容，~~已废弃~~。

            - [x] 做完了
            - [ ] 没做完
            """);

        var json = JsonSerializer.Serialize(parsed, WorkbenchJson.Options);
        var restored = JsonSerializer.Deserialize<ParsedDocDto>(json, WorkbenchJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(parsed.Title, restored!.Title);
        Assert.Equal(parsed.Blocks.Count, restored.Blocks.Count);

        var struck = restored.Blocks
            .SelectMany(b => b.Inlines ?? [])
            .Where(i => i.Strike)
            .Select(i => i.Text)
            .ToList();

        Assert.Contains(struck, t => t.Contains("已废弃"));
    }

    [Fact]
    public void 值类型成员用正确的忽略条件不会抛异常()
    {
        // 当年这条会炸：非空值类型成员上用 WhenWritingNull 是非法的
        var block = new ParsedBlockDto(BlockKind.Paragraph, Level: 0, IsDiff: false, Ordered: false);

        var json = JsonSerializer.Serialize(block, WorkbenchJson.Options);

        Assert.DoesNotContain("level", json);
        Assert.Contains("\"kind\":\"paragraph\"", json);
    }

    // ------------------------------------------------------------------
    // 批注块新增的 Author / Date 是可选字段：老缓存里没有它们也要能读
    // ------------------------------------------------------------------

    [Fact]
    public void 批注块的作者与时间可以序列化并还原()
    {
        var block = new ParsedBlockDto(
            BlockKind.Comment,
            Inlines: [new ParsedInlineDto("这里要改")],
            Author: "评审人",
            Date: new DateTimeOffset(2026, 11, 12, 15, 4, 0, TimeSpan.FromHours(8)));

        var json = JsonSerializer.Serialize(block, WorkbenchJson.Options);
        var restored = JsonSerializer.Deserialize<ParsedBlockDto>(json, WorkbenchJson.Options);

        Assert.NotNull(restored);
        Assert.Equal("评审人", restored!.Author);
        Assert.Equal(block.Date, restored.Date);
        Assert.Equal("这里要改", restored.Inlines![0].Text);
    }

    [Fact]
    public void 老缓存JSON没有新字段也能读()
    {
        // 修复前写进库的缓存长这样：没有 author / date
        const string json = """{"kind":"paragraph","inlines":[{"text":"旧内容","strike":true}]}""";

        var restored = JsonSerializer.Deserialize<ParsedBlockDto>(json, WorkbenchJson.Options);

        Assert.NotNull(restored);
        Assert.Null(restored!.Author);
        Assert.Null(restored.Date);
        Assert.True(restored.Inlines![0].Strike);
    }

    [Fact]
    public void 新字段为空时不写进JSON()
    {
        var json = JsonSerializer.Serialize(new ParsedBlockDto(BlockKind.Paragraph), WorkbenchJson.Options);

        Assert.DoesNotContain("author", json);
        Assert.DoesNotContain("date", json);
    }

    [Fact]
    public void 表格与修订一起序列化不会丢结构()
    {
        var parsed = new MarkdownDocumentParser().Parse("""
            | 字段 | 说明 |
            | --- | --- |
            | id | ~~旧字段~~ 新字段 |
            """);

        var json = JsonSerializer.Serialize(parsed, WorkbenchJson.Options);
        var restored = JsonSerializer.Deserialize<ParsedDocDto>(json, WorkbenchJson.Options);

        var table = restored!.Blocks.Single(b => b.Kind == BlockKind.Table);
        Assert.Equal(2, table.Rows!.Count);
        Assert.Contains(table.Rows[1][1], i => i is { Text: "旧字段", Strike: true });
        Assert.Contains("id\t~~旧字段~~ 新字段", restored.PlainText);
    }
}
