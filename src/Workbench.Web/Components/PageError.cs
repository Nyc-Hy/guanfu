namespace Workbench.Web.Components;

using Workbench.Domain;

/// <summary>
/// 页面上的错误信息：一句人话 + 折叠起来的技术细节。
///
/// 为什么拆成两截：直接把 ex.Message 甩给用户，等于把 .NET 内部异常
/// （SqliteException、堆栈、绝对路径）原样暴露出去；但全吞掉又没人能排查。
/// 人话放显眼处，技术细节收进 &lt;details&gt;，出问题时展开就能看到。
///
/// 业务异常（WorkbenchException / NotFoundException / 非法迁移…）是例外：
/// 它们的消息本来就是写给人看的（「非法状态迁移 X → Y。当前允许: …」），
/// 吞成「服务可能出了点问题」等于把自我纠正需要的信息藏起来 —— 原文直出。
/// </summary>
public sealed record PageError(string Message, string Details, bool IsError = true)
{
    /// <summary>把异常翻译成「人话 + 细节」。</summary>
    public static PageError From(Exception exception, string what)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is WorkbenchException)
        {
            // 业务异常的消息就是人话，原文展示；不附带堆栈（没有排查价值）
            return new PageError($"{what}失败：{exception.Message}", string.Empty);
        }

        return new PageError(
            $"{what}失败，工作台服务可能出了点问题。请稍后重试；若一直不行，展开下面的技术细节。",
            exception.ToString());
    }

    /// <summary>不需要技术细节的错误提示（比如「凭证不对」）。</summary>
    public static PageError Simple(string message) => new(message, string.Empty);

    /// <summary>不是错误，只是要告诉用户一声（比如「已拉起外部 agent」）。</summary>
    public static PageError Notice(string message) => new(message, string.Empty, IsError: false);
}
