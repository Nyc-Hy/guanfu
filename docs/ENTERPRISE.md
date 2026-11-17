# 企业验收说明

这份文档回答一个问题：**这套东西能不能在企业里用？**
答案不是「能」或「不能」，而是**逐条能力 → 落地位置 → 自动化证据**。
每一条都能用一条命令复现；不能复现的，老实写在最后一节。

一条命令跑完全部验收：

```bash
./tests/verify.sh
```

它会依次做三件事并给总账：编译（0 警告）→ 单元测试 → 端到端（七条链路）。
条数每次都在变 —— **以这次运行的输出为准**，别照抄文档里的旧数字。
端到端自己起临时工作台、跑完即收，不留状态。

---

## 一、先说清楚工作台是什么

**它是一个给 agent 用的项目管理工具** —— 第一公民是 agent，人也要用。
任务、里程碑、依赖、状态机是内核；MCP 工具集把这些原语喂给 agent；
审计与审批是让人敢信这个看板的底座。

这条定位落到具体，是三件事：

1. **追踪任务进度** —— 状态机 + append-only 事件账本 + 评审门禁；
2. **记录开发产出的文件** —— 工作台自己从 git 里数，不是 agent 自报的；
3. **留下数据库调用的记录** —— SQL 原文、目标库、目标表、影响行数、审批人。

工作台**不提供 AI 服务** —— 不调模型、不跑 agent、不生成需求理解与开发计划。
智能动作发生在外部 agent 进程里，它们通过 MCP 工具与 CLI 驱动工作台的状态流转。

在这三件事之上还有一层**约束**：**写操作（改数据库、提交、推送、跑命令）不会在没人点头的情况下发生**。

这条定位决定了「能不能企业用」的判断标准：不是「AI 聪不聪明」，
而是**「外部 agent 拿着写权限时，能不能保证它写的东西都有人批过、批的是不是那件事、事后能不能查」**。

还有一层**归属**：agent 的对话跟哪个项目、哪个任务绑在一起，是**绑定键说了算**的 ——
项目创建时绑定本地仓库，agent 在这个目录里干活就自动落到这个项目。
审计里的项目与任务归属不靠 agent 自觉填。

---

## 二、逐条能力与证据

### A. 变更管控：写操作必须有人点头

| 项 | 内容 |
|---|---|
| 能力 | agent 的所有写操作走同一条闸门；不批就不执行 |
| 落地 | `src/Workbench.Application/Services/ApprovalGate.cs`（闸门本体）<br>`src/Workbench.Mcp/GeneralMcpTools.cs`、`DatabaseMcpTools.cs`（`git_commit` / `git_push` / `shell_run` / `db_execute` 共用） |
| 自动化证据 | 端到端 **链路 D**（D1–D10，10 组断言）<br>单测 `ApprovalGateTests`（10）、`ApprovalLifecycleTests`（21）、`ApprovalServiceTests`（7） |

关键事实：`db_execute` / `git_commit` / `git_push` / `shell_run` **共用同一套闸门**，
不是各写一份 —— 以前各写一份时行为已经开始漂移（一份查了参数指纹，另一份没查）。

### B. 最小权限：一条批准不是万能令牌

| 项 | 内容 |
|---|---|
| 能力 | 批准绑「工具名 + 参数指纹 + 项目」三件事；换个命令、换个工具、换个项目都用不了；一条批准只执行一次 |
| 落地 | `ApprovalService.TryConsumeAsync`（`src/Workbench.Application/Services/ApprovalService.cs`）<br>指纹 = 参数 SHA-256；消费用条件更新原子抢占（`Approved → Executing → Executed`） |
| 自动化证据 | 端到端 **D4**（复用被拒）、**D5**（换命令被拒）、**D6**（换项目被拒）<br>单测 `ApprovalGateTests` |

特别说明：`db_execute` 的指纹绑的是 `connectionId + server + database + sql` —— 换个库执行对不上；
`git_commit` 绑的是**待提交内容的指纹**，而且**批准之后、执行之前要再算一次**：
等审批期间工作区变了就拒。人批的是「那些内容」，不是「提交这个动作」。

### C. 命令执行：不走 shell 解析 + 项目白名单

