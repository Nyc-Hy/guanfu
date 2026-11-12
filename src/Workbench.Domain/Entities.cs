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
