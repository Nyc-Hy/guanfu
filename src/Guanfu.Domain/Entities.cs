namespace Guanfu.Domain;

/// <summary>工作区。单机默认一个，schema 预留多租户。</summary>
public class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "默认工作区";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Project> Projects { get; set; } = [];

    /// <summary>工作区标准规约（所有项目通用）。</summary>
    public WorkspaceSpec? Spec { get; set; }
}

/// <summary>项目。观复的核心管理单元。</summary>
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
    public bool ManagedByGuanfu { get; set; }

    /// <summary>
    /// 仓库根（git toplevel，已解符号链接的绝对路径）。这是**绑定键**：
    /// agent 的工作目录落在哪个仓库，观复就靠它反查项目。
    ///
    /// 与 <see cref="RepoPath"/> 的分工：RepoPath 是人配的路径（工具的作用范围根），
    /// RepoRoot 是它所属的 git 仓库根。反查要用仓库根 —— agent 在仓库子目录里
    /// 干活时也该落到同一个项目上，用子目录去匹配就匹配不上了。
    /// 为空表示项目还没绑仓库。
    /// </summary>
    public string? RepoRoot { get; set; }

    /// <summary>允许 shell.run 执行的命令前缀（JSON 数组）。不在白名单里的一律拒绝。</summary>
    public string? AllowedCommandsJson { get; set; }

    /// <summary>启动 agent 的命令模板，支持 {taskId} {title} {project} {repoPath} 占位符。</summary>
    public string? AgentCommand { get; set; }

    /// <summary>通知推送地址（钉钉/飞书群机器人 webhook 或通用 webhook）。</summary>
    public string? NotificationWebhookUrl { get; set; }

    /// <summary>通知格式：dingtalk / feishu / generic。</summary>
    public string? NotificationFormat { get; set; } = "generic";

    /// <summary>
    /// 同时最多几个 agent 能在这个项目里领活。
    ///
    /// 0 = 不限。agent 是并发生产者，不设上限的话，
    /// 「三个 agent 抢同一个仓库」这种事故要等踩了文件才发现。
    /// 上限是**节流阀**，不是安全闸门 —— 拦的是并发过头，不是越权。
    /// </summary>
    public int MaxConcurrentAgents { get; set; }

    /// <summary>
    /// 每个任务最多能调多少次工具（0 = 不限）。
    ///
    /// agent 是消耗资源的生产者：不限的话，一个跑飞的 agent 能把
    /// 整个观复当自己的工具箱刷。超了就拒 —— agent 看到明确的上限，
    /// 会自己去收敛，而不是被人发现账单时才停。
    /// </summary>
    public int MaxToolCallsPerTask { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TaskItem> Tasks { get; set; } = [];
    public List<Milestone> Milestones { get; set; } = [];
    public List<DbConnectionConfig> DbConnections { get; set; } = [];
    public ProjectSpec? Spec { get; set; }
}

