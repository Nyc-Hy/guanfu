namespace Workbench.Domain;

/// <summary>工作区。单机默认一个，schema 预留多租户。</summary>
public class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "默认工作区";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Project> Projects { get; set; } = [];
}

/// <summary>项目。工作台的核心管理单元。</summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // ---- Git 托管（决策 6）----
    public string? RepoPath { get; set; }
    public string? RepoRemote { get; set; }
    public string? DefaultBranch { get; set; } = "main";
    public bool ManagedByWorkbench { get; set; }

    /// <summary>允许 shell.run 执行的命令前缀（JSON 数组）。不在白名单里的一律拒绝。</summary>
    public string? AllowedCommandsJson { get; set; }

    /// <summary>启动 agent 的命令模板，支持 {taskId} {title} {project} {repoPath} 占位符。</summary>
    public string? AgentCommand { get; set; }

    /// <summary>通知推送地址（钉钉/飞书群机器人 webhook 或通用 webhook）。</summary>
    public string? NotificationWebhookUrl { get; set; }

    /// <summary>通知格式：dingtalk / feishu / generic。</summary>
    public string? NotificationFormat { get; set; } = "generic";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TaskItem> Tasks { get; set; } = [];
    public List<Milestone> Milestones { get; set; } = [];
    public List<DbConnectionConfig> DbConnections { get; set; } = [];
}

/// <summary>里程碑：项目内的一批任务归组。</summary>
public class Milestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public int Order { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TaskItem> Tasks { get; set; } = [];
}

/// <summary>需求任务。</summary>
public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>父任务（模块级拆分）。为空表示顶层任务。</summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>所属里程碑。为空表示不在任何里程碑下。</summary>
    public Guid? MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    /// <summary>
    /// 项目内可读编号（如 REQ-001）。
    /// agent 是听人话来的 ——「看下 req001」这种话要能落地，得有个稳定的短标识。
    /// </summary>
    public string? Key { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public TaskStatus Status { get; set; } = TaskStatus.Draft;

    /// <summary>在项目进度里的权重，默认 1。</summary>
    public double Weight { get; set; } = 1d;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最后一次有动静的时间，用于停滞检测。</summary>
    public DateTimeOffset? LastActivityAt { get; set; }

    // ---- 认领租约（决策 4：agent 自己领）----
    public string? ClaimedByAgentId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public List<StatusEvent> StatusEvents { get; set; } = [];
    public List<Document> Documents { get; set; } = [];
    public List<Artifact> Artifacts { get; set; } = [];
    public List<Plan> Plans { get; set; } = [];
    public List<TestRun> TestRuns { get; set; } = [];
    public List<ReviewRound> ReviewRounds { get; set; } = [];
}

