using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 界面文案的约定。
///
/// 这些映射以前散在各个页面里各写一套 —— 度量页写「已规划」「阻塞」，
/// 别的页写「已计划」「受阻」，同一个状态两种说法，人会以为是两个状态。
/// 现在统一到 UiText，这里钉住，别再分叉。
/// </summary>
public class UiTextTests
{
    [Theory]
    [InlineData(TaskStatus.Draft, "草稿")]
    [InlineData(TaskStatus.DocsReady, "文档就绪")]
    [InlineData(TaskStatus.Planned, "已计划")]
    [InlineData(TaskStatus.InDev, "开发中")]
    [InlineData(TaskStatus.Testing, "测试中")]
    [InlineData(TaskStatus.Review, "评审中")]
    [InlineData(TaskStatus.Done, "已完成")]
    [InlineData(TaskStatus.Blocked, "受阻")]
    [InlineData(TaskStatus.Cancelled, "已取消")]
    public void 任务状态有唯一的中文说法(TaskStatus status, string expected)
    {
        Assert.Equal(expected, UiText.Status(status));
    }

    [Fact]
    public void 每个任务状态都有中文说法()
    {
        // 漏一个枚举值，界面上就会露出代码标识（规矩：界面不许露代码标识）
        foreach (var status in Enum.GetValues<TaskStatus>())
        {
            var text = UiText.Status(status);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(status.ToString(), text);
        }
    }

    [Theory]
    [InlineData("Planned", "已计划")]
    [InlineData("Review", "评审中")]
    [InlineData("Blocked", "受阻")]
    [InlineData("Cancelled", "已取消")]
    public void 字符串形式的状态也认得中文说法(string status, string expected)
    {
        // 度量页拿到的就是枚举名字符串，不能因为走了字符串路径就换个说法
        Assert.Equal(expected, UiText.Status(status));
    }

    [Fact]
    public void 认不出来的状态原样透出而不是瞎编()
    {
        Assert.Equal("??", UiText.Status("??"));
    }

    [Theory]
    [InlineData(TaskStatus.Draft, "wb-status-neutral")]
    [InlineData(TaskStatus.DocsReady, "wb-status-neutral")]
    [InlineData(TaskStatus.Planned, "wb-status-neutral")]
    [InlineData(TaskStatus.InDev, "wb-status-active")]
    [InlineData(TaskStatus.Testing, "wb-status-active")]
    [InlineData(TaskStatus.Review, "wb-status-review")]
    [InlineData(TaskStatus.Done, "wb-status-ok")]
    [InlineData(TaskStatus.Blocked, "wb-status-danger")]
    [InlineData(TaskStatus.Cancelled, "wb-status-muted")]
    public void 任务状态有对应的展示色(TaskStatus status, string expected)
    {
        Assert.Equal(expected, UiText.StatusClass(status));
    }

    [Fact]
    public void 状态色不能为空()
    {
        foreach (var status in Enum.GetValues<TaskStatus>())
        {
            Assert.StartsWith("wb-status-", UiText.StatusClass(status));
        }
    }

    [Fact]
    public void 字符串形式的状态色跟枚举一致()
    {
        foreach (var status in Enum.GetValues<TaskStatus>())
        {
            Assert.Equal(UiText.StatusClass(status), UiText.StatusClass(status.ToString()));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("??")]
    public void 认不出来的状态色退回中性色(string? status)
    {
        Assert.Equal("wb-status-neutral", UiText.StatusClass(status));
    }
}
