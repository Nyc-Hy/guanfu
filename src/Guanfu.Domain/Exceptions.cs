namespace Guanfu.Domain;

public class GuanfuException : Exception
{
    public GuanfuException(string message) : base(message) { }
    public GuanfuException(string message, Exception inner) : base(message, inner) { }
}

public class NotFoundException : GuanfuException
{
    public NotFoundException(string what, object key)
        : base($"{what} 未找到: {key}")
    {
        What = what;
        Key = key;
    }

    public string What { get; }
    public object Key { get; }
}

/// <summary>非法状态迁移。带「当前允许的迁移列表」，方便 agent 自我纠正。</summary>
public class InvalidStatusTransitionException : GuanfuException
{
    public InvalidStatusTransitionException(
        TaskStatus from,
        TaskStatus to,
        IReadOnlyList<TaskStatus> allowed)
        : this(from, to, allowed, $"非法状态迁移 {from} → {to}。当前允许: {string.Join(", ", allowed)}")
    {
    }

    protected InvalidStatusTransitionException(
        TaskStatus from,
        TaskStatus to,
        IReadOnlyList<TaskStatus> allowed,
        string message)
        : base(message)
    {
        From = from;
        To = to;
        Allowed = allowed;
    }

    public TaskStatus From { get; }
    public TaskStatus To { get; }
    public IReadOnlyList<TaskStatus> Allowed { get; }
}

/// <summary>
/// 并发冲突：读到状态之后、写回去之前，别的请求已经把状态改掉了。
///
/// 继承 <see cref="InvalidStatusTransitionException"/> 是刻意的 ——
/// 调用方（MCP/CLI）已经在按「迁移被拒 + 当前允许列表」处理这一类错误，
/// 冲突同样要带上**当前真实状态**和允许列表，agent 才能自我纠正。
/// </summary>
public class StatusConflictException : InvalidStatusTransitionException
{
    public StatusConflictException(
        TaskStatus current,
        TaskStatus requested,
        IReadOnlyList<TaskStatus> allowed)
        : base(current, requested, allowed,
            $"任务状态已被其他请求改为 {current}，本次「{current} → {requested}」没有生效。" +
            $"当前允许: {string.Join(", ", allowed)}。请重新读取任务后再决定下一步。")
    {
    }
}