| 项 | 内容 |
|---|---|
| 能力 | 命令拆成可执行文件 + 参数直接 exec，杜绝 `;` `&` `|` 拼接注入；且必须命中项目白名单 |
| 落地 | `src/Workbench.Application/Services/ShellRunner.cs`（exec 语义、输出上限 1 MB、10 分钟超时、杀进程树）<br>`ShellCommandPolicy`（按词的前缀匹配：`"git status"` 不误伤 `git status-tree`） |
| 自动化证据 | 端到端 **D1**（白名单外的命令连审批单都不建）<br>单测 `ShellRunnerTests`（13） |

### D. 数据保护：只读通道不许写

| 项 | 内容 |
|---|---|
| 能力 | `db_query` 只放行能确认为只读的语句；写操作和**认不出来的语句**一律拦下 |
| 落地 | `src/Workbench.Infrastructure/SqlStatementClassifier.cs`（ScriptDom AST，不看首关键字）<br>`WITH c AS (...) SELECT` 与 `WITH c AS (...) UPDATE` 首关键字都是 WITH，`SELECT ... INTO` 会建表 —— 只有看 AST 才分得开<br>`OPENROWSET` / `OPENQUERY` / `OPENDATASOURCE` / `OPENXML` 一律判 Unknown |
| 自动化证据 | 端到端 **D8**（`UPDATE` 被拦、错误信息指引改用 `db_execute`；`EXEC` 被拦）<br>单测 `SqlStatementClassifierTests`（19） |

表级规则也在守，三档：
- `AllowRead=false` 的表不许读；
- `WriteRequiresApproval=false` 的表免审批写（免的只是「等人工点头」，审计照写）；
- `WriteForbidden=true` 的表**一律不许写** —— 连人工审批都不放行（审批是流程，禁写才是保证）。

写目标的识别走 AST（`SqlWriteTargetAnalyzer`）：别名（`UPDATE t … FROM Secret AS t` 写的是
Secret）、跨库（`OtherDb.dbo.T`）、`INSERT … EXEC` 一律判「认不出」→ 转人工审批，
免审批的判定不会认错表。单测 `SqlWriteTargetAnalyzerTests`（12）、`DbTableRuleTests`。

### D2. 项目级规约：人定规矩，agent 读得到

| 项 | 内容 |
|---|---|
| 能力 | 项目目标、完成定义（DoD 逐条）、提交信息约定、测试命令、分支命名、红线与禁忌 —— **人填、agent 读**；改规约要人工凭证 |
| 落地 | `src/Workbench.Domain/Entities.cs`（`ProjectSpec`）<br>`src/Workbench.Application/Services/ProjectSpecService.cs`<br>`workbench_get_project_spec` + 上下文包 + 评审包 |
| 自动化证据 | 单测 `ProjectSpecTests`（7）<br>端到端（agent 读到 DoD 逐条、上下文包里带着规约） |

评审时它就是尺子：七个维度是通用的，规约是这个项目特有的 —— 「什么算做完」不再各说各话。

### D3. 里程碑验收与用量闸门

| 项 | 内容 |
|---|---|
| 能力 | 里程碑验收（人的签字，签过之后里面的活不许回退）；每任务工具调用上限（可执行的节流阀）；用量统计（工具调用次数自动可验证，token / 费用为 agent 自报） |
| 落地 | `src/Workbench.Application/Services/MilestoneService.cs`（验收 / 撤销验收）<br>`src/Workbench.Application/Services/TaskUsageService.cs`<br>MCP 中央过滤器（`McpServiceCollectionExtensions`）里的用量闸门 |
| 自动化证据 | 单测 `MilestoneAcceptanceTests`（6）、`TaskUsageTests`（5）、`AgentHandoffTests`（3）<br>端到端 F4.2（超上限后工具调用被拦下，错误信息给出上限） |

多 agent 协作的痕迹也在：交接点（`task.handoff`，带 from / to）与按次留痕的开销上报。
还有**可解释的调度**：`IDispatchService.SuggestAsync` 给出「先干哪个」并附理由
（优先级、权重、等待时长、解锁数），agent 与人看同一份 —— 派单不再靠猜。

### E. 身份与授权：敏感操作要人工凭证

| 项 | 内容 |
|---|---|
| 能力 | 裁决审批、改项目配置、存数据库密码、启动 agent、工作区搬迁、**新建项目** —— 没凭证直接 401 |
| 落地 | `src/Workbench.Web/HumanTokenMiddleware.cs` + `HumanTokenPolicy.IsSensitiveEndpoint`（端点级规则表）<br>token 落在 `WORKBENCH_HOME/.human-token`（0600），启动横幅打印 |
| 自动化证据 | 端到端 **E2**（CLI 不带凭证改项目配置 → 退出码 2 + `HTTP 401`）<br>单测 `HumanTokenTests`（22）、`EndpointClassificationTests`（4） |

