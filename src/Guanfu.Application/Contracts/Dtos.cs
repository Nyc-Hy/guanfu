using Guanfu.Domain;

namespace Guanfu.Application.Contracts;

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
    /// <summary>所属工作区（工作区级规约是所有项目通用的底线）。</summary>
    Guid WorkspaceId,
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
    int PendingDependencies = 0,
    /// <summary>agent 干活的工作目录（自己的 git worktree）。为空 = 用项目主工作区。</summary>
    string? WorkPath = null);

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
    string? RepoRoot = null,
    /// <summary>
    /// 仓库相关的提醒。
    ///
    /// 两件事会提醒人：
    /// ① 有别的项目也绑了同一个仓库 —— 同一个仓库绑两个项目是允许的
    ///    （前后端同仓可以各绑各的子目录），但**完全相同**的目录会让
    ///    「agent 按目录认项目」分不出来，那时必须显式指定 projectId；
    /// ② 目录现在还不存在 —— agent 在这里干活前要先把它建出来或 clone 进来。
    ///
    /// 在绑定的那一刻就讲清楚，别等 agent 撞了墙才发现。
    /// </summary>
    string? RepoWarning = null,
    /// <summary>同时最多几个 agent 能领活（0 = 不限）。</summary>
    int MaxConcurrentAgents = 0,
    /// <summary>每个任务最多能调多少次工具（0 = 不限）。</summary>
    int MaxToolCallsPerTask = 0);

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
    string? Payload = null,
    /// <summary>实际发生时间（补报时与记录时间不同）。为空表示没补报。</summary>
    DateTimeOffset? OccurredAt = null);

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

/// <summary>批量上传里的一个文件。</summary>
public sealed record DocumentUploadInput(string FileName, Stream Content, string? Kind = null);

/// <summary>批量上传里失败的一个文件 —— 带文件名与原因，人才知道该补哪一个。</summary>
public sealed record DocumentBatchFailureDto(string FileName, string Error);

/// <summary>批量上传结果：成功的与失败的分开摆。</summary>
public sealed record DocumentBatchDto(
    IReadOnlyList<DocumentDto> Uploaded,
    IReadOnlyList<DocumentBatchFailureDto> Failed);

public sealed record ArtifactDto(
    Guid Id,
    string Type,
    string Title,
    string? Content,
    string? ExternalPath,
    string? CommitSha,
    string? Branch,
    string? Author,
    DateTimeOffset CreatedAt,
    int Revision = 1,
    DateTimeOffset? UpdatedAt = null,
    string? EditedBy = null);

/// <summary>产物的一个历史版本。</summary>
public sealed record ArtifactRevisionDto(
    Guid Id,
    int Version,
    string? Title,
    string? Content,
    string? Editor,
    string? Note,
    DateTimeOffset CreatedAt);

/// <summary>
/// 交付打包的候选项：文档或产物。
///
/// <see cref="ItemId"/> 是条目 id（文档 id 或产物 id），
/// <see cref="Kind"/> 区分它是文档还是产物（"document" / "artifact"）。
/// </summary>
public sealed record DeliveryCandidateDto(
    Guid ItemId,
    string Kind,
    string Title,
    string? FileName,
    string? KindLabel,
    long SizeBytes,
    string TaskKey,
    string TaskTitle,
    bool HasContent);

/// <summary>一个迭代版本下能打包的东西（候选项 + 已打过的包）。</summary>
public sealed record DeliveryCandidatesDto(
    Guid MilestoneId,
    string MilestoneName,
    string ProjectName,
    IReadOnlyList<DeliveryCandidateDto> Candidates);

