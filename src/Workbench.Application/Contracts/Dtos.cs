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
    string? RepoRemote);

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
    string? Branch = null);

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
    string? NotificationFormat);

public sealed record StatusEventDto(
    Guid Id,
    TaskStatus? From,
    TaskStatus? To,
    string Source,
    string? Actor,
    string? Note,
    DateTimeOffset CreatedAt);

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
    DateTimeOffset? DecidedAt);

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
    IReadOnlyList<ReviewItemDto> Items);

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
    IReadOnlyList<string> PendingDimensions);