`EndpointClassificationTests` 会枚举所有 `/api` 写端点，断言该要人工凭证的要、读端点别误伤 ——
**新增敏感端点漏一条规则 = 开一扇门**，所以这条由测试钉住。

**新建项目也要凭证**：项目一创建就把一个文件系统根目录授给了它，之后 `fs_list` / `fs_read`
以它为根。不设门的话，拿到 MCP/REST 的 agent 自己建个项目指向 `/etc` 就能把文件读出去 ——
改 `repoPath` 要凭证、建项目不要，等于开了扇后门。

### F. Web 安全：跨站与 DNS rebinding

| 项 | 内容 |
|---|---|
| 能力 | `/api` 写方法的 `Origin` 必须与 `Host` 同源；`Host` 头必须在允许清单里（默认回环） |
| 落地 | `src/Workbench.Web/OriginHostGuard.cs`（策略层是纯函数，方便单测）<br>内网部署用 `Workbench:AllowedHosts` 显式放行 |
| 自动化证据 | 单测 `OriginHostGuardTests`（21） |

只监听回环挡不住 rebinding —— 浏览器发出来的 `Host` 就是攻击者的域名，所以必须验 Host。

### G. 可审计：谁在什么时候干了什么

| 项 | 内容 |
|---|---|
| 能力 | **每次工具调用**逐条留痕（谁调的、哪个项目、哪个任务、用的哪条审批、结果如何）；写操作与数据库访问另记 SQL 原文与结果摘要；审计表只追加不修改；链头可归档到工作台数据目录**之外** |
| 落地 | `src/Workbench.Application/Services/ToolAuditService.cs`（append-only + 哈希链）<br>`src/Workbench.Infrastructure/AuditChainArchiver.cs`（链头外部归档与对账）<br>写工具无论成功、失败、被策略拦下（`denied`）都记 |
| 自动化证据 | 端到端 **D10**（`pending` / `success` / `denied` 三种结果都留痕，approvalId 与 callerId 都记上）<br>单测 `AuditChainTests`（链断点定位）、`AuditChainArchiveTests`（8：归档、删过查得出、改过查得出、路径在工作台里面要拒）<br>CLI：`wb audit list --tool/--project/--task` |

### H. 事件账本：机器可读，不是自由文本

| 项 | 内容 |
|---|---|
| 能力 | 每条事件带固定 `EventType` + 结构化 JSON `Payload`；消费方按 EventType 判定，不去猜 `Note` |
| 落地 | `src/Workbench.Domain/Entities.cs`（`StatusEvent`）、`TaskEventTypes`（固定标识表） |
| 自动化证据 | 单测 `EventLedgerContractTests`（2）<br>端到端 A：`wb task events --format markdown/csv/json` |

### I. 流程控制：状态机 + 评审门禁

| 项 | 内容 |
|---|---|
| 能力 | 非法状态迁移被拒，并带回**当前允许的迁移列表**让 agent 自我纠正；结项前当前轮 7 个维度必须全过 |
| 落地 | `src/Workbench.Domain/TaskStatusMachine.cs`、`ReviewGate`（`TaskService.ChangeStatusAsync` 在 `Review → Done` 时校验） |
| 自动化证据 | 端到端 **A**（正常走到结项）、**B**（只评 6 个维度 / 有一个不通过，两种都拦下）<br>单测 `TaskStatusMachineTests`（4）、`ReviewGateTests`（15） |

没开过评审的老流程不受影响 —— 这条兼容性刻意保留。

### J. 部署安全：分支守卫

| 项 | 内容 |
|---|---|
| 能力 | 任务绑了分支，仓库当前不在那个分支上就不许领、不许推进到 InDev、不许启动 agent；**读不到当前分支时也拒绝**（「不知道在哪个分支」不等于「在对的分支上」） |
| 落地 | `src/Workbench.Application/Services/BranchGuard.cs`，拦在三处：领取、推进到 InDev、启动 agent |
| 自动化证据 | 端到端 **C**（错误信息点名绑定分支 + 当前分支）<br>单测 `TaskTypeAndBranchTests`（15） |