/// <summary>一次交付打包。</summary>
public sealed record DeliveryPackageDto(
    Guid Id,
    Guid ProjectId,
    Guid MilestoneId,
    string FileName,
    long SizeBytes,
    int ItemCount,
    IReadOnlyList<DeliveryPackageItemDto> Items,
    string? Manifest,
    string CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>清单里的一条。</summary>
public sealed record DeliveryPackageItemDto(
    string Kind,
    string Title,
    string? FileName,
    string TaskKey,
    string TaskTitle,
    long SizeBytes);

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
    string? ProjectName = null,
    /// <summary>
    /// 执行结果未知（服务重启导致执行中断，那次写操作可能已经生效）。
    /// 跟「执行失败」不是一回事：失败的可以重试，未知的要先人工确认。
    /// </summary>
    bool OutcomeUnknown = false);

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
    bool BranchReady = true,
    /// <summary>
    /// 工位现状：主工作区被谁占着、并行上限、这个任务登记的工位。
    /// agent 领活前要看 —— 不然只能靠踩文件发现有人在同目录干活。
    /// </summary>
    WorkStationDto? Station = null,
    /// <summary>
    /// 项目级规约（人定的规矩）。agent 开工前要读 ——
    /// 「什么算做完」「提交怎么写」「测试怎么跑」都在这儿。
    /// </summary>
    ProjectSpecDto? Spec = null);

/// <summary>
/// 交接包：这个活现在是什么样、上一位留下了什么。
///
/// 交接的难点不是「把文件传过去」，是**接手的人不知道该从哪儿下手**：
/// 上一个 agent 做到哪一步、为什么卡住、哪些坑别再踩。
/// 这些散在计划、文档、产物、流水、租约里 —— 打包成一份，接手的人一次拿全。
///
/// 只放**结构化事实** —— 观复不生成自然语言总结（它不调模型，也不该假装会）。
/// </summary>
public sealed record HandoffPackDto(
    TaskSummaryDto Task,
    ProjectContextDto Project,
    PlanDto? Plan,
    IReadOnlyList<DocumentDto> Documents,
    IReadOnlyList<ArtifactDto> Artifacts,
    TestResultDto? LastTestResult,
    ReviewRoundDto? Review,
    WorkStationDto? Station,
    ProjectSpecDto? Spec,
    IReadOnlyList<StatusEventDto> RecentEvents,
    IReadOnlyList<string> AllowedTransitions,
    HandoffInfoDto? Handoff,
    /// <summary>交接链：这个活转过几次手，每次谁交给谁、为什么。</summary>
    IReadOnlyList<HandoffStepDto> HandoffChain,
    DateTimeOffset GeneratedAt);

/// <summary>交接链的一步：谁交给谁、为什么、什么时候。</summary>
public sealed record HandoffStepDto(
    /// <summary>什么时候发生的。</summary>
    DateTimeOffset At,
    /// <summary>谁交出去的（null = 第一次接手，前面没人）。</summary>
    string? From,
    /// <summary>谁接手的（null = 放下了，还没别人接手）。</summary>
    string? To,
    /// <summary>为什么交出来。</summary>
    string? Reason);

/// <summary>上一个是谁、为什么交出来、现在在谁手上。</summary>
public sealed record HandoffInfoDto(
    /// <summary>上一个持有者（agent id）。</summary>
    string? PreviousHolder,
    /// <summary>为什么交出来（他放手时写的理由，或接活时留的上下文）。</summary>
    string? Reason,
    /// <summary>交接发生的时间。</summary>
    DateTimeOffset? At,
    /// <summary>现在的持有者（没人持有就是 null）。</summary>
    string? CurrentHolder,
    /// <summary>现在持有者的租约到期时间。</summary>
    DateTimeOffset? LeaseExpiresAt);