/// <summary>
/// 项目级规约：**人定的规矩，agent 要读得到**。
///
/// 以前这些规矩只存在于人的脑子和 AGENTS.md 里 —— agent 只能靠人转述，
/// 或者自己去猜「什么叫做完了」。于是「人以为做完了、agent 以为做完了」
/// 这种分歧没法在观复里解决。
///
/// 现在它们是**一等实体**：人填（界面 / REST，要人工凭证），agent 读（MCP 工具、
/// 上下文包、评审包）。评审时它就是尺子 —— 七个维度是通用的，
/// 规约是这个项目特有的。
/// </summary>
public class ProjectSpec
{
    /// <summary>项目 id。</summary>
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }


    /// <summary>项目目标 / 背景。一段话，agent 开工前先读它。</summary>
    public string? Purpose { get; set; }

    /// <summary>完成定义（DoD）。**逐行存**，一行一条 —— agent 逐条对着做。</summary>
    public string? DefinitionOfDone { get; set; }

    /// <summary>提交信息约定（模板 / 前缀 / 要带什么）。</summary>
    public string? CommitConvention { get; set; }

    /// <summary>测试命令。agent 跑完测试就照这条跑。</summary>
    public string? TestCommand { get; set; }

    /// <summary>分支命名约定。</summary>
    public string? BranchConvention { get; set; }

    /// <summary>红线 / 禁忌（不许碰的东西）。</summary>
    public string? Guardrails { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>里程碑：项目内的一批任务归组。可嵌套，形成「版本 → 需求组 → 需求」三层结构。</summary>
public class Milestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// 父里程碑。为空表示顶层（通常是「迭代版本」）。
    /// 结构是 项目 → 迭代版本 → 需求组 → 需求，不加这一层就表达不出来。
    /// </summary>
    public Guid? ParentId { get; set; }
    public Milestone? Parent { get; set; }

    /// <summary>种类：迭代版本（顶层）还是需求组（子层）。</summary>
    public MilestoneKind Kind { get; set; } = MilestoneKind.Release;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public int Order { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 验收时间。非空 = 这个版本**已经签字验收**了。
    ///
    /// 验收是人的决定（只有人能做），条件是它下面（含子里程碑）的活都做完了。
    /// 验收之后，里面的活不能悄悄回退 —— 要改得先撤销验收。
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>验收人。</summary>
    public string? AcceptedBy { get; set; }

    public List<TaskItem> Tasks { get; set; } = [];
    public List<Milestone> Children { get; set; } = [];

    /// <summary>这个里程碑自己的规约（叠加在项目级之上）。</summary>
    public MilestoneSpec? Spec { get; set; }
}

/// <summary>
/// 工作区级规约：**所有项目通用的底线**。
///
/// 公司/团队的规矩（「必须有单测」「必须更新文档」「不许改 migrations」）
/// 不该每个项目抄一遍 —— 抄一遍就会抄漏、改一处就忘了另一处。
/// 工作区级是底线，项目级与里程碑级在它之上叠加。
/// </summary>
public class WorkspaceSpec
{
    /// <summary>工作区 id。</summary>
    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>工作区目标 / 背景。</summary>
    public string? Purpose { get; set; }

    /// <summary>完成定义（DoD）。**逐行存**，一行一条。</summary>
    public string? DefinitionOfDone { get; set; }

    /// <summary>提交信息约定（所有项目通用）。</summary>
    public string? CommitConvention { get; set; }

    /// <summary>测试命令（所有项目通用）。</summary>
    public string? TestCommand { get; set; }

    /// <summary>分支命名约定（所有项目通用）。</summary>
    public string? BranchConvention { get; set; }

    /// <summary>红线 / 禁忌（所有项目通用）。</summary>
    public string? Guardrails { get; set; }

    /// <summary>
    /// 整个工作区的并行上限（同时干活的 agent 数）。0 = 不限。
    ///
    /// 项目级也有一个（<see cref="Project.MaxConcurrentAgents"/>）——
    /// 那个管「这个项目里别挤」，这个管「这个工作区里别挤」。
    /// 两个都是**节流阀**，拦的是并发过头，不是越权。
    /// </summary>
    public int MaxConcurrentAgents { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// 访问凭证（成员 / agent）。
///
/// **一个人用的时候不需要它** —— 那时只有一个共享的人工凭证。
/// 但一台服务器给一个团队用时，「谁在操作」必须是真的：
/// agent id 自报、审批人自己填名字，出了事查不出是谁。
///
/// 所以凭证在**签发**的时候就把身份绑上：绑谁、能看哪些项目。
/// 运行时带的只是那把钥匙，明文只在签发时显示一次，库里只存哈希。
/// </summary>
public class AccessCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>成员（人）还是 agent。</summary>
    public CredentialKind Kind { get; set; }

    /// <summary>身份：成员名 / agent id。</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>凭证哈希（SHA-256 的十六进制）。**不存明文**。</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// 可见的项目（逗号分隔的 id）。空 = 全部可见。
    ///
    /// 「这个凭证能看见什么」是团队部署的最小权限边界：
    /// 一个人只该看见自己负责的项目。
    /// </summary>
    public string? ProjectScope { get; set; }

    /// <summary>备注（谁发的、给谁的）。</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>过期时间。为空 = 长期有效。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>吊销时间。非空 = 已作废。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>谁签发的。</summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// 归属的成员。
    ///
    /// 一把 agent 凭证背后站着一个人 —— 「这台机器上的这个 agent 是谁的」
    /// 就靠这一列。成员被停用，名下的凭证跟着失效。
    /// </summary>
    public Guid? MemberId { get; set; }

    public Member? Member { get; set; }
}

/// <summary>
/// 团队成员（人）。
///
/// 一个人用的时候不需要这张表 —— 那时「一个人 = 全部可见 = owner」。
/// 团队用的时候必须有：**谁在操作必须是真的**，不能谁都能自称张三。
/// </summary>
public class Member
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>登录名（人写的那一串）。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// 登录名的归一化形式（小写）。
    ///
    /// 唯一索引建在它上面 —— 「ZhangSan」与「zhangsan」是同一个登录名，
    /// 只按原字符串比的话会漏掉重号。
    /// </summary>
    public string UserNameNormalized { get; set; } = string.Empty;

    /// <summary>显示名（人看到的名字）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色：普通成员还是管理员。</summary>
    public MemberRole Role { get; set; } = MemberRole.Member;

    /// <summary>
    /// 密码哈希（<c>pbkdf2$迭代次数$盐$哈希</c>）。**不存明文**。
    /// 为空 = 还没设密码 —— 这种成员只能靠人工凭证进，不能自己登录。
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>停用后不能登录，名下的凭证也跟着失效。</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>上次登录时间。</summary>
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>凭证的种类。</summary>
public enum CredentialKind
{
    /// <summary>成员（人）—— 能裁决审批、改配置。</summary>
    Member = 0,

    /// <summary>agent —— 走 MCP 干活。</summary>
    Agent = 1
}

/// <summary>成员角色。</summary>
public enum MemberRole
{
    /// <summary>普通成员：只看自己的东西，批自己的活。</summary>
    Member = 0,

    /// <summary>管理员：看全部；能分配任务；能代别人批（要写理由）。</summary>
    Admin = 1
}

/// <summary>
/// 里程碑级规约。
///
/// 与项目级规约**叠加**：项目级的规矩对所有活适用，里程碑级的只对这个里程碑里的活生效。
/// 「12 月版本要额外写迁移说明」这类只对某个版本成立的要求，
/// 不该塞进项目级规约里污染所有活。
///
/// 单独一张表，不跟项目级规约混在一张 —— 混在一起的话「项目级只有一份」
/// 这个唯一性在 NULL 语义下就说不清了。
/// </summary>
public class MilestoneSpec
{
    /// <summary>所属里程碑 id。</summary>
    public Guid MilestoneId { get; set; }

    public Milestone? Milestone { get; set; }

    /// <summary>这个里程碑的目标 / 补充说明。</summary>
    public string? Purpose { get; set; }

    /// <summary>完成定义（DoD）。**逐行存**，一行一条。</summary>
    public string? DefinitionOfDone { get; set; }

    /// <summary>提交信息约定（这个版本特有的）。</summary>
    public string? CommitConvention { get; set; }

    /// <summary>测试命令（这个版本特有的）。</summary>
    public string? TestCommand { get; set; }

    /// <summary>红线 / 禁忌（这个版本特有的）。</summary>
    public string? Guardrails { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
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
    /// 项目内可读编号（如 REQ-001 / BUG-001 / OPT-001）。
    /// agent 是听人话来的 ——「看下 req001」这种话要能落地，得有个稳定的短标识。
    /// 前缀由 <see cref="Type"/> 决定，各类型独立计数。
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// 编号的归一化形式（只留字母数字并转小写）。
    ///
    /// 存在这一列是为了让「同一个项目里不能有两个归一化后相同的编号」
    /// 能被**数据库**保证：原来的唯一索引按原字符串比，
    /// 「req-001」和「REQ-001」在库里是两条，索引拦不住 ——
    /// 而业务口径（<c>TaskKeys.Normalize</c>）认它们是同一个号。
    ///
    /// 可空：老库的存量数据这一列是空的，而 SQLite 的唯一索引不约束多个 NULL，
    /// 补列时不会因为一列都是空值而建不出索引。
    /// </summary>
    public string? KeyNormalized { get; set; }

    /// <summary>类型：需求 / 缺陷 / 优化。</summary>
    public TaskType Type { get; set; } = TaskType.Requirement;

    /// <summary>
    /// 是否已归档。归档不是删除 —— 流水、产物、评审都还在，
    /// 只是默认不再出现在列表里。误建的任务需要个清理路径，
    /// 否则只能一直挂在看板上。
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// 绑定的开发分支。为空表示不限制分支。
    /// 绑了分支的任务只有在仓库当前分支与之一致时才会运行（见 <c>IBranchGuard</c>）。
    /// </summary>
    public string? Branch { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>负责人（人名或 agent id）。为空表示还没分派。</summary>
    public string? Assignee { get; set; }

    /// <summary>
    /// 负责人是哪个成员。
    ///
    /// 「负责人」那一列是给人看的名字（可能是人，也可能是 agent），
    /// 这一列才是**归属**：可见性按它算 —— 普通成员只看得到自己的活。
    /// 为空 = 还没分派（谁都能看见，也谁都能认领）。
    /// </summary>
    public Guid? AssigneeMemberId { get; set; }

    public Member? AssigneeMember { get; set; }

    /// <summary>优先级。不参与状态机，只影响排序与展示。</summary>
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    /// <summary>截止日。为空表示没有承诺时间。</summary>
    public DateTimeOffset? DueAt { get; set; }

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

    /// <summary>
    /// 人工排的位次（越小越先做）。为空 = 没排，由调度建议按分排。
    ///
    /// 调度建议只是**建议** —— 最终谁先干，人说了算。
    /// 所以人能「排进计划」把它顶到前面，也能随时移出去。
    /// </summary>
    public int? PinnedOrder { get; set; }

    /// <summary>
    /// 这个 agent 干活的工作目录（绝对路径）。
    ///
    /// 为空 = 用项目的主工作区（仓库根）。非空 = agent 自己开的 git worktree。
    ///
    /// 为什么要有它：两个 agent 共用同一个工作区就会互相踩文件。
    /// 主工作区同一时刻只允许**一个** agent 占着；要并行就得各开各的 worktree，
    /// 并在这里登记 —— 观复靠它知道「谁在哪个目录干活」，
    /// 也靠它挡住「两个人抢同一个目录」。
    /// </summary>
    public string? WorkPath { get; set; }

    public List<StatusEvent> StatusEvents { get; set; } = [];
    public List<Document> Documents { get; set; } = [];
    public List<Artifact> Artifacts { get; set; } = [];
    public List<TaskFileChange> FileChanges { get; set; } = [];
    public List<Plan> Plans { get; set; } = [];
    public List<TestRun> TestRuns { get; set; } = [];
    public List<ReviewRound> ReviewRounds { get; set; } = [];
    public List<TaskDependency> Dependencies { get; set; } = [];
}

/// <summary>
/// 任务依赖。表达「A 做完 B 才能开始」。
///
/// 方向统一按 <see cref="DependencyType.BlockedBy"/> 存：TaskId 是被阻塞的，
/// DependsOnTaskId 是前置的。Blocks 类型的记录在写入时就翻成同一条 BlockedBy，
/// 省得查询时两边都要判一遍。
/// </summary>
public class TaskDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>被阻塞的任务。</summary>
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    /// <summary>前置任务（必须先完成的那个）。</summary>
    public Guid DependsOnTaskId { get; set; }
    public TaskItem? DependsOn { get; set; }

    public DependencyType Type { get; set; } = DependencyType.BlockedBy;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// 机器可读的事件类型（如 status.changed / plan.saved / artifact.added）。
    /// <see cref="Note"/> 是给人看的自由文本，机器要判定得靠这个固定标识。
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// 结构化载荷（JSON）。事件账本要能被机器消费、能回放，
    /// 光有自由文本不够 —— 关键字段得能按字段取。
    /// </summary>
    public string? Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 实际发生时间（补报用）。
    ///
    /// agent 可能在观复够不着的时候就把活干完了（离线补报）。
    /// 这时它报上来的时间是**真的发生时间**，跟「观复什么时候知道」不是一回事 ——
    /// 账本上这两件事都要有：CreatedAt 是记录时间，OccurredAt 是发生时间。
    /// 为空表示没补报，两者相同。
    /// </summary>
    public DateTimeOffset? OccurredAt { get; set; }
}

/// <summary>上传的文档原件（PRD / 测试用例 / 接口文档等）。</summary>
public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public string FileName { get; set; } = string.Empty;
    /// <summary>相对 数据目录 的存储路径。</summary>
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

    /// <summary>
    /// 当前是第几版。第一次登记是 1，每改一次 +1。
    /// </summary>
    public int Revision { get; set; } = 1;

    /// <summary>最后一次修改时间。为空 = 没改过（还是登记时那一版）。</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>谁改的。</summary>
    public string? EditedBy { get; set; }
}

/// <summary>
/// 产物的一个历史版本。
///
/// 人会改 AI 写出来的脚本 —— 改完不能把原来那版抹掉：
/// 「谁在什么时候把它改成这样」是复盘时最常问的问题。
/// </summary>
public class ArtifactRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArtifactId { get; set; }
    public Artifact? Artifact { get; set; }

    /// <summary>第几版（与产物的 Revision 对齐）。</summary>
    public int Version { get; set; }

    public string? Title { get; set; }
    public string? Content { get; set; }

    /// <summary>谁改的。</summary>
    public string? Editor { get; set; }

    /// <summary>改的时候写的话（为什么改）。</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 一次交付打包的记录。
///
/// 交付物是「这个迭代版本要发出去的东西」：文档 + 产物（脚本、报告）。
/// 打包那一刻的清单连同包一起留在账上 —— 事后来问「发出去的是哪一版」要有得答。
/// </summary>
public class DeliveryPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid MilestoneId { get; set; }

    /// <summary>下载时用的文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>包在数据目录里的相对路径。</summary>
    public string StoredPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>包里带了几个条目（清单条目数）。</summary>
    public int ItemCount { get; set; }

    /// <summary>清单条目（JSON 数组）。</summary>
    public string Items { get; set; } = "[]";

    /// <summary>清单正文（markdown），与包放在一起。</summary>
    public string? Manifest { get; set; }

    public string CreatedBy { get; set; } = "human";
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

    /// <summary>
    /// 请求参数的指纹（SHA-256）。审批必须绑在**具体操作**上 ——
    /// 只记「批过一条 shell_run」的话，这条批准换个命令、换个工具还能再用一次，
    /// 等于审批形同虚设。续跑时要比对指纹，不一致就拒。
    /// </summary>
    public string? PayloadHash { get; set; }
    /// <summary>给审批人看的一句话摘要。</summary>
    public string Summary { get; set; } = string.Empty;

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? RequestedBy { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }

    /// <summary>
    /// 这条审批归谁批。
    ///
    /// 「审批只能个人审批」：agent 的写操作要人点头，点的是**这把钥匙背后的人**。
    /// 为空 = 还没归到人身上（老数据，或没人签发过凭证）。
    /// </summary>
    public Guid? AssigneeMemberId { get; set; }

    public Member? AssigneeMember { get; set; }

    /// <summary>
    /// 代批时被代的人。
    ///
    /// 管理员可以替别人批 —— 但**谁替谁批的必须看得见**：
    /// 实际裁决人记在 <see cref="DecidedBy"/>，被代人记在这里。
    /// </summary>
    public Guid? OnBehalfOfMemberId { get; set; }

    public Member? OnBehalfOfMember { get; set; }

    /// <summary>
    /// 裁决人所用人工凭证的指纹（哈希前 12 位，不存明文）。
    ///
    /// 共享 token 的现状下，「谁批的」只能靠 decidedBy 这个名字 —— 填错了系统不知道。
    /// 记下凭证指纹，好歹能区分「同一个操作员」和「换了个人操作」，
    /// 不用把 token 本身写进审计。
    /// </summary>
    public string? DecidedByCredential { get; set; }
    public string? ResultSummary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>
    /// 进入 Executing 的时刻。执行方崩了/被杀了，审批会永远卡在 Executing，
    /// 既不能再批也不能再用。有了这个时间戳才能把「执行中断」的单子回收掉。
    /// 为空表示老数据，回收时退回 <see cref="DecidedAt"/> 判定。
    /// </summary>
    public DateTimeOffset? ExecutingStartedAt { get; set; }

    /// <summary>
    /// 执行结果未知（服务重启导致执行中断）。
    ///
    /// 这条审批在被中断时，那次写操作**可能已经生效**了 ——
    /// 比如 git commit 已经写进仓库，只是执行方没来得及回写状态。
    /// 标记出来，人在审批页上一眼能看见「这条的结果要人工确认」，
    /// 别让它跟「执行失败」混为一谈：失败的可以重试，未知的要先查。
    /// </summary>
    public bool OutcomeUnknown { get; set; }
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

    /// <summary>
    /// 审计记录的序号（从 1 开始，单调递增）。
    ///
    /// 哈希链靠它定序，不能靠时间戳 —— 同一时钟刻里写的两条记录时间戳一样，
    /// 按时间排序它们的先后就是不确定的，链会误报断裂。
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// 上一条审计的哈希。空表示这是哈希链的起点（或者本条是链启用前的老数据）。
    /// </summary>
    public string? PrevHash { get; set; }

    /// <summary>
    /// 本条审计内容的哈希（内容 + 上一条的哈希）。
    ///
    /// 有了它，「有人直接改了数据库文件」这件事能验出来：
    /// 改动一改，链上的哈希就对不上了。应用层不提供改审计的入口，
    /// 但拿到库文件的人能直接改 —— 这条链就是为了让那种改动**可发现**。
    /// </summary>
    public string? Hash { get; set; }
}

/// <summary>
/// agent 会话 —— 一次「agent 干活」的过程。
///
/// 存在的理由：agent 的对话跟观复之间本来没有任何关联。
/// agent 自己记着「我在干哪个项目」，观复只知道「某个 callerId 调了某个工具」——
/// 两边对不上，审计里的项目/任务归属就只能靠 agent 自觉填。
///
/// 会话把两边接上：这次干活在哪个仓库、属于哪个项目、认领了哪个任务。
/// 桥接启动时按工作目录自动绑定，agent 之后所有工具调用都自动归属到这里，
/// 不用每次自己背项目 GUID。
/// </summary>
public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// 认领任务后回填。认领之前为空 —— 会话可以先于任务存在
    /// （agent 先进来看看有什么活，再领）。
    /// </summary>
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }

    /// <summary>外部 agent 的标识（agentId）。审计里用它区分是谁在干活。</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 会话所在仓库的根（已解符号链接的绝对路径）。
    /// 项目绑了仓库后，agent 在这个仓库里干活 → 会话绑定到该项目。
    /// </summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>
    /// MCP 协议会话 id。同一个协议会话复用同一条 agent 会话，
    /// 免得每次工具调用都新开一条（审计里会碎成一堆）。
    /// </summary>
    public string? McpSessionId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最后一次有动静的时间。用来判断会话是不是还活着。</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// 任务的产出文件 —— 开发过程中实际改了哪些文件。