/// <summary>状态流水。append-only，谁在什么时候把状态改成了什么。</summary>
public class StatusEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public TaskStatus? FromStatus { get; set; }
    public TaskStatus? ToStatus { get; set; }
    public TaskEventSource Source { get; set; } = TaskEventSource.System;
    public string? Actor { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>上传的文档原件（PRD / 测试用例 / 接口文档等）。</summary>
public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public string FileName { get; set; } = string.Empty;
    /// <summary>相对 WORKBENCH_HOME 的存储路径。</summary>
    public string StoredPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public DocFormat Format { get; set; } = DocFormat.Unknown;

    /// <summary>业务分类：prd / testcase / apidoc / other。</summary>
    public string? Kind { get; set; }

    public bool Parsed { get; set; }
    public string? ParseError { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>解析产物缓存。存结构化 JSON，避免重复解析。</summary>
public class DocumentParse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    public DocFormat Format { get; set; } = DocFormat.Unknown;
    public int ParserVersion { get; set; } = 1;
    public string Json { get; set; } = string.Empty;
    public DateTimeOffset ParsedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>需求理解 + 开发计划。由 agent 回写，可多版本。</summary>
public class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public int Version { get; set; } = 1;

    public string? Background { get; set; }
    public string? Understanding { get; set; }
    public string? Breakdown { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public string? Risks { get; set; }

    public string? Author { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>一次单元测试的结果（结构化，度量看板要用）。</summary>
public class TestRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public double? Coverage { get; set; }
    public string? Failures { get; set; }
    public string? Author { get; set; }
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>开发过程产物。</summary>
public class Artifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public ArtifactType Type { get; set; } = ArtifactType.Other;
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ExternalPath { get; set; }

    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public string? Author { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>审批请求。写操作、git 写操作等必须先过这里。</summary>
public class Approval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public string ToolName { get; set; } = string.Empty;
    /// <summary>审批类别：db.write / git.write / ...</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>请求参数 JSON（含 SQL 原文）。</summary>
    public string Payload { get; set; } = string.Empty;
    /// <summary>给审批人看的一句话摘要。</summary>
    public string Summary { get; set; } = string.Empty;

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? RequestedBy { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
    public string? ResultSummary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>工具调用审计（append-only）。</summary>
public class ToolCallAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ToolName { get; set; } = string.Empty;
    public string? CallerId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? TaskId { get; set; }

    public string Arguments { get; set; } = string.Empty;
    public bool RequiredApproval { get; set; }
    public Guid? ApprovalId { get; set; }

    /// <summary>success / denied / error / pending</summary>
    public string Outcome { get; set; } = string.Empty;
    public string? ResultSummary { get; set; }
    public int? RowsAffected { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>项目数据库连接（SQL Server）。</summary>
public class DbConnectionConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Name { get; set; } = "default";
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string? UserId { get; set; }

    /// <summary>AES-GCM 加密后的密码（密文，非明文）。</summary>
    public string? EncryptedPassword { get; set; }

    public bool TrustServerCertificate { get; set; } = true;
    public bool IsDefault { get; set; } = true;

    public List<DbTableRule> Rules { get; set; } = [];
}

/// <summary>
/// MCP 工具开关。关掉的工具不再出现在 tools/list，调用也会被拒。
/// 只存「被显式改过的」——没记录的一律按开启处理，升级加新工具不用改数据。
/// </summary>
public class McpToolSetting
{
    public string ToolName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 评审轮次。一次开发完成后的一轮「专家团评审」。
///
/// 一轮 = 若干个维度（见 <see cref="ReviewDimensions"/>），每个维度独立出结论。
/// 多轮保留，形成可追溯的评审历史。
/// </summary>
public class ReviewRound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    /// <summary>第几轮（从 1 开始）。</summary>
    public int Round { get; set; }

    /// <summary>汇总结论：全部维度通过才算通过。</summary>
    public ReviewVerdict Verdict { get; set; } = ReviewVerdict.Pending;

    /// <summary>总体说明（agent 汇总的话）。</summary>
    public string? Summary { get; set; }

    public string? Reviewer { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public List<ReviewItem> Items { get; set; } = [];
}

/// <summary>某个维度的评审结论。</summary>
public class ReviewItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReviewRoundId { get; set; }
    public ReviewRound? Round { get; set; }

    public Guid TaskId { get; set; }

    /// <summary>维度标识（见 <see cref="ReviewDimensions"/>）。</summary>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>维度中文名（存一份，历史记录不依赖代码里的定义）。</summary>
    public string DimensionName { get; set; } = string.Empty;

    public int Order { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    /// <summary>发现的问题。</summary>
    public string? Findings { get; set; }

    /// <summary>证据（文件路径、行号、片段）。</summary>
    public string? Evidence { get; set; }

    /// <summary>建议怎么改。</summary>
    public string? Recommendation { get; set; }

    /// <summary>谁评的（agent id / 子 agent 名）。</summary>
    public string? Reviewer { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>表级白名单规则。写操作一律需要审批，此处只控制是否可免审批。</summary>
public class DbTableRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public DbConnectionConfig? Connection { get; set; }

    public string SchemaName { get; set; } = "dbo";
    public string TableName { get; set; } = string.Empty;
    public bool AllowRead { get; set; } = true;

    /// <summary>true = 写也需要人工审批（默认）。false = 免审批写。</summary>
    public bool WriteRequiresApproval { get; set; } = true;
}
