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
    Guid? MilestoneId = null);

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
    IReadOnlyList<string> AllowedTransitions);