///
/// 这是「产出可追溯」的落地：不是 agent 说它改了什么，而是观复自己去
/// git 里数出来。采集点在提交之后（闸门执行完）与进入评审之前，
/// agent 绕不过去。
/// </summary>
public class TaskFileChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>哪次 agent 会话干的。审计里能顺着会话查到人。</summary>
    public Guid? SessionId { get; set; }
    public AgentSession? Session { get; set; }

    /// <summary>仓库相对路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>变更类型：Added / Modified / Deleted / Renamed。</summary>
    public string ChangeKind { get; set; } = "Modified";

    /// <summary>重命名前的路径。只有 Renamed 才有值。</summary>
    public string? OldPath { get; set; }

    public int LinesAdded { get; set; }
    public int LinesDeleted { get; set; }

    /// <summary>
    /// 对应的 commit。空表示「工作区里改了但还没提交」——
    /// 那种改动照样要能追到。
    /// </summary>
    public string? CommitSha { get; set; }

    public string? Branch { get; set; }
    public string? Author { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 一次数据库调用的结构化记录。
///
/// 工具审计表是通用的（所有工具都往里写），SQL 原文、目标库、目标表
/// 埋在那儿的 JSON 里，查不动。这里单独一张表，把「谁在什么时候对哪个库的
/// 哪张表跑了什么 SQL、影响几行、谁批的」摊成列。
/// </summary>
public class DbCallRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>对应的工具审计记录 id。</summary>
    public Guid? AuditId { get; set; }

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }

    public Guid? SessionId { get; set; }

    public Guid? ConnectionId { get; set; }

    /// <summary>工具名：db_query / db_execute / db_schema 等。</summary>
    public string ToolName { get; set; } = string.Empty;

    public string? Server { get; set; }
    public string? Database { get; set; }

    /// <summary>SQL 原文。</summary>
    public string? Sql { get; set; }

    /// <summary>只读 / 写 / 认不出来（Read / Write / Unknown）。</summary>
    public string? Kind { get; set; }

    /// <summary>目标表，多个用逗号分隔。</summary>
    public string? TargetTables { get; set; }

    public int? RowsAffected { get; set; }

    /// <summary>success / denied / error / pending。</summary>
    public string? Outcome { get; set; }

    public Guid? ApprovalId { get; set; }

    /// <summary>谁调的（agent 标识）。</summary>
    public string? CallerId { get; set; }

    public long DurationMs { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>项目数据库连接（SQL Server）。</summary>
/// <summary>
/// 项目规约的**逐条判定**（评审轮次内）。
///
/// 项目规约里的 DoD 是一条一条的，评审时也应该一条一条判 ——
/// 「全都满足」这种笼统结论没法复核，也不值当尺子。
///
/// 挂在评审轮次下：开新一轮评审，判定就重来一遍（旧结论留在历史里）。
/// </summary>
public class SpecVerdict
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>所属评审轮次。</summary>
    public Guid ReviewRoundId { get; set; }
    public ReviewRound? ReviewRound { get; set; }

    /// <summary>所属任务。</summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// DoD 条目的稳定键（条目的归一化形式）。
    /// 规约改了文字 → 键变了 → 旧判定不再算数（人得重新判）。
    /// </summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>条目原文快照（规约后来改了也不丢当时的判据）。</summary>
    public string ItemText { get; set; } = string.Empty;

    /// <summary>判定：通过 / 不通过 / 不适用。</summary>
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    /// <summary>说明（不适用必须写理由）。</summary>
    public string? Note { get; set; }

    public string? Reviewer { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>谁认领了这条的判定（多 agent 分工）。</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>认领的时间。</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>认领的租约到期时间。到期还没判，别的 agent 可以接手。</summary>
    public DateTimeOffset? ClaimedUntil { get; set; }
}

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

    /// <summary>
    /// 开轮时的仓库 HEAD。评审评的是「这一版代码」——
    /// 评完之后代码又变了，这轮的结论就不再对应当前要结项的东西。
    /// 为空表示老轮次（或项目没配仓库），结项门禁跳过这项校验以保持兼容。
    /// </summary>
    public string? CommitHash { get; set; }

    public List<ReviewItem> Items { get; set; } = [];

    /// <summary>项目规约（DoD）的逐条判定。</summary>
    public List<SpecVerdict> SpecVerdicts { get; set; } = [];
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

    /// <summary>
    /// 谁**认领**了这个维度的评审（多 agent 分工）。
    /// 为空 = 没人认领，谁都能评。
    /// </summary>
    public string? ClaimedBy { get; set; }

    /// <summary>认领的时间。</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// 认领的租约到期时间。到期还没结论，别的 agent 可以接手 ——
    /// 认领不是占座城堡，是「我在做，别重复」。
    /// </summary>
    public DateTimeOffset? ClaimedUntil { get; set; }
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

    /// <summary>
    /// true = 这张表**一律不许写**（连人工审批都不放行）。
    ///
    /// 为什么要有这一档：以前写侧只有「要审批 / 不要审批」两档，
    /// 审批就是唯一的一道兜底 —— 而审批是流程，不是保证。
    /// 真正不能碰的表（比如生产库的核心表）得有一道**硬门**：
    /// 配了就写不进去，谁批都不行。
    ///
    /// 字段取「否定式」是刻意的：SchemaUpgrader 补 NOT NULL 列时用**CLR 默认值**，
    /// bool 的 CLR 默认是 false —— 所以 false 必须表示「不禁止」，
    /// 老库补列后才是「照常可写」，而不是一夜之间全表禁写。
    /// </summary>
    public bool WriteForbidden { get; set; }
}

/// <summary>
/// 数据库结构版本。每次升级写一行。
///
/// 这个项目用 <c>EnsureCreated</c> 建表，不走 EF Migrations，所以没有
/// <c>__EFMigrationsHistory</c> 那种版本表。没有版本表就没法回答
/// 「这个库现在是哪个版本的 schema」—— 排查老库问题时全靠猜。
/// </summary>
public class SchemaVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 模型指纹（EF 建表脚本的哈希前 16 位）。
    /// 模型不变指纹就不变，所以它精确对应「当前库的结构」。
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>这次升级做了什么（建了哪些表、补了哪些列）。</summary>
    public string? Note { get; set; }
}