/// <summary>工位（工作区）现状。</summary>
public sealed record WorkStationDto(
    /// <summary>项目主工作区（仓库根）。为空 = 项目没绑仓库，没有文件系统面。</summary>
    string? MainCheckout,
    /// <summary>主工作区现在被谁占着（agentId）。没人占就是 null。</summary>
    string? MainCheckoutHolder,
    /// <summary>占着主工作区的那个任务编号。</summary>
    string? MainCheckoutTaskKey,
    /// <summary>这个项目的并行上限（0 = 不限）。</summary>
    int MaxConcurrentAgents = 0,
    /// <summary>项目里当前活着的租约数。</summary>
    int ActiveLeases = 0,
    /// <summary>这个任务登记的工位（null = 主工作区）。</summary>
    string? WorkPath = null,
    /// <summary>主工作区现在能不能用（没被**别人**占着就是 true）。</summary>
    bool MainCheckoutAvailable = true,
    /// <summary>这次调用该用的目录（登记的工位优先，回退主工作区）。</summary>
    string? EffectiveWorkPath = null,
    /// <summary>该用的目录是不是独立 worktree（true = 不跟别人共用）。</summary>
    bool Isolated = false,
    /// <summary>建议的独立工位路径（主工作区被占时用它）。</summary>
    string? SuggestedWorktree = null,
    /// <summary>建议的分支名。</summary>
    string? SuggestedBranch = null,
    /// <summary>
    /// 建这个工位的确切命令。**观复不替 agent 建 worktree** ——
    /// 它自己仓库里的事，观复只给路径与命令，外加事后校验。
    /// </summary>
    IReadOnlyList<string>? SetupCommands = null);

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
    DateTimeOffset? DecidedAt,
    /// <summary>谁认领了这个维度的评审（多 agent 分工）。为空 = 没人认领。</summary>
    string? ClaimedBy = null,
    /// <summary>认领的租约到期时间。</summary>
    DateTimeOffset? ClaimedUntil = null);

/// <summary>一轮评审。</summary>
/// <summary>
/// 一轮评审的分工与结论：谁评了哪一条、结论是什么。
///
/// 一轮七个维度可以拆给几个 agent 各评各的 —— 收尾的人（协调者）要一眼看出：
/// 谁还没交、哪条不通过、每条是谁说的。
/// </summary>
public sealed record ReviewJudgementDto(
    /// <summary>维度标识。</summary>
    string Dimension,
    /// <summary>维度中文名。</summary>
    string DimensionName,
    /// <summary>结论：Pending / Passed / Failed / Skipped。</summary>
    string Status,
    /// <summary>谁评的（agent id / 子 agent 名）。</summary>
    string? Reviewer);

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
    string? CommitHash = null,
    /// <summary>本轮的分工与结论（谁评了哪一条）。</summary>
    IReadOnlyList<ReviewJudgementDto>? Judgements = null,
    /// <summary>还没给结论的维度数。</summary>
    int PendingCount = 0,
    /// <summary>不通过的维度数。</summary>
    int FailedCount = 0);

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
    IReadOnlyList<ReviewDimensionDto> Dimensions,
    /// <summary>项目级规约（人定的规矩）—— 评审时的尺子。</summary>
    ProjectSpecDto? Spec = null,
    /// <summary>
    /// 规约（DoD）的逐条判定。维度是通用的，规约是这个项目特有的 ——
    /// 评审时要一条一条对着判，没判的算「待判定」。
    /// </summary>
    IReadOnlyList<SpecItemDto>? SpecItems = null);

/// <summary>规约里的一条 DoD，以及它在当前轮的判定。</summary>
public sealed record SpecItemDto(
    /// <summary>条目的稳定键（归一化形式）。</summary>
    string ItemKey,
    /// <summary>条目原文。</summary>
    string Text,
    /// <summary>Pending / Passed / Failed / Skipped。</summary>
    string Status,
    string? Note,
    string? Reviewer,
    DateTimeOffset? DecidedAt,
    /// <summary>谁认领了这条的判定（多 agent 分工）。为空 = 没人认领。</summary>
    string? ClaimedBy = null,
    /// <summary>认领的租约到期时间。</summary>
    DateTimeOffset? ClaimedUntil = null,
    /// <summary>
    /// 这条要求是哪一层来的：<see cref="SpecSource.Workspace"/>（所有项目通用）/
    /// <see cref="SpecSource.Project"/>（这个项目通用）/<see cref="SpecSource.Milestone"/>（这个版本特有）。
    /// 判的时候该知道：里程碑特有的要求只对这个版本里的活成立。
    /// </summary>
    string? Source = null);

