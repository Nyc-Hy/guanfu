namespace Workbench.Domain;

public class WorkbenchException : Exception
{
    public WorkbenchException(string message) : base(message) { }
    public WorkbenchException(string message, Exception inner) : base(message, inner) { }
}

public class NotFoundException : WorkbenchException
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
public class InvalidStatusTransitionException : WorkbenchException
{
    public InvalidStatusTransitionException(
        TaskStatus from,
        TaskStatus to,
        IReadOnlyList<TaskStatus> allowed)
        : base($"非法状态迁移 {from} → {to}。当前允许: {string.Join(", ", allowed)}")
    {
        From = from;
        To = to;
        Allowed = allowed;
    }

    public TaskStatus From { get; }
    public TaskStatus To { get; }
    public IReadOnlyList<TaskStatus> Allowed { get; }
}
