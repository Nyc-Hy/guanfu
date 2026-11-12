namespace Workbench.Web.Components;

/// <summary>
/// 页面上的错误信息：一句人话 + 折叠起来的技术细节。
///
/// 为什么拆成两截：直接把 ex.Message 甩给用户，等于把 .NET 内部异常
/// （SqliteException、堆栈、绝对路径）原样暴露出去；但全吞掉又没人能排查。
/// 人话放显眼处，技术细节收进 &lt;details&gt;，出问题时展开就能看到。
/// </summary>
public sealed record PageError(string Message, string Details, bool IsError = true)
{
    /// <summary>把异常翻译成「人话 + 细节」。</summary>
    public static PageError From(Exception exception, string what)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new PageError(
            $"{what}失败，工作台服务可能出了点问题。请稍后重试；若一直不行，展开下面的技术细节。",
            exception.ToString());
    }

    /// <summary>不需要技术细节的错误提示（比如「凭证不对」）。</summary>
    public static PageError Simple(string message) => new(message, string.Empty);

    /// <summary>不是错误，只是要告诉用户一声（比如「已拉起外部 agent」）。</summary>
    public static PageError Notice(string message) => new(message, string.Empty, IsError: false);
}