### K. 密钥与文件边界

| 项 | 内容 |
|---|---|
| 能力 | 数据库密码加密存储；密钥文件 0600；并发首启不会互相覆盖密钥；路径比较带目录分隔符且解析符号链接后再判一遍 |
| 落地 | `src/Workbench.Infrastructure/AesSecretProtector.cs`、`PathBoundary`（`Workbench.Application.Services`） |
| 自动化证据 | 单测 `SecretProtectorTests`（3）、`FileBoundaryTests`（18） |

自己写 `StartsWith` 判路径就是在挖洞 —— `/tmp/wb-root-secrets` 不是 `/tmp/wb-root` 的子目录。

### L. 集成可靠性：契约稳定、工具不抛异常

| 项 | 内容 |
|---|---|
| 能力 | 所有 MCP 工具返回 `ok` + `error` 的统一形状；工具自己判出来的失败也给同样的形状；真抛异常时全局 `CallTool` 兜底过滤器转成 `{ok:false,error}`（`isError=true`） |
| 落地 | `src/Workbench.Mcp/`（`WriteToolResult`、`DbQueryToolResult`、`ExportToolResult` 等）<br>`McpToolContractTests` 逐个工具检查返回类型不是裸 `object` / 裸字符串 / 裸 bool |
| 自动化证据 | 单测 `McpToolContractTests`（4）、`McpToolErrorTests`（7）、`McpToolTests`（8） |

### M. 客户端兼容：stdio 桥接

| 项 | 内容 |
|---|---|
| 能力 | 只支持 stdio 的 MCP 客户端通过 `wb mcp` 接进来；桥接的 stdout 只能是 JSON-RPC，日志一行都不许混进去 |
| 落地 | `src/Workbench.Cli/McpBridge.cs`（协议逻辑抽成纯函数 `McpBridgeProtocol` 才好单测）<br>并发模型：stdin 读取循环不阻塞在 HTTP 上；通知单独信道；请求并发处理；stdout 单一消费者串行输出 |
| 自动化证据 | 端到端 **E3**（喂 initialize 与 tools/list，回包 id 对得上、58 个工具、工具名全用下划线）<br>单测 `McpBridgeProtocolTests`（11） |

工具名一律用下划线（`workbench_get_context`），不用点号 —— 部分 MCP 客户端不接受点号。

### N. 运维

| 项 | 内容 |
|---|---|
| 能力 | 加字段/表/索引不用改升级器（模型变了下次启动自动跟上）；换机用 `wb workspace export` / `import`（导出带库+文档+密钥+运行日志）；待审审批不会因为进程重启丢掉 |
| 落地 | `src/Workbench.Infrastructure/SchemaUpgrader.cs`（完全从 EF 模型推导，不维护手写清单）<br>`WorkspaceArchiver` |
| 自动化证据 | 端到端 **E2.3**（导出目录里确有 `workbench.db` + `manifest.json` + `secret.key`）<br>单测 `SchemaUpgraderTests`（16）、`WorkspaceArchiverTests`（13）、`WorkspaceArchiveFilesTests`（6） |

### O. 架构可控：单一数据通路

| 项 | 内容 |
|---|---|
| 能力 | `Workbench.Web` 独占 SQLite，是唯一写者；MCP 与 CLI 都是 Web 的 HTTP 客户端 |
| 落地 | `src/Workbench.Mcp/`、`src/Workbench.Cli/WorkbenchClient.cs`（只有 HTTP，没有 DbContext） |
| 自动化证据 | 端到端 **A/D/E** 三条链路跑的都是同一个服务，CLI 与 MCP 读写的是同一个库 |

这条不是靠测试钉的，是靠**结构**：MCP 与 CLI 两个工程里没有数据库依赖。

---

## 三、三个问题，三个查法

企业落地时被问得最多的三件事，各给一条可复现的路。

### 1. 任务进度怎么追

| 问题 | 查法 |
|---|---|
| 现在什么在动 | 首页总览 →「在动的任务」 |
| 有哪些写操作等我批 | 首页总览 →「等我批的写操作」→ `/approvals` |
| 一个任务经历过什么 | 任务详情 →「事件时间线」（append-only，每条带结构化 Payload） |
| 干这个活的是哪个 agent | 任务详情 →「会话」 |
| 状态是怎么流转的 | `TaskStatusMachine` 拒绝非法迁移，并带回当前允许的迁移列表 |

