using Workbench.Domain;

namespace Workbench.Application.Contracts;

public sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    double Progress,
    int TotalTasks,
    int DoneTasks,
    int BlockedTasks,
    int ActiveTasks,
    DateTimeOffset? LastActivityAt,
    bool Stale,
    string? RepoPath,
    string? Branch,
    string? RepoRemote,
    /// <summary>
    /// 规范化后的仓库根（绑定键）。为空表示项目还没绑仓库 ——
    /// agent 在这个仓库里干活就落不到本项目上。
    /// </summary>
    string? RepoRoot = null);

public sealed record TaskSummaryDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Title,
    TaskStatus Status,
    string? ClaimedBy,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastActivityAt,
    bool Stale,
    Guid? MilestoneId = null,
    string? Key = null,
    TaskType Type = TaskType.Requirement,
    string? Branch = null,
    bool IsArchived = false,
    /// <summary>负责人（人名或 agent id）。</summary>
    string? Assignee = null,
    /// <summary>优先级。</summary>
    TaskPriority Priority = TaskPriority.Normal,
    /// <summary>截止日。</summary>
    DateTimeOffset? DueAt = null,
    /// <summary>在项目进度里的权重。</summary>
    double Weight = 1d,
    /// <summary>还没完成的前置任务数。大于 0 表示这任务现在动不了。</summary>
    int PendingDependencies = 0);

/// <summary>项目配置。跟概览分开：概览是给人看的汇总，配置是给人和 agent 改的字段集合。</summary>
public sealed record ProjectConfigDto(
    Guid Id,
    string Name,
    string? Description,
    string? RepoPath,
    string? RepoRemote,
    string? DefaultBranch,
    IReadOnlyList<string> AllowedCommands,
    string? AgentCommand,
    string? NotificationWebhookUrl,
    string? NotificationFormat,
    /// <summary>规范化后的仓库根（绑定键）。为空表示项目还没绑仓库。</summary>
    string? RepoRoot = null);

public sealed record StatusEventDto(
    Guid Id,
    TaskStatus? From,
    TaskStatus? To,
    string Source,
    string? Actor,
    string? Note,
    DateTimeOffset CreatedAt,
    /// <summary>机器可读的事件类型（如 status.changed）。</summary>
    string? EventType = null,
    /// <summary>结构化载荷（JSON 字符串）。</summary>
    string? Payload = null);

public sealed record DocumentDto(
    Guid Id,
    string FileName,
    long SizeBytes,
    string Format,
    string? Kind,
    bool Parsed,
    DateTimeOffset UploadedAt,
    string? ParseError,
    string? StoredPath);

public sealed record ArtifactDto(
    Guid Id,
    string Type,
    string Title,
    string? Content,
    string? ExternalPath,
    string? CommitSha,
    string? Branch,
    string? Author,
    DateTimeOffset CreatedAt);

public sealed record PlanDto(
    Guid Id,
    int Version,
    string? Background,
    string? Understanding,
    string? Breakdown,
    string? AcceptanceCriteria,
    string? Risks,
    string? Author,
    DateTimeOffset CreatedAt);

public sealed record TestResultDto(
    Guid TaskId,
    int Total,
    int Passed,
    int Failed,
    double? Coverage,
    string? Failures,
    string? Author,
    DateTimeOffset ReportedAt);

public sealed record ApprovalDto(
    Guid Id,
    string ToolName,
    string Kind,
    string Summary,
    string Payload,
    string Status,
    string? RequestedBy,
    string? DecidedBy,
    string? DecisionReason,
    string? ResultSummary,
    Guid? TaskId,
    Guid? ProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    /// <summary>裁决人所用人工凭证的指纹（不存明文）。</summary>
    string? DecidedByCredential = null,
    /// <summary>
    /// 关联任务的编号与标题。
    ///
    /// 人批一条写操作时，判断该不该批的依据是「这是哪个需求的活」——
    /// 光有 TaskId（GUID）不行，界面上不许露 GUID，所以要把编号/标题带出来。
    /// </summary>
    string? TaskKey = null,
    string? TaskTitle = null,
    /// <summary>关联项目名。同理：项目给名字，给不出名字就空着。</summary>
    string? ProjectName = null);

public sealed record ProjectContextDto(
    Guid Id,
    string Name,
    string? RepoPath,
    string? Branch,
    string? HeadSha,
    bool Dirty,
    string? RepoRemote);

