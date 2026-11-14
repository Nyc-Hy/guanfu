using Markdig;

namespace Workbench.Documents;

/// <summary>
/// 渲染与解析共用的 Markdig pipeline 构造器。
///
/// **刻意不用 <c>UseAdvancedExtensions()</c>**：它最后一行是 <c>UseGenericAttributes()</c>，
/// 而 GenericAttributes 会把 <c>{onerror=alert(1)}</c> 这类花括号属性直接挂到
/// 生成的 HTML 标签上 —— <c>DisableHtml()</c> 只管裸 HTML，管不到这条路径。
/// 实测 <c>![a](https://e/a.png){onerror=alert(1)}</c> 会渲染出
/// <c>&lt;img src="..." onerror="alert(1)"&gt;</c>，是实打实的 XSS。
///
/// 所以这里把 Markdig 1.6.0 的 <c>UseAdvancedExtensions()</c> 逐项摊开，
/// **只去掉 GenericAttributes**，其余扩展与顺序保持一致（删除线靠 EmphasisExtras，
/// 表格靠 PipeTables / GridTables，任务清单靠 TaskLists，脚注靠 Footnotes）。
/// 渲染器与解析器共用同一份，避免两边语法支持漂移。
///
/// 升级 Markdig 时要重新核对 <c>UseAdvancedExtensions()</c> 的清单：
/// 新增的扩展要显式补进来，GenericAttributes 永远不要加回去。
/// </summary>
internal static class MarkdownPipelineFactory
{
    /// <summary>
    /// 与 <c>UseAdvancedExtensions()</c> 等价、但不含 GenericAttributes 的扩展链。
    /// 调用方按需再叠加 <c>DisableHtml()</c> 之类的开关。
    /// </summary>
    public static MarkdownPipelineBuilder CreateBuilder() =>
        new MarkdownPipelineBuilder()
            .UseAlertBlocks()
            .UseAbbreviations()
            .UseAutoIdentifiers()
            .UseCitations()
            .UseCustomContainers()
            .UseDefinitionLists()
            .UseEmphasisExtras()   // 删除线 ~~x~~ / 下划线 ++x++ / 上标下标
            .UseFigures()
            .UseFooters()
            .UseFootnotes()
            .UseGridTables()
            .UseMathematics()
            .UseMediaLinks()
            .UsePipeTables()
            .UseListExtras()
            .UseTaskLists()
            .UseDiagrams()
            .UseAutoLinks();
}