**落地**：`src/Workbench.Domain/TaskStatusMachine.cs`、`src/Workbench.Domain/Entities.cs`（`StatusEvent`）、
`src/Workbench.Application/Services/OverviewService.cs`（首页总览）
**自动化证据**：端到端 A（正常走到结项）；单测 `TaskStatusMachineTests`（4）、`EventLedgerContractTests`（2）

### 2. 产出文件怎么查

| 问题 | 查法 |
|---|---|
| 这个任务改了哪些文件 | 任务详情 →「产出文件」 |
| 最近产出了什么 | 首页总览 →「最近产出的文件」 |
| 这次改动对应哪个 commit | 产出文件行上的 commit sha |
| 接口 | `GET /api/tasks/{id}/file-changes` |

**关键**：这些数据**不是 agent 自报的**，是工作台自己在两个采集点从 git 里数的 ——
① `git_commit` 审批通过、真正提交之后（`git show --numstat`）；
② 推进到 `Review` 时快照工作区（`git diff --numstat` + 未跟踪文件）。
两个点都在 agent 绕不过去的地方。

**落地**：`src/Workbench.Application/Services/FileChangeService.cs`、
`src/Workbench.Infrastructure/GitInspector.cs`（numstat / name-status 解析）
**自动化证据**：端到端 **F6**（提交后产出文件自动落库，含路径与 commit sha）；单测 `FileChangeTests`（22）

### 3. 数据库调用怎么查

| 问题 | 查法 |
|---|---|
| 谁对哪个库哪张表跑了什么 SQL | 「SQL 执行记录」页（`/db-calls`） |
| 这张表被哪些任务改过 | 按目标表筛 |
| 影响了几行 | 行上的「影响行数」 |
| 这次写是谁批的 | 行上的审批 id → `/approvals` |
| 接口 | `GET /api/db-calls`（按项目 / 连接 / 库 / 表 / 类型 / 时间筛，服务端真分页） |

**为什么单开一张表**：通用工具审计表是给所有工具用的，SQL 原文、目标 server/database、
目标表全埋在那行的 JSON 里 —— 查不动。这里把它们摊成独立的列。

**落地**：`src/Workbench.Application/Services/DbCallService.cs`、
`src/Workbench.Domain/Entities.cs`（`DbCallRecord`）
**自动化证据**：单测 `DbCallRecordTests`（6）；端到端 **D8/D9**（只读判定与审批闸门）

---

## 四、端到端七条链路是什么

`./tests/e2e/run.sh` 自起临时工作台（临时 `WORKBENCH_HOME`），跑完即收：

| 链路 | 内容 | 断言数 |
|---|---|---|
| A | 从编号到结项：`req001` → claim → 上下文 → 回写计划 → 推进 Review → 开评审轮 → 取评审包 → 逐维度回写 7 条 → 结项 | 23 |
| B | 评审没过不许结项：只评 6 个维度 / 有一个维度不通过，两种都拦下 | 5 |
| C | 类型编号前缀各类型独立计数；分支守卫 | 8 |
| D | 写操作的人工审批门禁（D1–D11，含「只读调用也留痕」） | 44 |
| E | CLI 冒烟：常用命令 + 敏感命令 401 + `wb mcp` stdio 桥接 | 86 |
| F | **agent 会话 ↔ 项目 / 任务绑定**：绑仓库 → 不传 projectId 也能干活 → 跨项目被拒 → 按编号认领 → 审计自动带任务 → 产出文件自动落库 | 25 |
| G | **重启对账**：起服务 → 执行途中杀进程 → 同一个 `WORKBENCH_HOME` 再起 → 那次写操作标成「结果未知」 | 8 |

合计 **199 条断言**，全过。

---

## 五、还没做到的（诚实清单）

企业落地前要知道边界在哪。下面每一条都是**真实缺口**，不是「计划中」：

1. **没有身份体系，只有一个共享的人工凭证。**
   token 是「谁在操作」，不是「是谁在操作」—— 审计里的 `decidedBy` 仍然是人自己填的名字，
   填错了系统不知道。已经做的只是**兜底**：裁决人不能为空（空了就拒），并且记下
   所用人工凭证的哈希指纹（不存明文），好区分「同一个操作员」和「换了人操作」。
   企业里要接 SSO / OIDC，并把裁决人换成真实登录态。
