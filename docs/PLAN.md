# 开发工作台 — 实施计划（v0.6，P0~P6 全部落地）

> v0.1 征求意见 → v0.2 锁定六项决策 → v0.3 同步实现现状 → v0.4 P3/P4 收尾 →
> **v0.5 评审工作流**（7 维度专家团 + 结项门禁 + 任务编号 + 版本归组）→
> **v0.6 结构与分支**（项目→迭代版本→需求/缺陷/优化、编号按类型分前缀、
> 分支守卫、页面内联看文档、schema 补丁）。
> 计划文档随代码走，放在仓库 `docs/PLAN.md`。

---

## 0. 一句话定位

**工作台 = 状态中枢 + 事件账本 + 审批门禁 + 评审骨架。**
工作台**不含任何 AI 能力**：不调模型、不跑 agent、不做"需求理解"。所有智能动作发生在外部 agent 进程里，它们通过 **MCP 工具**和 **CLI** 驱动工作台的状态流转。

「文档解读」是**确定性解析**（结构化还原 + 修订还原），不是 AI 理解，不破坏这条边界。
评审同理 —— 工作台给**骨架**（阶段、维度、清单、门禁、历史），判断由 agent（可开子 agent / 专家团）做，再逐维度回写结论。

---

## 1. 已锁定的六项决策

| # | 决策 | 落地情况 |
|---|---|---|
| 1 | **目标框架 net8.0** | ✅ 全部工程 `net8.0`。开发侧用 `workbench/.dotnet` 里的隔离 .NET 8 SDK 编译与验证，不动系统版 |
| 2 | **SQLite 存工作台数据** | ✅ `WORKBENCH_HOME/workbench.db`（默认 `~/.workbench`） |
| 3 | **项目库 = SQL Server** | ✅ `Microsoft.Data.SqlClient`。**读默认放行**（`db_query`），**写/改/DDL 必须人工审批**（`db_execute`） |
| 4 | **agent 自己领任务** | ✅ `workbench_claim_task` 带租约；租约未过期时其他 agent 领不走，过期可再领 |
| 5 | **完整文档解读工具** | ✅ `Workbench.Documents`：MD/DOCX/PDF/diff → 统一模型，**保留删除线与修订（补丁）语义** |
| 6 | **Git 托管 + 开源 + 可换机** | ✅ 仓库已 git 化（MIT / README / AGENTS.md / CI）；`wb workspace export/import` 整目录搬迁（库+文档+密钥）；git 读写工具齐备（写走审批） |

---

## 2. 架构与工程

```
Workbench.sln
├─ src/
│  ├─ Workbench.Domain          实体、任务状态机、规则
│  ├─ Workbench.Application     用例服务、DTO、事件、审批编排
│  ├─ Workbench.Documents       文档解析引擎（MD/DOCX/PDF/diff → 统一模型）
│  ├─ Workbench.Infrastructure  EF Core/SQLite、文档存储、Git 检视、SQL Server 执行器、密钥加密
│  ├─ Workbench.Mcp             MCP 工具集（41 个工具）+ 注册
│  ├─ Workbench.Web             Blazor UI + REST API + /mcp 端点（宿主进程）
│  └─ Workbench.Cli             wb 命令行 + stdio↔HTTP MCP 桥接
└─ tests/Workbench.Tests        162 项测试
```

**唯一数据通路**：Web 独占 SQLite（唯一写者）。MCP 与 CLI 都是 Web 的 HTTP 客户端，不直连数据库。
理由：SQLite 多进程写会锁冲突；状态机校验 / 审计 / 审批编排必须只有一处实现；审批阻塞需要常驻服务挂起连接。

> 唯一的例外是**工作区导入**：它只是搬文件、不碰数据库，所以 CLI 本地执行。
> 导出不行 —— 库要做一致性快照（`VACUUM INTO`），得由持有数据库的服务来做。

**三个平行前端**（不是叠加关系）：

