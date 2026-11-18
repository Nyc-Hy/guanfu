# 观复 — 实施计划

> v0.1 征求意见 → v0.2 锁定六项决策 → v0.3 同步实现现状 → v0.4 P3/P4 收尾 →
> **v0.5 评审工作流**（7 维度专家团 + 结项门禁 + 任务编号 + 版本归组）→
> **v0.6 结构与分支**（项目→迭代版本→需求/缺陷/优化、编号按类型分前缀、
> 分支守卫、页面内联看文档、schema 补丁）→
> **v0.7 加固与补缺**（全仓审计后的修复：审批绑定操作、`db_query` 只读化、
> model-driven schema 升级、路径边界、人工凭证、任务依赖/分派/里程碑层级、
> 导出、备份、批量审批、表级规则）→
> **v0.8 归属与可见性**（把「agent 的对话跟项目/任务绑上」这条地基补齐，
> 并让三件事可查：任务进度、产出文件、数据库调用）。
> 计划文档随代码走，放在仓库 `docs/PLAN.md`。

---

## 0. 一句话定位

**这是一个给 agent 用的项目管理工具** —— 第一公民是 agent，人也要用，但人不是唯一用户。

任务、里程碑、依赖、状态机、编号是**内核**；**MCP 工具集**把这些项目管理原语喂给 agent
（agent 像人用 PM 工具那样操作看板，只是操作的是结构化对象，不是去爬 UI）；
**审计与审批**是让人敢信这个看板的底座 —— 看板上的状态是 agent 写的。

底下垫着的是：**状态中枢 + 事件账本 + 审批门禁**。
观复**不含任何 AI 能力**：不调模型、不跑 agent、不做"需求理解"。所有智能动作发生在外部 agent 进程里，它们通过 **MCP 工具**和 **CLI** 驱动观复的状态流转。

「文档解读」是**确定性解析**（结构化还原 + 修订还原），不是 AI 理解，不破坏这条边界。
评审同理 —— 观复给**骨架**（阶段、维度、清单、门禁、历史），判断由 agent（可开子 agent / 专家团）做，再逐维度回写结论。

### v0.8 做了什么

| 能力 | 落地 |
|---|---|
| **项目绑本地仓库** | 创建时必填仓库路径；`RepoPath`（人填的）+ `RepoRoot`（规范化绑定键）两套 |
| **会话绑定** | `AgentSession` 实体；桥接带 `X-Workbench-Repo`；`guanfu_bind_session` 工具 |
| **projectId 可选化** | 19 处工具签名；会话解析优先级「显式 > 会话」，**冲突直接拒** |
| **按编号干活** | `claim(taskKey)` / `get_context(taskKey)` —— agent 不用背 GUID |
| **产出文件自动采集** | `TaskFileChange`；两个采集点（提交后、Review 快照） |
| **数据库调用专页** | `DbCallRecord` 专表 + `/db-calls` 页面 + 服务端真分页 |
| **首页总览** | 等我批 / 在动任务 / 活动会话 / 最近产出 / 最近 SQL |
| **审计哈希链** | `prevHash` + `hash`；改一条断一条，页面可验 |
| **验收** | 单测 739 条；端到端 183 条（新增链路 F 25 条） |

---

### v0.9 做了什么

| 能力 | 落地 |
|---|---|
| **工位独占** | 主工作区（仓库根）同一时刻只允许一个 agent 占；占用判断与认领放在同一条 UPDATE（`!db.Tasks.Any(...)`） |
| **worktree 登记与校验** | `guanfu_claim_task(worktree=)`；读 `.git` 文件确认是本仓库的 worktree（主 checkout 的 .git 是目录，不算） |
| **项目级并行上限** | `Project.MaxConcurrentAgents`（0 = 不限），项目设置页可配；节流阀，不是越权闸门 |
| **工位可见** | `TaskSummaryDto.WorkPath`、`WorkStationDto`；任务详情与总览显示「谁在哪个工位」，`get_context` 也带着 |
| **工具认工位** | `IWorkstationResolver`：`fs_*` / `git_*` / `shell_run` / 产出采集全部按工位走；工位挂在「agent 手上那个活的租约」上，会话没挂任务也找得回来 |
| **会话认 worktree** | worktree 是仓库的兄弟目录（属于同一个仓库）—— 绑定与作用域校验都放它过 |

### v0.10 做了什么

| 能力 | 落地 |
|---|---|
| **项目级规约（一等实体）** | `ProjectSpec`：项目目标 / DoD（逐条）/ 提交约定 / 测试命令 / 分支约定 / 红线。人填（要人工凭证），agent 读（`guanfu_get_project_spec`），上下文包与评审包都带着 |
| **调度板** | `IDispatchService`：能干（ready）/ 有人在干（inProgress）/ 卡着（blocked，带阻塞链深度）。agent 用 `guanfu_get_dispatch_board`，人在项目页「现在能干」页签看同一份 |