/// <summary>
/// 交给 agent 的一次性上下文包。设计目标是让 agent 一次调用就能开工，
/// 而不是连着调十几次。
/// </summary>
public sealed record TaskContextDto(
    TaskSummaryDto Task,
    ProjectContextDto Project,
    PlanDto? Plan,
    IReadOnlyList<DocumentDto> Documents,
    IReadOnlyList<ArtifactDto> Artifacts,
    IReadOnlyList<StatusEventDto> RecentEvents,
    IReadOnlyList<string> AllowedTransitions,
    /// <summary>仓库当前分支。跟任务绑定的分支不一致时，agent 不该贸然开跑。</summary>
    string? CurrentBranch = null,
    /// <summary>当前分支是否满足任务绑定的分支要求。</summary>
    bool BranchReady = true);

/// <summary>单个评审维度的结论。</summary>
public sealed record ReviewItemDto(
    Guid Id,
    string Dimension,
    string DimensionName,
    int Order,
    string Status,
    string? Findings,
    string? Evidence,
    string? Recommendation,
    string? Reviewer,
    DateTimeOffset? DecidedAt);

/// <summary>一轮评审。</summary>
public sealed record ReviewRoundDto(
    Guid Id,
    Guid TaskId,
    int Round,
    string Verdict,
    string? Summary,
    string? Reviewer,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ReviewItemDto> Items,
    /// <summary>开轮时的仓库 HEAD。为空表示老轮次或项目没配仓库。</summary>
    string? CommitHash = null);

/// <summary>评审维度的定义（检查清单）。</summary>
public sealed record ReviewDimensionDto(
    string Key,
    string Name,
    string Question,
    IReadOnlyList<string> Checklist);

/// <summary>
/// 给 agent 的评审包：维度定义 + 任务上下文 + 变更文件。
/// agent（或它开的子 agent）照着包里的清单逐维度评审，再逐维度回写结论。
/// </summary>
public sealed record ReviewPackDto(
    ReviewRoundDto Round,
    TaskSummaryDto Task,
    PlanDto? Plan,
    IReadOnlyList<ArtifactDto> Artifacts,
    IReadOnlyList<DocumentDto> Documents,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ReviewDimensionDto> Dimensions);

/// <summary>评审门禁状态。状态机用它决定 Review → Done 放不放行。</summary>
public sealed record ReviewGateDto(
    bool HasReview,
    string Verdict,
    IReadOnlyList<string> FailedDimensions,
    IReadOnlyList<string> PendingDimensions,
    /// <summary>
    /// 维度之外的拦截理由（如「评审后代码已变更」）。
    /// 非空即不放行 —— 门禁不只有维度这一道。
    /// </summary>
    string? BlockReason = null);

/// <summary>
/// 数据库连接配置。**不含密码**（明文与密文都不出去），只告诉调用方有没有配密码。
/// 跟项目配置一样，这是给人和 agent 看的字段集合，别直接把 EF 实体序列化出去。
/// </summary>
public sealed record DbConnectionConfigDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Server,
    string Database,
    string? UserId,
    bool IsDefault,
    bool HasPassword,
    /// <summary>是否信任服务端证书。默认 true（兼容既有连接），可显式关掉。</summary>
    bool TrustServerCertificate = true);

/// <summary>
/// 任务产出文件 —— 开发过程中实际改了哪些文件。
/// 工作台自己去 git 里数出来的，不是 agent 自报的。
/// </summary>
public sealed record TaskFileChangeDto(
    Guid Id,
    string Path,
    string ChangeKind,
    string? OldPath,
    int LinesAdded,
    int LinesDeleted,
    string? CommitSha,
    string? Branch,
    string? Author,
    DateTimeOffset CreatedAt);

/// <summary>
/// 一次数据库调用的记录。SQL 原文、目标库、目标表摊成列 —— 查得动，
/// 不用人肉展开审计里的 JSON。
/// </summary>
public sealed record DbCallRecordDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    Guid? TaskId,
    string? TaskKey,
    Guid? ConnectionId,
    string ToolName,
    string? Server,
    string? Database,
    string? Sql,
    string? Kind,
    IReadOnlyList<string> TargetTables,
    int? RowsAffected,
    string? Outcome,
    Guid? ApprovalId,
    string? CallerId,
    long DurationMs,
    DateTimeOffset CreatedAt);

/// <summary>
/// 搜索命中的产出文件。
///
/// 全局搜索要能答「哪个任务动过这个文件」—— 光给路径不够，
/// 得同时给出任务编号和项目名，否则人还得自己点进去确认。
/// </summary>
public sealed record FileChangeHitDto(
    Guid TaskId,
    string? TaskKey,
    string? TaskTitle,
    string ProjectName,
    string Path,
    string ChangeKind,
    int LinesAdded,
    int LinesDeleted,
    DateTimeOffset CreatedAt);

/// <summary>搜索命中的数据库调用。</summary>
public sealed record DbCallHitDto(
    Guid Id,
    Guid? TaskId,
    string? TaskKey,
    string? ProjectName,
    string? Database,
    string? Sql,
    string? Kind,
    string? Outcome,
    string? CallerId,
    DateTimeOffset CreatedAt);
