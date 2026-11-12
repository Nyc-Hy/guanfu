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
}