### v0.11 做了什么

| 能力 | 落地 |
|---|---|
| **DoD 逐条判定** | `SpecVerdict`：规约的完成定义一条一条判（满足 / 不满足 / 不适用）；`guanfu_submit_spec_verdict`；**没判完 / 判成不满足不许结项**（结项门禁加上规约这一道） |
| **里程碑验收门禁** | 验收要求整棵子树（版本 → 需求组 → 需求）的活都做完；签过字之后里面的活状态冻住、不许再往里加活；要改先撤销验收 |
| **用量** | `TaskUsageService`：工具调用次数 / 工具种类 / 事件数 / 租约时长 —— **只记观复自己数得出来的**；token 与费用刻意不记（观复不知道用的哪个模型什么套餐，自报的数字审计上站不住）；**每任务工具调用上限**（MCP 中央过滤器里生效，被拦下的调用照样进审计） |

### v0.12 做了什么

| 能力 | 落地 |
|---|---|
| **交接记录** | `task.handoff` 事件（from / to）；上一个持有者从事件流里取，不加列 |
| **交接说明** | 交接带原因：上一个是谁、为什么交出去；新领活的 agent 看得见 |

### v0.13 做了什么

| 能力 | 落地 |
|---|---|
| **调度建议「先干哪个」** | `IDispatchService.SuggestAsync`：优先级 + 权重 + 等待时长（防饥饿）+ 解锁数排序；每条带 `reasons`；主工作区被占给工位提示；`guanfu_suggest_tasks` + 项目页「建议先做」表 |

### v0.14 做了什么

| 能力 | 落地 |
|---|---|
| **里程碑级规约** | `MilestoneSpec`：这个版本**额外**的完成定义，叠加在项目级之上（里程碑的字段优先、DoD 两层叠加）；评审逐条判定与结项门禁走**生效**的那一份 |
| **交接上下文** | 领活结果带 `previousHolder` / `handoffReason`（优先取放手时写的理由，没有就取接活时留的上下文）；`guanfu_release_task` 让 agent 能把活交回去 |

### v0.15 做了什么

| 能力 | 落地 |
|---|---|
| **验收 × 规约打通** | 里程碑验收除了「子树里的活都做完」，还要看**这个版本的完成定义逐条判过、且没有判成不满足的**。判定的证据在评审轮次里（每个活评的是生效的那份完成定义），验收时汇总 |

### v0.16 做了什么

| 能力 | 落地 |
|---|---|
| **文档导入的批量入口** | 界面一次选多个文件；REST `POST /api/tasks/{id}/documents/batch`；MCP `doc_upload` 支持 `documents` 数组；CLI `guanfu doc upload <任务ID> <文件...>`。**一个文件失败不牵连其它的** —— 成功的进 `uploaded`，失败的进 `failed`（带文件名与原因） |
| **文档类别统一猜** | 类别（PRD / 用例 / 接口文档）由文件名猜，**服务层统一算**，界面 / REST / MCP / CLI 共用一份 —— 同一份文档从哪进来都带着同一个类别 |
| **agent 侧的离线补报** | 回写计划 / 产物 / 测试结果 / 流水事件都能带 `occurredAt`（实际发生时间）。账本上 `OccurredAt` 是发生时间、`CreatedAt` 是「观复什么时候知道的」；界面给补报的事件挂「补报」标记并摆出两个时间。未来的时间不收，离谱的过去也拒 |

### v0.17 做了什么

| 能力 | 落地 |
|---|---|
| **工位自助化** | `guanfu_get_workstation`：主工作区被谁占着、这次该用哪个目录、要并行的话建哪个工位、**建它的确切命令**。观复不替 agent 建 worktree（规矩 35），只给路径与命令，外加事后校验。建议的工位在**仓库旁边**（`<仓库名>-wb-worktrees/<编号>`），不落在仓库里 |
| **评审的多 agent 分工** | `guanfu_claim_review_dimension` / `guanfu_claim_spec_item`：维度与完成定义都能**认领**（带租约，到期可接手）；别人持有时不能替他交结论。结论按维度独立，**不需要合并** |
| **交接包** | `guanfu_get_handoff_pack` / `GET /api/tasks/{id}/handoff-pack` / 任务页「交接」页签：一次给全 —— 上一个是谁 / 为什么交出来 / 什么时候交的，加上规约、计划、文档、产物、测试、评审轮、工位、允许的迁移。只放结构化事实，不生成自然语言 |

### v0.18 做了什么