| 前端 | 使用者 | 独有职责 |
|---|---|---|
| Web UI | 人 | 看板、审批页、文档查看、度量页、工具开关页 |
| MCP 工具集 | agent | 结构化读写；**只暴露读与干活，不暴露「改变工作台结构」的动作** |
| CLI (`wb`) | 人 / 脚本 / 运维 | 审批的人工决定、上传、增删、开关、启动 agent、搬迁、**stdio 桥接** |

MCP 里**刻意没有**「批准审批」的工具：审批只能由人做，否则 agent 能自己批自己的写请求，
门禁形同虚设。同理，文档上传、里程碑增删、工具开关都不通过 MCP 暴露。

---

## 3. 状态机

```
Draft → DocsReady → Planned → InDev → Testing → Review → Done
          │           │         │         │         │         │
          └───────────┴─────────┴─────────┴─────────┴─────────┴──► Blocked
   └──► Cancelled（任意态可取消）
```

- `Draft` **不能**直接进 `Blocked`：草稿不算阻塞，要先有文档（`Draft → Blocked` 被状态机拒绝）
- `Blocked` 可以回到流水线的任一状态；`Cancelled` 只能回到 `Draft` 重开
- 非法迁移直接拒绝，并返回**当前允许的迁移列表**（agent 拿得到反馈，不会瞎试）
- `Review → InDev` 是合法的打回回路
- 每次迁移写一条 append-only 的 `status_events`（来源：人 / agent / CI）
- 进度 = 状态权重均值；附加停滞检测、阻塞数、最近测试通过率
- **评审门禁**（服务层的业务前置条件，叠加在状态机之上）：`Review → Done` 时，
  若该任务**开过评审**，当前轮的 7 个维度必须全部通过；没开过评审的老流程照旧能结项。
  拦下来时返回的还是结构化错误，明确点名「还差哪几个维度」「哪几个不通过」。

---

## 3.1 结构：项目 → 迭代版本 → 需求/缺陷/优化

```
Project
└── 迭代版本（Milestone，如「12月版本」）
    └── 任务项（TaskItem）—— 类型：需求 / 缺陷 / 优化
        ├── 编号：REQ-001 / BUG-001 / OPT-001（各类型独立计数）
        ├── 绑定分支（可选）：绑了就只有当前分支对得上才运行
        ├── 计划 / 产物 / 文档 / 测试结果 / 评审结论
        └── 状态机：Draft → DocsReady → Planned → InDev → Testing → Review → Done
```

**任务项就是开发任务**——针对单条工作项进行，不跨项。一个任务项绑一个分支。

| 概念 | 落地 | 理由 |
|---|---|---|
| **任务类型** | `Requirement`（需求）/ `Bug`（缺陷）/ `Optimization`（优化） | 一眼能看出条目性质，也决定编号前缀 |
| **任务编号（Key）** | 按类型分前缀、各类型独立计数（`REQ-001` / `BUG-001` / `OPT-001`），可手工指定，项目内不重号 | 人说话是「看下 req001」「看下 bug001」，编号本身就把类型说清楚了 |
| **编号匹配** | 归一化（只留字母数字、转小写）后精确匹配 | 人嘴里的 `req001` 和库里的 `REQ-001` 差一个连字符，不该让人去猜写法 |
| **迭代版本** | 用**里程碑**承载（「12月版本」就是里程碑） | 里程碑已有筛选 / 进度 / 度量全套 UI；真要版本发布语义（版本号、发布状态、时间范围）再加 Release 实体 |
| **改类型不改编号** | `SetTypeAsync` 只改类型，编号保持原样 | 编号已经被引用了（PR 里写着、agent 记着），说变就变会让人找不到 |

`workbench_get_task_by_key` 查不到时不抛异常，而是返回 `ok=false` + **项目里现有的编号清单**，
让 agent 能自己纠正，而不是卡在这。

### 分支绑定与分支守卫

任务项可以绑一个开发分支（如 `feature/login`）。**绑了分支的任务，只有仓库当前分支
与之一致时才会运行** —— 这是服务层的业务前置条件，和评审门禁一样是「叠加在状态机之上」。

