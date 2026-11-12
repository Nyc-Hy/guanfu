using Workbench.Domain;

namespace Workbench.Documents;

/// <summary>
/// 解析失败的统一表示。
///
/// 解析器自己兜住包格式异常（损坏的 docx / pdf）后，
/// 错误信息通过 <see cref="ParsedDocDto.Error"/> 回传上层，
/// 上层据此把 Document.Parsed 置 false、ParseError 写进库 ——
/// 页面显示「为什么没内容」，而不是让异常穿出去变成 500。
/// </summary>
internal static class ParseFailure
{
    public static ParsedDocDto Create(string fileName, DocFormat format, Exception ex, string hint)
    {
        var title = Path.GetFileNameWithoutExtension(fileName);

        return new ParsedDocDto(
            string.IsNullOrEmpty(title) ? "未命名文档" : title,
            format.ToString(),
            [],
            [],
            [],
            string.Empty)
        {
            Error = $"{hint}：{ex.Message}"
        };
    }
}