2. **没有多租户数据隔离。**
   项目之间靠 `ProjectId` 约束（审批绑项目、查询按项目过滤），但同一个库、同一个进程，
   没有行级隔离也没有租户级密钥隔离。要多租户得改存储层。
3. **单机 SQLite。**
   适合团队级 / 部门级部署，不适合多节点高并发。可用性与并发上限受单机限制。
   WAL 模式与 busy_timeout 有配（`SqlitePragmasTests`），但改不了「只有一个写进程」这件事。
4. **审计表的防篡改做到了「库外可查」，还不是「不可篡改」。**
   审计记录之间用哈希链串起来了（每条带 `prevHash` + `hash`，改动或删除都会让链断），
   「工具调用记录」页面上点「验哈希链」就能验出断在哪一条。链头还可以**归档到工作台数据目录之外**
   （配 `Workbench:AuditArchivePath`，每 10 分钟自动归档一次，界面上也能手动归档），
   对账时能发现「记录被删过」「库被回滚过」「同一条被改过」。
   **但归档文件本身挡不住同样的人** —— 拿到库文件的人如果也能改归档文件，
   照样能把两边一起重算。归档位置必须放在工作台够不着的地方
   （只读挂载、另一台机器、另一个权限域），这一步是部署方的责任。
   另外链只覆盖启用之后写入的记录。
5. **没有真实 SQL Server 的端到端。**
   只读判定发生在连库之前，所以 D8 不需要真库就能验；
   但 `db_execute` 真正连库执行的那一段（事务、超时、批量结果）只有单测覆盖，没有跑过真库。
6. **CI 只接了 GitHub Actions，别的平台还得自己接。**
   `.github/workflows/verify.yml` 每次推送与 PR 都跑 `./tests/verify.sh`
   （编译 → 单测 → 端到端），CI 里没有仓库自带的隔离 SDK 时脚本会用
   PATH 上的 dotnet，版本固定在 8.0.x。但**这个仓库目前没有配远端**，
   推上去之前它只是文件；GitLab CI / Jenkins 要另写一份等价配置。
7. **审批等待是进程内轮询，重启时正在跑的那次写操作结果未知。**
   待审单在库里不会丢；但服务重启时正在执行的那次写操作**可能已经生效**
   （commit 已写进仓库，只是执行方没来得及回写状态），系统判不出来。
   现在的做法：启动时把「上个进程崩在执行中间」的审批挑出来，
   标成**结果未知**（`OutcomeUnknown`），过期并写明「请人工确认后再重新申请」，
   审批页上带一个醒目的标记（端到端链路 G 验的就是这一段）。
   **但它只是标出来，没有「确认已生效 / 确认未生效」的裁决动作** ——
   要闭环还得给这类审批加一条人工确认路径。另外 agent 那边仍是进程内轮询，
   agent 与工作台不在同一个进程里时，重启期间它拿不到通知。
8. **审计留的是「工具调用」，不是「数据访问」。**
   现在每次工具调用都留痕（谁调的、哪个项目、哪个任务、参数、花了多久），
   写操作与数据库访问另记 SQL 原文与结果摘要。
   但**工作台自己的 REST 调用与界面操作不在审计表里** ——
   审计表记的是 agent 通过 MCP 干了什么，不是人点了什么。
   要看人的操作痕迹，目前只有任务的状态事件流水。
9. **没有速率限制、没有配额。**
   MCP 端点对外开放（只监听回环 + Host 白名单），内部 agent 可以高频调用。
   企业内网部署要自己加反向代理限流。

---

## 六、怎么自己验一遍

```bash
# 一键验收：编译 → 单测 → 端到端，给总账
./tests/verify.sh

# 只跑端到端（看明细）
./tests/e2e/run.sh

# 单看审批门禁那条链路
python3 tests/e2e/approval_e2e.py --url http://127.0.0.1:5199

# 单看 CLI 冒烟
python3 tests/e2e/cli_smoke.py --url http://127.0.0.1:5199 \
    --dotnet ./.dotnet/dotnet --cli src/Workbench.Cli/bin/Debug/net8.0/Workbench.Cli.dll
```

想看审批门禁在界面上怎么表现：起服务后开 `/approvals`，
会看到待审列表里每条都写着工具名、目标项目、命令原文，人点批准/撤销都有据可查。

CI（`.github/workflows/verify.yml`）跑的就是上面第一条 `./tests/verify.sh`：
每次推送与每个 PR 都来一遍，产出同样的总账。
