namespace Workbench.Domain;

/// <summary>任务状态。状态机见 <see cref="TaskStatusMachine"/>。</summary>
public enum TaskStatus
{
    Draft = 0,
    DocsReady = 1,
    Planned = 2,
    InDev = 3,
    Testing = 4,
    Review = 5,
    Done = 6,
    Blocked = 7,
    Cancelled = 8
}

/// <summary>事件来源，用于区分是人、agent 还是 CI 写进来的。</summary>
public enum TaskEventSource
{
    Human = 0,
    Agent = 1,
    Ci = 2,
    System = 3
}

public enum ArtifactType
{
    Plan = 0,
    DesignDoc = 1,
    CodeChange = 2,
    TestReport = 3,
    DeployRecord = 4,
    Screenshot = 5,
    Commit = 6,
    Document = 7,
    Other = 8
}

public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Executed = 4
}

/// <summary>文档格式。解析器按此分派。</summary>
public enum DocFormat
{
    Unknown = 0,
    Markdown = 1,
    Docx = 2,
    Pdf = 3,
    PlainText = 4,
    UnifiedDiff = 5,
    Json = 6,
    Html = 7
}

/// <summary>修订（补丁）类型。</summary>
public enum RevisionKind
{
    Insert = 0,
    Delete = 1,
    MoveFrom = 2,
    MoveTo = 3
}

/// <summary>列表类型，Task 表示带复选框的任务清单。</summary>
public enum ListKind
{
    Bullet = 0,
    Ordered = 1,
    Task = 2
}