| 守卫点 | 触发时机 |
|---|---|
| `TaskService.ClaimAsync` | agent 领取任务（= 准备开跑） |
| `TaskService.ChangeStatusAsync` → `InDev` | 进入开发中 |
| `AgentLauncher.LaunchAsync` | 「启动 agent」按钮拉起外部进程 |

判定规则：

- **没绑分支** → 放行（不设约束）
- **绑了分支，当前分支一致** → 放行（比较时忽略 `refs/heads/` 前缀与大小写）
- **绑了分支，当前分支不一致** → 拒绝，错误信息带上「绑定的是 X，当前在 Y」
- **绑了分支但读不到当前分支**（项目没配仓库路径 / git 不可用）→ 拒绝
  「不知道当前在哪个分支」不等于「在对的分支上」，这种含糊不能放行

`workbench_get_context` 也会把 `currentBranch` / `branchReady` 一起带出去，
agent 拿到上下文就知道现在能不能开跑。

### schema 补丁（不是 EF Migrations）

这个项目用 `EnsureCreated` 建表，不走 EF Migrations。但 SQLite 的 `EnsureCreated`
**不会给已存在的表补列** —— 不补的话升级后老数据库一查就报 `no such column: Type`。

所以加了一个幂等的补列步骤（`SchemaUpgrader`）：查 `PRAGMA table_info(tasks)`，
缺哪个列补哪个（`ALTER TABLE ... ADD COLUMN`），补过就跳过。只处理「加可空 /
有默认值的列」这类 SQLite 支持且不丢数据的改动。

---

## 3.2 评审工作流（7 维度专家团）

```
开发完成 → workbench_start_review（开一轮，7 个维度各建一条待办）
         → workbench_get_review_pack（拿维度清单 + 任务上下文 + 变更文件）
         → 逐维度评审（agent 可开子 agent 并行）→ workbench_submit_review（逐条回写）
         → 7 个全过 → workbench_update_status(Done) 才放行
```

**七个维度**（定义在代码里 `Workbench.Domain.ReviewDimensions`，不存库）：

| key | 维度 | 问的是什么 |
|---|---|---|
| `code_quality` | 代码是否合适 | 这份实现本身写得对不对、选型对不对？ |
| `comments` | 注释是否合适 | 注释有没有说清「为什么」，而不是复述代码？ |
| `security` | 是否有安全问题 | 这段改动有没有引入安全问题？ |
| `scope_impact` | 是否影响非本次需求的功能 | 这次改动有没有误伤本次需求之外的东西？ |
| `missing_requirement` | 是否有需求未实现 | 对照需求本身，有没有漏做的地方？ |
| `over_implementation` | 是否有需求过度实现 | 有没有做了需求根本没要求的事？ |
| `over_operation` | 是否有过度操作的代码 | 有没有画蛇添足、没必要的动作？ |

每个维度带一份**检查清单**（agent 照着清单查，不是自由发挥）。多轮评审全部留 history。

**边界**：
- 评审**不自动创建** —— `workbench_start_review` 显式调用才开轮
- 工作台**不判断** —— 判断由 agent（或它开的子 agent）做，工作台只负责「不评完不许结项」和「每轮结论都留痕」
- 评审**不影响**没开过评审的任务 —— 向后兼容，既有流程照走

---

## 4. MCP 工具（41 个，下划线命名）

**工作台组（25）**：`workbench_list_projects` / `get_project` / `get_project_config` / `create_task` /
`list_tasks` / `get_task` / **`get_task_by_key`**（按编号找任务）/ `claim_task` /
**`get_context`**（一次取全上下文）/ `set_plan` / `update_status` / `add_artifact` /
`report_test_result` / `post_event` / `list_approvals` / `get_approval` / `await_approval` /
`list_milestones` / `assign_milestone` / `get_metrics` /
**`start_review`** / **`get_review_pack`** / **`submit_review`** / **`list_reviews`** / **`get_review`**