| 能力 | 落地 |
|---|---|
| **跨项目的并行总量** | 工作区级并行上限（`WorkspaceSpec.MaxConcurrentAgents`，0 = 不限），跨项目一起算活跃租约。项目级那个管「这个项目里别挤」，这个管「这个工作区里别挤」。两个都是节流阀，上限管的是「几个 **agent**」不是「几个活」 |
| **交接链** | 交接包多给一条 `handoffChain`：每次转手谁交给谁、为什么、什么时候。链从账本的认领 / 放手事件拼出来（不另存一列），顶部的「上一个是谁」也从链上摘 |
| **完成定义的来源可见** | 每条完成定义带来源层级（工作区通用 / 项目通用 / 本版本特有），评审时知道该谁负责。重复的按最外层算；里程碑编辑器把项目级那几条只读摆出来（不用重抄） |
| **工位的清理** | `guanfu_list_workstations`（谁在用、目录还在不在、**有没有人管**）+ `guanfu_release_workstation`（清登记 + 给出 `git worktree remove` 的确切命令）；项目页多一个「工位」页签。**观复不替 agent 删目录** |

顺带修掉两个真问题：
- 工作区标准规约那一页**从来没加载过**（`LoadWorkspaceSpecAsync` 定义了但没人调）——
  界面上永远是「还没有项目」。现在会加载，而且**编辑中不会被 15 秒的自动刷新冲掉**；
- `guanfu_release_task` 拿任务 id 就能放活，不必先绑会话（任务自己知道属于哪个项目），
  并且可以显式带 `agentId`。

### v0.19 做了什么

| 能力 | 落地 |
|---|---|
| **评审分工的收尾** | 评审轮多带一份 `judgements`（维度 → 评审人 → 结论）+ `pendingCount` / `failedCount` —— 收尾的人（协调者）一次调用就知道还差谁的活、哪条不通过。轮次结束的账本事件里也留一份分工，事后翻得到每条是谁说的 |
| **工位的自动收尾** | 任务进终态（已完成 / 已取消）时留一笔「该收了」，工位清单上标出来，任务页给出 `git worktree remove` 的确切命令。**观复仍然不替 agent 删目录** |

### v0.20 做了什么

| 能力 | 落地 |
|---|---|
| **agent 的工作包** | `guanfu_get_my_work`：一次给全「跟我有关的部分」—— 我正做的活（租约到什么时候）、**我卡着谁**（别人在等我的哪个活）、我放掉过的活（为什么交、谁接了）、接下来建议我做什么（带理由）、生效的规约、我认领了还没交的评审维度、我的工位、当前任务能去哪。工位现状由工位服务算，上下文包与工作包共用同一份 |

### v0.21 做了什么（团队身份：凭证、绑定与可见范围）

| 能力 | 落地 |
|---|---|
| **谁在操作必须是真的** | `AccessCredential`（人 / agent + 凭证哈希 + 可见范围）；`/mcp` 端点认凭证，认不出就 401；签发明文只显示一次，可吊销 |

### v0.22 做了什么（归档交付线 · 第 0 步）

| 能力 | 落地 |
|---|---|
| **产物能改，改完留版本** | `ArtifactService`：每次改动多一版（`第 N 版`），旧版**永远留着**（`ArtifactRevision`），流水留一笔 `artifact.updated`（谁、哪一版、为什么改）；界面在「产出」页签直接编辑 + 看版本历史 |
| **类型与类别说人话** | 产物类型补 **SQL 脚本 / 脚本**；文档类别补 **交付文件**；界面一律中文说法（`UiText`），产物类型枚举与中文都认（`SqlScript` / 「SQL 脚本」） |
| **文档看的是渲染结果** | 任务页「就地查看」改走解析后的块（Markdown → 标题 / 列表 / 表格），**不再把原始 md 摊给用户**；解析不出块（脚本、纯代码）才按代码块显示 |
| **打包交付：一个 zip + 一份清单** | 按迭代版本圈候选池（该版本下任务的文档 + 产物）→ 勾选 → 打成 zip，包里带 `清单.md`（给人看）与 `manifest.json`（给机器读）；`DeliveryPackage` 落库（谁、何时、带了什么），涉及的活各留一笔 `delivery.packed`；打包要人工凭证（东西是要发出去的） |

### v0.23 做了什么（团队身份：成员、登录、凭证归属）

| 能力 | 落地 |
|---|---|
| **成员是一等实体** | `Member`（登录名唯一 + 显示名 + 角色 + 密码哈希 + 状态）；登录名按**归一化**唯一（`ZhangSan` 与 `zhangsan` 是同一个） |
| **账号 + 密码登录** | 密码只存 **PBKDF2-HMAC-SHA256 + 每人一个盐**的哈希；`/login` 登录后发一枚签名 Cookie（HttpOnly / SameSite=Strict）；导航栏显示「现在是谁在操作」 |
| **停用即失效** | 停用成员 → 名下的 agent 凭证**立刻作废**（「人走了，钥匙还在」是团队部署最大的洞） |
| **凭证归属成员** | 签发凭证时选归属成员；凭证带出成员身份与角色（MCP 侧的身份也随之可信） |
| **三档端点门禁** | 端点对账测试从两档变三档：**开放**（agent / CI 的正常工作面）/ **角色门禁**（管理员会话管成员）/ **人工凭证敏感**（owner 的钥匙）—— 管理员用自己的账号就能管成员，不必拿到共享 token |

