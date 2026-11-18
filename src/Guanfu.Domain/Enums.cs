namespace Guanfu.Domain;

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

/// <summary>
/// 任务类型。观复的结构是 项目 → 迭代版本 → 需求/缺陷/优化，
/// 类型决定了编号前缀和任务卡上的徽章。
/// </summary>
public enum TaskType
{
    /// <summary>需求（REQ）。</summary>
    Requirement = 0,

    /// <summary>缺陷（BUG）。</summary>
    Bug = 1,

    /// <summary>优化（OPT）。</summary>
    Optimization = 2
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
    Executed = 4,

    /// <summary>
    /// 已被占住、正在执行。令牌一旦进这个状态就不能再被别的请求拿去用了 ——
    /// 这是「一条审批只能执行一次」的落点。
    /// </summary>
    Executing = 5
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

/// <summary>任务优先级。排序与展示用，不参与状态机。</summary>
public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>里程碑的种类。版本 → 需求组 → 需求，三层结构靠这个表达。</summary>
public enum MilestoneKind
{
    /// <summary>需求组（子里程碑）。</summary>
    Group = 0,

    /// <summary>迭代版本（顶层里程碑）。</summary>
    Release = 1
}

/// <summary>任务依赖的方向。</summary>
public enum DependencyType
{
    /// <summary>本任务依赖另一个任务（被阻塞方 → 前置方）。</summary>
    BlockedBy = 0,

    /// <summary>本任务阻塞另一个任务（阻塞方 → 被阻塞方）。</summary>
    Blocks = 1
}