/// <summary>生效的完成定义里的一条，带来源层级。</summary>
public sealed record EffectiveDoDItem(string Text, string Source);

/// <summary>完成定义的来源层级。</summary>
public static class SpecSource
{
    /// <summary>工作区级：所有项目通用的底线。</summary>
    public const string Workspace = "workspace";

    /// <summary>项目级：这个项目里所有活都适用。</summary>
    public const string Project = "project";

    /// <summary>里程碑级：只对这个版本里的活成立。</summary>
    public const string Milestone = "milestone";

    /// <summary>界面上怎么写（人话，不露内部标识）。</summary>
    public static string Label(string? source) => source switch
    {
        Workspace => "工作区通用",
        Project => "项目通用",
        Milestone => "本版本特有",
        _ => "未标注"
    };
}

/// <summary>一条 DoD 判定。</summary>
public sealed record SpecVerdictDto(
    Guid Id,
    Guid TaskId,
    string ItemKey,
    string Text,
    string Status,
    string? Note,
    string? Reviewer,
    DateTimeOffset? DecidedAt,
    /// <summary>谁认领了这条的判定（多 agent 分工）。为空 = 没人认领。</summary>
    string? ClaimedBy = null,
    /// <summary>认领的租约到期时间。</summary>
    DateTimeOffset? ClaimedUntil = null);

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
    string? BlockReason = null,
    /// <summary>规约里还没判定的 DoD 条目。</summary>
    IReadOnlyList<string>? PendingSpecItems = null,
    /// <summary>规约里判定为不满足的 DoD 条目。</summary>
    IReadOnlyList<string>? FailedSpecItems = null);

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
/// 观复自己去 git 里数出来的，不是 agent 自报的。
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

/// <summary>规约的内容。DoD 是**逐条**的 —— agent 逐条对着做，人逐条对着判。</summary>
public sealed record ProjectSpecInput(
    /// <summary>项目目标 / 背景。</summary>
    string? Purpose = null,
    /// <summary>完成定义（DoD），一条一项。</summary>
    IReadOnlyList<string>? DefinitionOfDone = null,
    /// <summary>提交信息约定。</summary>
    string? CommitConvention = null,
    /// <summary>测试命令。</summary>
    string? TestCommand = null,
    /// <summary>分支命名约定。</summary>
    string? BranchConvention = null,
    /// <summary>红线 / 禁忌。</summary>
    string? Guardrails = null,
    /// <summary>并行上限（同时干活的 agent 数）。0 = 不限。**只有工作区级规约用得上**。</summary>
    int MaxConcurrentAgents = 0);

/// <summary>规约的对外形状。</summary>
public sealed record ProjectSpecDto(
    Guid ProjectId,
    string? Purpose,
    IReadOnlyList<string> DefinitionOfDone,
    string? CommitConvention,
    string? TestCommand,
    string? BranchConvention,
    string? Guardrails,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    /// <summary>并行上限（同时干活的 agent 数）。0 = 不限。**只有工作区级规约用得上**。</summary>
    int MaxConcurrentAgents = 0)
{
    /// <summary>空规约（还没填过）。</summary>
    public static ProjectSpecDto Empty(Guid projectId) =>
        new(projectId, null, [], null, null, null, null, null, null);

    /// <summary>这份规约有没有内容（一条都没有 = 没填）。</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Purpose)
        && DefinitionOfDone.Count == 0
        && string.IsNullOrWhiteSpace(CommitConvention)
        && string.IsNullOrWhiteSpace(TestCommand)
        && string.IsNullOrWhiteSpace(BranchConvention)
        && string.IsNullOrWhiteSpace(Guardrails);
}