### v0.24 做了什么（可见性、分配与代批）

| 能力 | 落地 |
|---|---|
| **个人数据隔离** | 可见性落在**服务层**（REST / MCP / 界面三条路一样严）：普通成员只看**分派给自己的 + 自己认领的 + 还没分派的**；管理员看全部。任务的归属记在 `Task.AssigneeMemberId`（「负责人」那一列只是显示名） |
| **管理员分配任务** | 任务页「归属成员」下拉；**只有管理员能分给别人**（判定在服务层，普通成员只能分给自己）；未分派的任务谁都能认领 |
| **审批只能个人审批 + 管理员代批** | 审批归**这把钥匙背后的人**（`Approval.AssigneeMemberId`）；普通成员批别人的单子被拒；管理员能代批但**必须写理由**，账上记「实际裁决人」与「被代人」 |

### 接下来该往哪儿长

1. **身份与隔离（第 1 步）** —— 单个部署 = 一个团队。**已做**：成员表、角色、
   账号密码登录、凭证归属成员、停用即失效、可见性过滤、任务分配、
   审批个人批 + 管理员代批。**还没做**：审计里记「这把钥匙是谁的」、
   agent 侧的成员级过滤（现在 agent 走 MCP 看的是全局看板）。
2. **砍清单（第 2 步）** —— 作废产出采集 / 分支守卫 / 工位 / 仓库路径绑定降级、
   评审去掉变更文件列表：观复**只管过程与记录**，不碰代码。
3. **商业化最后一层（第 3 步）** —— 多租户、计费、审计导出。

### 更早的取舍（仍然成立）

1. **成本不做** —— 观复不知道 agent 用什么模型什么套餐，自报的数字在审计上站不住；
   agent 的资源消耗用「工具调用次数」代理就够了
2. **排期与干预** —— 调度建议给了顺序，但人还改不动它（要能手动指派、改优先级）
3. **规约的层级** —— 规约现在是项目级的；「12 月版本」这种粒度要不要单独一套 DoD
4. **多 agent 协作的痕迹** —— 工位与交接是第一块；还需要「谁把活交给谁」的上下文

审计与审批那块已经比初心厚，**不再加戏**。

## 1. 已锁定的六项决策

| # | 决策 | 落地情况 |
|---|---|---|
| 1 | **目标框架 net8.0** | ✅ 全部工程 `net8.0`。开发侧用 `workbench/.dotnet` 里的隔离 .NET 8 SDK 编译与验证，不动系统版 |
| 2 | **SQLite 存观复数据** | ✅ 数据目录里的 `guanfu.db`（默认 `~/.guanfu`；旧目录 `~/.workbench` 仍然认） |
| 3 | **项目库 = SQL Server** | ✅ `Microsoft.Data.SqlClient`。**读默认放行**（`db_query`），**写/改/DDL 必须人工审批**（`db_execute`） |
| 4 | **agent 自己领任务** | ✅ `guanfu_claim_task` 带租约；租约未过期时其他 agent 领不走，过期可再领 |
| 5 | **完整文档解读工具** | ✅ `Guanfu.Documents`：MD/DOCX/PDF/diff → 统一模型，**保留删除线与修订（补丁）语义** |
| 6 | **Git 托管 + 开源 + 可换机** | ✅ 仓库已 git 化（MIT / README / AGENTS.md / CI）；`guanfu workspace export/import` 整目录搬迁（库+文档+密钥）；git 读写工具齐备（写走审批） |

---

## 2. 架构与工程

```
Guanfu.sln
├─ src/
│  ├─ Guanfu.Domain          实体、任务状态机、规则
│  ├─ Guanfu.Application     用例服务、DTO、事件、审批编排
│  ├─ Guanfu.Documents       文档解析引擎（MD/DOCX/PDF/diff → 统一模型）
│  ├─ Guanfu.Infrastructure  EF Core/SQLite、文档存储、Git 检视、SQL Server 执行器、密钥加密
│  ├─ Guanfu.Mcp             MCP 工具集（68 个工具）+ 注册
│  ├─ Guanfu.Web             Blazor UI + REST API + /mcp 端点（宿主进程）、人工凭证、定时备份
│  └─ Guanfu.Cli             guanfu 命令行 + stdio↔HTTP MCP 桥接
└─ tests/Guanfu.Tests        389 项测试
```

**唯一数据通路**：Web 独占 SQLite（唯一写者）。MCP 与 CLI 都是 Web 的 HTTP 客户端，不直连数据库。
理由：SQLite 多进程写会锁冲突；状态机校验 / 审计 / 审批编排必须只有一处实现；审批阻塞需要常驻服务挂起连接。

> 唯一的例外是**工作区导入**：它只是搬文件、不碰数据库，所以 CLI 本地执行。
> 导出不行 —— 库要做一致性快照（`VACUUM INTO`），得由持有数据库的服务来做。