**文档组（7）**：`doc_parse` / `doc_get_outline` / `doc_get_text` / `doc_get_revisions` /
`doc_list_struck` / `doc_search` / `doc_list_formats`

**数据库组（4）**：`db_query`（读，直通）/ `db_execute`（写，必须审批）/ `db_list_connections` /
`db_schema`（表结构，直通）

**通用组（5）**：`fs_list` / `fs_read`（项目仓库内，越界拒绝）/ `git_commit` / `git_push`（**走审批**）/
`shell_run`（**白名单 + 强制审批**，不走 shell 解析）

> 工具名用下划线而非点号：部分 MCP 客户端不接受点号。
> 写类工具（`db_execute` / `git_*` / `shell_run`）统一是「先建审批 → 等待 → 批准后带 `approvalId` 续跑」。
>
> **工具不抛异常**：MCP SDK 会把异常吞成一句 `An error occurred invoking 'xxx'.`，
> agent 看不到错在哪。所以两种做法：工具自己返回带 `error` 的结构化结果；
> 以及最外层一个 `CallToolFilters` 兜底，把任何漏网的异常转成 `{"ok":false,"error":"..."}`。

---

## 5. 文档解读

| 语义 | Markdown | DOCX | diff / patch |
|---|---|---|---|
| **删除线** | `~~text~~` | `w:strike` / `w:dstrike` | — |
| **修订（补丁）** | ` ```diff ` 代码块 | `w:ins` / `w:del` / `w:moveFrom` / `w:moveTo`（带作者+时间） | unified diff 的 +/- 行 |

- 删除线语义是「已废弃」，agent 读时不能当需求
- 修订清单回答「谁在何时改了什么」
- 另支持：标题层级大纲、任务清单勾选状态、表格、代码块、引用、JSON、HTML
- PDF 走 PdfPig 提取带位置的词，按基线聚行、按行距聚段——PDF 无语义级删除线/修订（能力矩阵里如实标注）

---

## 6. 审批与审计

- **读**：默认放心读，直接执行，只记审计日志
- **写 / 改 / DDL / git 写 / shell**：必须人工审批通过后才真正执行
- `shell_run` 额外受**项目白名单**约束：不在白名单里的命令直接拒，连审批都不进
- 独立审批页 `/approvals`：待审列表（风险色标 + 倒计时 + 角标）、详情（SQL 原文 / 影响表 / 发起上下文）、批准 / 驳回（必填理由）
- 闭环：agent 调 `db_execute` → 挂起等待 → 人工批准 → agent 带 `approvalId` 续跑执行（令牌式）
- 所有工具调用与审批决定写 append-only 审计表
- 默认 30 分钟未处理自动过期，防僵尸审批

---

## 7. 分期现状

| 阶段 | 状态 | 说明 |
|---|---|---|
| **P0 骨架** | ✅ 完成 | 解决方案、SQLite schema、状态机、CRUD、Blazor 页面 |
| **P1 MCP + CLI** | ✅ 完成 | 25 个 MCP 工具、REST 回调、`wb` CLI、stdio↔HTTP 桥接 |
| **P1.5 文档解读** | ✅ 完成 | 四种格式解析、删除线与修订还原、`doc_*` 工具、文档详情页 |
| **P2 审批审计** | ✅ 完成 | 审批模型、审批页、`db_query`/`db_execute`、审计表、过期、SSE 预留 |
| **P3 Git + 通用工具** | ✅ 完成 | `git_commit`/`git_push`（走审批）、`fs_list`/`fs_read`（越界拒绝）、`db_schema`、`shell_run`（白名单+强制审批，不走 shell 解析） |
| **P4 开源就绪** | ✅ 完成 | LICENSE / README / AGENTS.md / CI；`wb workspace export/import`（整目录搬迁，含库+文档+密钥）、「启动 agent」按钮、里程碑（实体+服务+看板筛选）、度量看板（流速/周期/通过率/阻塞/停滞） |
| **P5 评审工作流** | ✅ 完成 | 评审实体（轮次 + 逐维度结论）、7 维度定义与检查清单、`ReviewService`、**结项门禁**、5 个评审 MCP 工具、任务编号 Key、按编号找任务、任务详情页评审面板、`wb review` 子命令 |
| **P6 结构与分支** | ✅ 完成 | 任务类型（需求/缺陷/优化）、编号按类型分前缀独立计数、**绑定分支 + 分支守卫**（claim / 进 InDev / 启动 agent 三处）、类型与分支的 MCP/REST/CLI 面、页面内联查看生成的文档、schema 补丁（老库补列） |

### P3~P6 新增界面

| 页面 | 作用 |
|---|---|
| `/projects/{id}/settings` | 仓库路径、git 远端、agent 启动命令、命令白名单、审批通知 webhook |
| `/metrics` | 各项目度量：状态分布、近 8 周流速、平均周期、测试通过率、平均覆盖率、待审数、阻塞与停滞 |
| `/mcp-tools` | MCP 工具开关：按分类（工作台组 / 文档组 / 数据库组 / 通用组）逐个开关；关掉的工具从 `tools/list` 隐藏且调用被拒 |
| `/connect` | 接入 agent：HTTP 与 stdio 两份可直接粘贴的配置（地址按实际访问地址算）、当前暴露工具数、典型流程 |
| 看板页 | 里程碑筛选 + 里程碑增删 + 任务编号显示 |
| 任务详情页 | 迭代版本归属、**「启动 agent」按钮**（拉起外部 CLI，输出落 `WORKBENCH_HOME/runs/{taskId}/`）、**评审面板**（7 维度表格 + 轮次判定 + 历史轮次 + 开始评审）、**任务属性面板**（改类型 / 绑分支 / 显示仓库当前分支与就绪状态）、**文档就地查看**（产物按 Markdown 渲染、上传文档显示大纲与正文） |
| 看板页 | 任务卡显示类型徽章（需求蓝 / 缺陷红 / 优化绿）与绑定分支；新建任务可选类型与分支 |

---

## 8. 明确不做

- 工作台内不调模型、不跑 agent、不生成需求理解与计划（那是 agent 回写的内容）
- 工作台内**不替 agent 判断代码好不好** —— 评审工作台只给骨架、清单、门禁、历史
- 不做 CI 编排（只接收回调）
- 不做 Git 托管平台（只做本地仓库的托管与追踪）
- V1 不做多用户权限
- 文档解析只做结构与修订还原，不做语义理解
- 版本发布语义（版本号 / 发布状态 / 时间范围）暂不做 —— 「12月版本」先用里程碑承载

---

## 9. 实现中踩到的坑（均已补回归测试）

| 坑 | 症状 | 修法 |
|---|---|---|
| SQLite 不能对 `DateTimeOffset` 列 ORDER BY | 所有按时间排序的查询抛 `NotSupportedException` | 全局 `ConfigureConventions` 转 UTC `DateTime` 存储 |
| 空项目进度除零得 NaN | 新建第一个项目后列表接口直接 500 | 除法前判分母 + `double.IsFinite` 兜底 |
| `JsonIgnore(WhenWritingNull)` 用在非空值类型成员上 | 整个文档模块运行时崩溃 | 值类型改用 `WhenWritingDefault` |
| `JsonSerializerOptions(JsonSerializerDefaults.Web)` 的 TypeInfoResolver 是空的 | MCP SDK 调 `MakeReadOnly()` → 启动即崩 | 显式指定 `TypeInfoResolver` |
| Markdig 的 `EmphasisInline` 子节点常是 LeafInline | `as ContainerInline` 拿到 null，删除线/加粗内容整段丢失 | 新增 `ConvertChildInlines` 兼容两种形态 |
| Markdig 代码块文本在 `Lines` 字段不在 `CodeBlockLines` | 围栏代码块内容为空 | 改用 `Lines.ToString()` |
| MCP 会把工具异常吞成泛化信息 | agent 看不到「当前允许的迁移列表」 | 工具改为返回带 `error` / `allowedTransitions` 的结构化结果 |
| 审批在工具返回后才批准 → SQL 永不执行 | 长流程走不通 | `db_execute` 增加 `approvalId` 令牌式续跑 |
| `AwaitAsync` 用注入的业务时钟判超时 | 测试里时钟冻结 → 死等 | 超时改用墙上时间（Stopwatch） |
| `git push` 只给分支不给远端 | 命令变成 `git push main`，git 去找一个叫 main 的远端 → `does not appear to be a git repository` | 只给分支时补 `origin`（`GitWriter.BuildPushArgs`，已补回归测试） |
| 边跑边复制 SQLite 文件 | 导出的是撕裂的库，导入后打不开 | 用 SQLite 的 `VACUUM INTO` 做一致性快照 |
| shell 白名单用字符串前缀匹配 | `dotnet test` 会匹配到 `dotnet test-foo` | 白名单按「完全相等或前缀+空格」匹配，且命令拆成 exec 参数，**不经过 shell** |
| 手写 MCP 工具清单 | 加了工具忘了同步，清单和代码对不上 | 清单从**实际注册的工具**反射出来（`McpToolCatalog`），加了工具自动出现 |
| 工具开关存成「每个工具一行」 | 以后加新工具要写迁移脚本把它打开 | 只存「被显式改过的」，没记录的一律按开启处理 |
| CLI 自己抄了一份 DTO | 字段会漏同步（`MilestoneId` 就漏过一次），改一处要改两处 | CLI 引用 Application，直接用应用层的 DTO |
| CLI 自己抄了一份 JSON 选项 | 读的时候能读枚举、打印的时候写成 `0` | JSON 选项集中到 `CliJson` / `WorkbenchClient`，读写用同一份 |
| 导入也走 REST | 换机时服务还没起，导不进去；目标还不能是正在跑的 home | 导入是纯文件搬运，CLI 本地执行；导出才需要服务（要一致性快照） |
| MCP 的 `tools/list` 是会话级 | 没法按项目返回不同工具清单 | 开关做成**工作台级**；按项目管执行范围用项目配置里的命令白名单 |
| 人嘴里的编号少个连字符 | 「req001」查不到库里的「REQ-001」，agent 以为任务不存在 | 编号归一化（只留字母数字、转小写）后精确匹配；查不到时把项目现有编号一并返回 |
| 编号精确匹配放在 SQL 里 | 归一化是 C# 逻辑，EF 翻译不了直接抛异常 | 编号过滤挪到内存里做（项目内任务量很小） |
| `git status --porcelain` 输出整段 `Trim()` | 第一行常以空格开头（`" M src/x.cs"`），前导空格被吃掉 → 第一条路径变成 `rc/x.cs` | 只 `TrimEnd()`；补了真实仓库的回归测试 |
| 工具抛异常被 SDK 吞成英文 | agent 只看到 `An error occurred invoking 'workbench_create_task'.`，不知道是编号重了还是里程碑错了 | 最外层加 `CallToolFilters` 兜底，统一转成 `{"ok":false,"error":"..."}` |
| JSON 默认转义非 ASCII | 中文全变 `\uXXXX`，日志和 agent 都要自己解码 | `WorkbenchJson` 统一用 `UnsafeRelaxedJsonEscaping` |
| SQLite 的 `EnsureCreated` 不给已存在的表补列 | 升级后老库一查就报 `no such column: Type` | 加幂等的 `SchemaUpgrader`：查 `PRAGMA table_info` 缺哪个补哪个 |
| 「不知道当前在哪个分支」当成「在对的分支上」 | 任务绑了分支但项目没配仓库路径，守卫放行 → agent 在错的分支上改代码 | 读不到当前分支时**拒绝**，错误信息说明原因 |
| 类型改成需求却改了编号前缀 | 编号已经被 PR 和 agent 引用，改了就找不到 | `SetTypeAsync` 只改类型，编号保持不变 |
| CLI 自己抄了一份 JSON 选项 | 读的时候能读枚举、打印的时候写成 `0` | JSON 选项收敛到应用层的 `WorkbenchJson`，CLI 直接用 |
