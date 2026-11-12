# 开发工作台 — 实施计划（v0.4，P0~P4 全部落地）

> v0.1 征求意见 → v0.2 锁定六项决策 → v0.3 同步实现现状 → **v0.4 P3/P4 收尾**。
> 计划文档随代码走，放在仓库 `docs/PLAN.md`。

---

## 0. 一句话定位

**工作台 = 状态中枢 + 事件账本 + 审批门禁。**
工作台**不含任何 AI 能力**：不调模型、不跑 agent、不做"需求理解"。所有智能动作发生在外部 agent 进程里，它们通过 **MCP 工具**和 **CLI** 驱动工作台的状态流转。

「文档解读」是**确定性解析**（结构化还原 + 修订还原），不是 AI 理解，不破坏这条边界。

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
│  ├─ Workbench.Mcp             MCP 工具集（35 个工具）+ 注册
│  ├─ Workbench.Web             Blazor UI + REST API + /mcp 端点（宿主进程）
│  └─ Workbench.Cli             wb 命令行 + stdio↔HTTP MCP 桥接
└─ tests/Workbench.Tests        87 项测试
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

---

## 4. MCP 工具（35 个，下划线命名）

**工作台组（19）**：`workbench_list_projects` / `get_project` / `get_project_config` / `create_task` /
`list_tasks` / `get_task` / `claim_task` / **`get_context`**（一次取全上下文）/ `set_plan` /
`update_status` / `add_artifact` / `report_test_result` / `post_event` / `list_approvals` /
`get_approval` / `await_approval` / `list_milestones` / `assign_milestone` / `get_metrics`

**文档组（7）**：`doc_parse` / `doc_get_outline` / `doc_get_text` / `doc_get_revisions` /
`doc_list_struck` / `doc_search` / `doc_list_formats`

**数据库组（4）**：`db_query`（读，直通）/ `db_execute`（写，必须审批）/ `db_list_connections` /
`db_schema`（表结构，直通）

**通用组（5）**：`fs_list` / `fs_read`（项目仓库内，越界拒绝）/ `git_commit` / `git_push`（**走审批**）/
`shell_run`（**白名单 + 强制审批**，不走 shell 解析）

> 工具名用下划线而非点号：部分 MCP 客户端不接受点号。
> 写类工具（`db_execute` / `git_*` / `shell_run`）统一是「先建审批 → 等待 → 批准后带 `approvalId` 续跑」。

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

### P3/P4 新增界面

| 页面 | 作用 |
|---|---|
| `/projects/{id}/settings` | 仓库路径、git 远端、agent 启动命令、命令白名单、审批通知 webhook |
| `/metrics` | 各项目度量：状态分布、近 8 周流速、平均周期、测试通过率、平均覆盖率、待审数、阻塞与停滞 |
| `/mcp-tools` | MCP 工具开关：按分类（工作台组 / 文档组 / 数据库组 / 通用组）逐个开关；关掉的工具从 `tools/list` 隐藏且调用被拒 |
| `/connect` | 接入 agent：HTTP 与 stdio 两份可直接粘贴的配置（地址按实际访问地址算）、当前暴露工具数、典型流程 |
| 看板页 | 里程碑筛选 + 里程碑增删 |
| 任务详情页 | 里程碑归属、**「启动 agent」按钮**（拉起外部 CLI，输出落 `WORKBENCH_HOME/runs/{taskId}/`） |

---

## 8. 明确不做

- 工作台内不调模型、不跑 agent、不生成需求理解与计划（那是 agent 回写的内容）
- 不做 CI 编排（只接收回调）
- 不做 Git 托管平台（只做本地仓库的托管与追踪）
- V1 不做多用户权限
- 文档解析只做结构与修订还原，不做语义理解

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