**三个平行前端**（不是叠加关系）：

| 前端 | 使用者 | 独有职责 |
|---|---|---|
| Web UI | 人 | 看板、审批页、文档查看、度量页、工具开关页 |
| MCP 工具集 | agent | 结构化读写；**只暴露读与干活，不暴露「改变观复结构」的动作** |
| CLI (`guanfu`) | 人 / 脚本 / 运维 | 审批的人工决定、上传、增删、开关、启动 agent、搬迁、**stdio 桥接** |

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
    └── 需求组（Milestone 子节点，可选）—— 版本 → 需求组 → 需求 三层
        └── 任务项（TaskItem）—— 类型：需求 / 缺陷 / 优化
            ├── 编号：REQ-001 / BUG-001 / OPT-001（各类型独立计数）
            ├── 绑定分支（可选）：绑了就只有当前分支对得上才运行
            ├── 分派：负责人 / 优先级 / 截止日 / 权重
            ├── 依赖：前置任务（前置没做完就不许领、不许进 InDev）
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

`guanfu_get_task_by_key` 查不到时不抛异常，而是返回 `ok=false` + **项目里现有的编号清单**，
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

`guanfu_get_context` 也会把 `currentBranch` / `branchReady` 一起带出去，
agent 拿到上下文就知道现在能不能开跑。

### schema 补丁（不是 EF Migrations）

这个项目用 `EnsureCreated` 建表，不走 EF Migrations。但 SQLite 的 `EnsureCreated`
**不会给已存在的表补列、补表、补索引** —— 不补的话升级后老数据库一查就报
`no such column` / `no such table`。

`SchemaUpgrader` 现在**完全从 EF 模型推导**：`db.Database.GenerateCreateScript()`
生成建表脚本 → 解析出表 / 列 / 索引 → 与实际库比对，缺什么补什么（补表 / 补列 / 建索引，
三步）。加了字段、表、索引**不用改升级器**，模型变了下次启动自动跟上。

> 以前维护的是一份**手写补列清单**，只覆盖了 `tasks` 表的两列 ——
> `projects` 的四个列、`tasks` 的 `Key`/`MilestoneId` 加上五张新表全都没补上，
> 老库一升级就炸。手写清单必然会漏，改成模型驱动后这个坑就没了。

另外 `schema_versions` 表记着当前库的**模型指纹**（建表脚本的哈希），
不用再猜「这个库到底升级到哪了」。

---

## 3.2 评审工作流（7 维度专家团）

```
开发完成 → guanfu_start_review（开一轮，7 个维度各建一条待办）
         → guanfu_get_review_pack（拿维度清单 + 任务上下文 + 变更文件）
         → 逐维度评审（agent 可开子 agent 并行）→ guanfu_submit_review（逐条回写）
         → 7 个全过 → guanfu_update_status(Done) 才放行
```

**七个维度**（定义在代码里 `Guanfu.Domain.ReviewDimensions`，不存库）：

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
- 评审**不自动创建** —— `guanfu_start_review` 显式调用才开轮
- 观复**不判断** —— 判断由 agent（或它开的子 agent）做，观复只负责「不评完不许结项」和「每轮结论都留痕」
- 评审**不影响**没开过评审的任务 —— 向后兼容，既有流程照走

---

## 4. MCP 工具（68 个，下划线命名）

**观复组（34）**：`guanfu_list_projects` / `get_project` / `get_project_config` / `create_task` /
`list_tasks` / `get_task` / **`get_task_by_key`**（按编号找任务）/ `claim_task` /
**`get_context`**（一次取全上下文）/ `set_plan` / `update_status` / `add_artifact` /
`report_test_result` / `post_event` / `list_approvals` / `get_approval` / `await_approval` /
`list_milestones` / `assign_milestone` / `get_metrics` /
**`start_review`** / **`get_review_pack`** / **`submit_review`** / **`list_reviews`** / **`get_review`** /
**`list_events`**（事件流水）/ **`list_audit`**（工具调用审计）/ **`archive_task`**（归档，不是删除）/
**`assign_task`**（负责人/优先级/截止日/权重）/ **`list_dependencies`** / **`add_task_dependency`** /
**`remove_task_dependency`** / **`export_events`** / **`export_review`**

**文档组（7）**：`doc_parse` / `doc_get_outline` / `doc_get_text` / `doc_get_revisions` /
`doc_list_struck` / `doc_search` / `doc_list_formats`

**数据库组（4）**：`db_query`（读，**连接层只读**）/ `db_execute`（写，必须审批）/ `db_list_connections` /
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

### 审批必须绑在具体操作上

四个写工具（`db_execute` / `git_commit` / `git_push` / `shell_run`）共用**一套**闸门
（`IApprovalGate`）。闸门比对的不只是「批过没有」，还有：

