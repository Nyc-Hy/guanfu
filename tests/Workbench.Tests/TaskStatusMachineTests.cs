using Workbench.Domain;

namespace Workbench.Tests;

public class TaskStatusMachineTests
{
    [Theory]
    [InlineData(TaskStatus.Draft, TaskStatus.DocsReady)]
    [InlineData(TaskStatus.DocsReady, TaskStatus.Planned)]
    [InlineData(TaskStatus.Planned, TaskStatus.InDev)]
    [InlineData(TaskStatus.InDev, TaskStatus.Testing)]
    [InlineData(TaskStatus.Testing, TaskStatus.Review)]
    [InlineData(TaskStatus.Review, TaskStatus.Done)]
    [InlineData(TaskStatus.Review, TaskStatus.InDev)]      // 打回回路
    [InlineData(TaskStatus.InDev, TaskStatus.Blocked)]     // 任意态可阻塞
    [InlineData(TaskStatus.Blocked, TaskStatus.InDev)]     // 解除阻塞
    public void 允许的迁移被接受(TaskStatus from, TaskStatus to)
    {
        Assert.True(TaskStatusMachine.CanTransition(from, to));
        TaskStatusMachine.EnsureTransition(from, to);
    }

    [Theory]
    [InlineData(TaskStatus.Draft, TaskStatus.InDev)]        // 跳步
    [InlineData(TaskStatus.Draft, TaskStatus.Done)]
    [InlineData(TaskStatus.Done, TaskStatus.InDev)]         // 终态不回退
    [InlineData(TaskStatus.Cancelled, TaskStatus.InDev)]
    public void 非法迁移被拒绝并带出允许列表(TaskStatus from, TaskStatus to)
    {
        Assert.False(TaskStatusMachine.CanTransition(from, to));

        var ex = Assert.Throws<InvalidStatusTransitionException>(
            () => TaskStatusMachine.EnsureTransition(from, to));

        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
        Assert.Equal(TaskStatusMachine.AllowedFrom(from), ex.Allowed);
        Assert.Contains("非法状态迁移", ex.Message);
    }

    [Fact]
    public void 进度权重单调不减()
    {
        var ordered = new[]
        {
            TaskStatus.Draft, TaskStatus.DocsReady, TaskStatus.Planned,
            TaskStatus.InDev, TaskStatus.Testing, TaskStatus.Review, TaskStatus.Done
        };

        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.True(
                TaskStatusMachine.ProgressWeight(ordered[i]) > TaskStatusMachine.ProgressWeight(ordered[i - 1]),
                $"{ordered[i]} 的权重应大于 {ordered[i - 1]}");
        }

        Assert.Equal(1d, TaskStatusMachine.ProgressWeight(TaskStatus.Done));
    }

    [Fact]
    public void 终态判定()
    {
        Assert.True(TaskStatusMachine.IsTerminal(TaskStatus.Done));
        Assert.True(TaskStatusMachine.IsTerminal(TaskStatus.Cancelled));
        Assert.False(TaskStatusMachine.IsTerminal(TaskStatus.Review));
    }
}