| 比对项 | 挡住什么 |
|---|---|
| `ToolName` | 批了 `shell_run` 拿去调 `git_commit` |
| `PayloadHash`（参数 SHA-256 指纹） | 批了「提交 A」拿去提交「提交 B」 |
| `ProjectId` | 批了 A 项目的拿去操作 B 项目 |
| `Status = Approved` + `ExpiresAt > now` | 重放已用过的审批 |

状态流转 `Approved → Executing → Executed`，靠**条件更新原子消费**
（`UPDATE ... WHERE Id=@id AND Status='Approved' AND ToolName=@t AND PayloadHash=@h`），
靠影响行数判定放行 —— 这是「一条审批只执行一次」的唯一可靠落点。

### 人工凭证（REST 敏感端点）

REST API 原本零认证，agent 能自己批自己的审批。现在敏感端点要人工凭证：

| 端点 | 为什么敏感 |
|---|---|
| `POST /api/approvals/{id}/decide` | 裁决审批 —— 门禁的最后一道 |
| `POST /api/approvals/batch-decide` | 批量裁决（单条那条没保护，绕一圈就漏了） |
| `PUT /api/projects/{id}/config` | 改 `agentCommand` / 命令白名单 |
| `POST /api/projects/{id}/db-connections` | 存数据库密码 |
| `POST /api/tasks/{id}/launch-agent` | 执行外部命令 |
| `POST /api/workspace/export` / `import` | 整目录搬家 |

token 落在数据目录的 `.human-token`（0600，重启不变），启动横幅打印。
请求带 `X-Guanfu-Human-Token` 头或 `wb_human_token` Cookie 均可，
比对走 SHA-256 + `FixedTimeEquals`（先哈希再比，免得泄长度）。
MCP 端点**不带**这个门槛 —— agent 走的就是它，它不能有裁决权。

### `db_query` 是真的只读

以前 `db_query` 把 SQL 原样丢进 `ExecuteReaderAsync` —— ADO.NET 下
`DELETE` / `UPDATE` / `DROP TABLE` 照样真执行，审批门禁被完全绕过。

现在是三层防线：

1. **连接层**：`ApplicationIntent=ReadOnly` + 显式事务 + 统一回滚
   （SQL Server 不支持 `IsolationLevel.ReadOnly`，用显式事务等价）
2. **AST 分类**（`SqlStatementClassifier`）：用 **ScriptDom**（`TSql160Parser`）
   把 T-SQL 解析成 AST，按语句类型分三档：

   | 档位 | 含义 | 处理 |
   |---|---|---|
   | `Read` | 确认是只读 | 直接放行 |
   | `Write` | 确认是写（DML / DDL / `SELECT INTO`） | 拦，指路走 `db_execute` |
   | `Unknown` | **认不出来**（EXEC / SET / 解析失败 / OPENROWSET） | 拦，**一律转人工审批** |

   为什么不用「剥注释取首关键字」：`WITH c AS (...) SELECT` 与
   `WITH c AS (...) UPDATE` 首关键字都是 WITH，`SELECT ... INTO` 首关键字是 SELECT
   但会建表 —— 只有看 AST 的根节点才分得开。上一版就是靠首关键字，漏过。
   「认不出来」归到需要审批那一边，不归到放行那一边。
3. **审批层**：写走 `db_execute`，照旧要人工批

另外 `db_table_rules` 表以前在库里却没人读它（语义等于不存在），
现在它真的管事：连接上配了「这张表免审批写」的表，写操作直接执行（仍然记审计）。

---

## 7. 分期现状

| 阶段 | 状态 | 说明 |
|---|---|---|
| **P0 骨架** | ✅ 完成 | 解决方案、SQLite schema、状态机、CRUD、Blazor 页面 |
| **P1 MCP + CLI** | ✅ 完成 | 25 个 MCP 工具、REST 回调、`guanfu` CLI、stdio↔HTTP 桥接 |
| **P1.5 文档解读** | ✅ 完成 | 四种格式解析、删除线与修订还原、`doc_*` 工具、文档详情页 |
| **P2 审批审计** | ✅ 完成 | 审批模型、审批页、`db_query`/`db_execute`、审计表、过期、SSE 预留 |
| **P3 Git + 通用工具** | ✅ 完成 | `git_commit`/`git_push`（走审批）、`fs_list`/`fs_read`（越界拒绝）、`db_schema`、`shell_run`（白名单+强制审批，不走 shell 解析） |
| **P4 开源就绪** | ✅ 完成 | LICENSE / README / AGENTS.md / CI；`guanfu workspace export/import`（整目录搬迁，含库+文档+密钥）、「启动 agent」按钮、里程碑（实体+服务+看板筛选）、度量看板（流速/周期/通过率/阻塞/停滞） |
| **P5 评审工作流** | ✅ 完成 | 评审实体（轮次 + 逐维度结论）、7 维度定义与检查清单、`ReviewService`、**结项门禁**、5 个评审 MCP 工具、任务编号 Key、按编号找任务、任务详情页评审面板、`guanfu review` 子命令 |
| **P6 结构与分支** | ✅ 完成 | 任务类型（需求/缺陷/优化）、编号按类型分前缀独立计数、**绑定分支 + 分支守卫**（claim / 进 InDev / 启动 agent 三处）、类型与分支的 MCP/REST/CLI 面、页面内联查看生成的文档、schema 补丁（老库补列） |
| **P7 加固与补缺** | ✅ 完成 | 全仓审计（94 个 `.cs` + 15 个 `.razor`）后的修复与补缺：审批绑定操作 + 原子消费、`db_query` 三层只读、model-driven schema 升级、路径边界（分隔符 + 软链）、人工凭证门禁、限流与健康检查、Markdown URL scheme 白名单、任务依赖 / 分派 / 里程碑层级、事件与评审导出、SQLite 定时备份、批量审批、表级规则、任务归档、Git 进程健壮性、前端异常兜底与防重复提交 |

### P3~P6 新增界面

| 页面 | 作用 |
|---|---|
| `/projects/{id}/settings` | 仓库路径、git 远端、agent 启动命令、命令白名单、审批通知 webhook |
| `/`（总览·项目页签） | 项目仪表盘：顶部全局汇总（项目 / 任务 / 进行中 / 待审 / 阻塞 / 停滞），下面每个项目一块 —— 进度、六个指标格、状态分布、近 8 周流速 |
| `/mcp-tools` | MCP 工具开关：按分类（观复组 / 文档组 / 数据库组 / 通用组）逐个开关；关掉的工具从 `tools/list` 隐藏且调用被拒 |
| `/connect` | 接入 agent：HTTP 与 stdio 两份可直接粘贴的配置（地址按实际访问地址算）、当前暴露工具数、典型流程 |
| 看板页 | 里程碑筛选 + 里程碑增删 + 任务编号显示 |
| 任务详情页 | 迭代版本归属、**「启动 agent」按钮**（拉起外部 CLI，输出落数据目录的 `runs/{taskId}/`）、**评审面板**（7 维度表格 + 轮次判定 + 历史轮次 + 开始评审）、**任务属性面板**（改类型 / 绑分支 / 显示仓库当前分支与就绪状态）、**文档就地查看**（产物按 Markdown 渲染、上传文档显示大纲与正文） |
| 看板页 | 任务卡显示类型徽章（需求蓝 / 缺陷红 / 优化绿）与绑定分支；新建任务可选类型与分支 |

---

## 8. 明确不做

- 观复内不调模型、不跑 agent、不生成需求理解与计划（那是 agent 回写的内容）
- 观复内**不替 agent 判断代码好不好** —— 评审观复只给骨架、清单、门禁、历史
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
| CLI 自己抄了一份 JSON 选项 | 读的时候能读枚举、打印的时候写成 `0` | JSON 选项集中到 `CliJson` / `GuanfuClient`，读写用同一份 |
| 导入也走 REST | 换机时服务还没起，导不进去；目标还不能是正在跑的 home | 导入是纯文件搬运，CLI 本地执行；导出才需要服务（要一致性快照） |
| MCP 的 `tools/list` 是会话级 | 没法按项目返回不同工具清单 | 开关做成**观复级**；按项目管执行范围用项目配置里的命令白名单 |
| 人嘴里的编号少个连字符 | 「req001」查不到库里的「REQ-001」，agent 以为任务不存在 | 编号归一化（只留字母数字、转小写）后精确匹配；查不到时把项目现有编号一并返回 |
| 编号精确匹配放在 SQL 里 | 归一化是 C# 逻辑，EF 翻译不了直接抛异常 | 编号过滤挪到内存里做（项目内任务量很小） |
| `git status --porcelain` 输出整段 `Trim()` | 第一行常以空格开头（`" M src/x.cs"`），前导空格被吃掉 → 第一条路径变成 `rc/x.cs` | 只 `TrimEnd()`；补了真实仓库的回归测试 |
| 工具抛异常被 SDK 吞成英文 | agent 只看到 `An error occurred invoking 'guanfu_create_task'.`，不知道是编号重了还是里程碑错了 | 最外层加 `CallToolFilters` 兜底，统一转成 `{"ok":false,"error":"..."}` |
| JSON 默认转义非 ASCII | 中文全变 `\uXXXX`，日志和 agent 都要自己解码 | `GuanfuJson` 统一用 `UnsafeRelaxedJsonEscaping` |
| SQLite 的 `EnsureCreated` 不给已存在的表补列 | 升级后老库一查就报 `no such column: Type` | 加幂等的 `SchemaUpgrader`：查 `PRAGMA table_info` 缺哪个补哪个 |
| 「不知道当前在哪个分支」当成「在对的分支上」 | 任务绑了分支但项目没配仓库路径，守卫放行 → agent 在错的分支上改代码 | 读不到当前分支时**拒绝**，错误信息说明原因 |
| 类型改成需求却改了编号前缀 | 编号已经被 PR 和 agent 引用，改了就找不到 | `SetTypeAsync` 只改类型，编号保持不变 |
| CLI 自己抄了一份 JSON 选项 | 读的时候能读枚举、打印的时候写成 `0` | JSON 选项收敛到应用层的 `GuanfuJson`，CLI 直接用 |
| 审批只看「批过没有」 | 批了 `shell_run` 拿去调 `git_commit`、批了「提交 A」拿去提交「提交 B」，全都能执行 | 闸门加 `ToolName` + `PayloadHash` + `ProjectId` 比对，条件更新原子消费 |
| `db_query` 拿 SQL 直接 `ExecuteReader` | `DROP TABLE Orders` 真落库，审批门禁形同虚设 | 连接层只读 + 显式事务回滚 + AST 分类（三层） |
| 只读判定靠「剥注释取首关键字」 | `WITH c AS (...) UPDATE` 与 `WITH ... SELECT` 首关键字都是 WITH，分不开；`SELECT INTO` 首关键字是 SELECT 但会建表 | 换成 ScriptDom 解析成 AST，按根语句类型判定；认不出来的一律转人工审批 |
| schema 补丁靠手写清单 | 清单只覆盖 `tasks` 两列，老库升级后 `no such column` / `no such table` | 改成从 EF 模型推导，缺什么补什么；加字段不用改升级器 |
| 路径边界用 `StartsWith` | `/tmp/wb-root-secrets` 被当成 `/tmp/wb-root` 的子目录放行 | `PathBoundary`：带分隔符比较 + 解析软链后再判 |
| 导出目录等于数据目录 | `Path.GetFullPath("/home/")` 不等于 `/home`，判断被绕开 → **删掉正在用的库文件** | 归一化 + 带分隔符比较 + 解软链；导出目标目录里有非导出文件也拒 |
| REST API 零认证 | agent 自己批自己的审批；`ASPNETCORE_URLS=0.0.0.0` 时升级为远程命令执行 | 敏感端点要人工凭证（token 0600 + 固定时间比对） |
| Markdown 链接只防裸 HTML | `[x](javascript:alert(1))` 原样输出 → 点一下就执行脚本 | `LinkUrlPolicy` scheme 白名单，`javascript:`/`data:` 等降级成纯文本 |
| DTO 的位置记录给 `Guid` 加默认值 | `Guid WorkspaceId = default` → SDK 生成 JSON Schema 时默认值转不回 `Guid` → **整个服务起不来**，而单测全绿 | 别给值类型加默认值（或写 `Guid?`）；`McpToolContractTests` 加一条「每个工具的 Schema 都能生成」 |
| CLI 的 multipart 走了 JSON 通道 | `guanfu doc upload` 把 `MultipartFormDataContent` 塞进 JSON 序列化 → 服务端收到一坨 JSON，上传必坏 | 用 `PostMultipartAsync`；端到端补一条 CLI 批量上传的断言 |
| 建议的工位路径用了规范化后的仓库根 | 给 agent 的命令里是 `/private/var/...`，人填的是 `/var/...` —— agent 敲了命令跟人看到的不是一个地方 | 命令与建议都用**人填的路径**，规范化后的根只用于比较 |
| 比对工位时直接比路径字符串 | 登记的是 `/tmp/...`，git 报的是 `/private/tmp/...` —— 同一个工位被算成两个 | 比对前**归一化**（解软链），两边都用解析过的路径比 |
| shell 白名单是命令串前缀 | `git` 一勾就放行 `git push --force`、`npm` 放行 `npm publish` | 白名单按**词**比较，条目可写子命令（`git status`） |
| `git status --porcelain` 中文路径 | 输出 `\346\226\207...`，按路径读文件必然找不到 | 调用加 `core.quotePath=false` + 自写 C 转义解码兜底 |
| `GitInspector` 先同步读完再等退出 | 10 秒超时根本走不到，git 挂住就永久占住线程；父子互等死锁 | 并发读两个流 + `WaitForExitAsync(token)`，超时杀整棵进程树 |
| 超时不杀 git 子进程 | 子进程继续持有 `.git/index.lock`，之后所有 git 命令报错 | 超时/取消都 `Kill(entireProcessTree: true)` |
| Blazor await 期间不重绘，`disabled` 来不及生效 | 连点两次启动两个 agent 进程 | `SubmitLatch` 闩锁 —— Blazor 事件是**排队串行**处理的，标志复位挡不住已排队的点击 |
| 布局加 `@rendermode` | 框架直接拒绝：`Body` 是 RenderFragment，跨渲染模式不可序列化 | 在 `App.razor` 给 `<Routes @rendermode="InteractiveServer" />` 标模式 |
| MCP 桥接把非 JSON 写进 stdout | 服务端回 400/500 的 HTML 时污染 JSON-RPC 流，客户端解析失败 | 识别非 JSON → 转成 JSON-RPC error，error 的 `id` 沿用原请求 |
