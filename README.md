# 开发工作台（Workbench）

> 状态中枢 + 事件账本 + 审批门禁。**工作台不提供 AI 服务**——所有智能动作都发生在外部 agent 进程里，
> 它们通过 MCP 工具与 CLI 驱动工作台的状态流转。

面向「一个项目 → 一堆需求 → agent 干活 → 人审批」的协作场景：把 PRD / 测试用例 / 接口文档
上传进来，把需求理解与开发计划回写下来，把开发过程的产物与单元测试结果记录下来，
把每一次状态变化与每一次数据库写操作都留下可追溯的流水。

---

## 核心设计

### 1. 工作台不含 AI

工作台**不调模型、不跑 agent、不生成需求理解与开发计划**。它提供的是：

| 通道 | 使用者 | 用途 |
|---|---|---|
| **MCP 工具** | 外部 agent（Claude Code / Codex 等） | 读上下文、回写状态、传产物 |
| **REST API** | CI / webhook / 外部脚本 | 回调更新状态 |
| **CLI (`wb`)** | 人 / shell 脚本 | 建任务、推进状态、批处理、审批 |

三条通道汇聚到同一批应用服务，保证状态机校验、事件流水、审计一次不落。

**三个前端是平行的，不是叠加的**：

```
              ┌──────────────────────────────────────────┐
              │  Workbench.Web（宿主进程）                │
              │  应用服务 · 状态机 · 审批编排 · SQLite    │
              └───────────────┬──────────────────────────┘
                              │  REST /api  +  /mcp
        ┌─────────────────────┼─────────────────────┐
        │                     │                     │
   Web UI (Blazor)      MCP 工具集              CLI (wb)
   人：看板/审批/看文档   agent：结构化读写      人/脚本：批处理
                                              ＋ wb mcp stdio 桥接
```

- **CLI 不是 MCP + Web UI 的组合**，是第三个平行入口。CLI 独有的是：审批的人工决定、
  文档上传、里程碑增删、工具开关、启动 agent、工作区搬迁，以及 `wb mcp` 那个
  **stdio 桥接**（只支持 stdio 的 MCP 客户端全靠它接进来）。
- **改变工作台状态的动作不通过 MCP 暴露给 agent**：审批只能由人批（否则 agent 能自己
  批自己的写请求，门禁形同虚设），上传文档、改里程碑结构、调工具开关同理。
- CLI 与 MCP 都用**应用层的同一批 DTO**，不各自抄一份 —— 抄了就会漏字段。

### 2. 单一数据通路

`Workbench.Web` 进程独占 SQLite（唯一写者）。MCP 与 CLI 都是 Web 的 HTTP 客户端，**不直连数据库**——
避免 SQLite 多进程写锁冲突，也避免状态机/审计/审批逻辑散落多处。

### 3. 任务状态机

```
Draft → DocsReady → Planned → InDev → Testing → Review → Done
          │           │         │         │         │         │
          └───────────┴─────────┴─────────┴─────────┴─────────┴──► Blocked
   └──► Cancelled（任意态可取消）
```

- `Draft` 不能直接进 `Blocked`（草稿不算阻塞，要先有文档）
- 非法迁移直接拒绝，并返回**当前允许的迁移列表**（agent 拿得到反馈，不会瞎试）
- `Review → InDev` 是合法的打回回路
- 每次迁移写一条 append-only 的 `status_events`（谁 / 何时 / 从哪来 / 说明）
- **评审门禁**：`Review → Done` 时，若该任务开过评审，当前轮 7 个维度必须全部通过。
  没开过评审的老流程照旧能结项（向后兼容）。拦下来时点名「还差哪几个维度」。

### 3.1 结构：项目 → 迭代版本 → 需求/缺陷/优化

```
项目
└── 迭代版本（如「12月版本」）
    └── 任务项 —— 类型：需求 / 缺陷 / 优化
        ├── 编号：REQ-001 / BUG-001 / OPT-001（各类型独立计数）
        ├── 绑定分支（可选）
        └── 计划 / 产物 / 文档 / 测试结果 / 评审结论
```

- **任务项就是开发任务**：针对单条工作项进行，不跨项。
- **编号按类型分前缀**，各类型独立计数 —— 需求 `REQ-001`、缺陷 `BUG-001`、优化 `OPT-001`。
  人说「看下 bug001」，编号本身就把类型说清楚了。
- **编号匹配认得人嘴里的写法**：`req001` / `REQ-001` / `Req-001` 归一化后是同一个。
  查不到时返回项目里现有的编号清单，agent 能自己纠正。
- **改类型不改编号** —— 编号已经被 PR 和 agent 引用了，说变就变会让人找不到。
- **迭代版本用里程碑承载**：「12月版本」就是一个里程碑，看板上可筛、可看进度、可算完成率。

### 3.2 分支绑定：不在绑定分支上就不会运行

任务项可以绑一个开发分支（如 `feature/login`）。**绑了分支的任务，只有仓库当前分支
与之一致时才会运行**：

| 什么时候拦 | 说明 |
|---|---|
| agent 领取任务 | 领取 = 准备开跑 |
| 状态推进到「开发中」 | 真正动手的那一刻 |
| 「启动 agent」按钮 | 拉起外部进程之前 |

拒绝时错误信息会说明「绑定的是 X，当前在 Y」，agent 能自己纠正。
读不到当前分支（项目没配仓库路径 / git 不可用）时**也拒绝** ——
「不知道当前在哪个分支」不等于「在对的分支上」。
没绑分支的任务不受限制。

`workbench_get_context` 会把 `currentBranch` 和 `branchReady` 一起带出来，
agent 拿到上下文就知道现在能不能开跑。

### 3.3 评审工作流（7 维度专家团）

开发完成后，agent 调评审工具开一轮评审，**工作台给骨架，判断由 agent 做**：

```
workbench_start_review      开一轮，7 个维度各建一条待办
workbench_get_review_pack   取评审包：维度清单 + 任务上下文 + 变更文件列表
   ↓  agent（或它开的子 agent / 专家团）逐维度评审
workbench_submit_review     逐维度回写结论（通过 / 不通过 + 发现 + 证据）
   ↓  7 个维度全过
workbench_update_status(Done)  才放行结项
```

七个维度（定义在代码里，不存库）：

| 维度 | 问的是什么 |
|---|---|
| 代码是否合适 | 这份实现本身写得对不对、选型对不对？ |
| 注释是否合适 | 注释有没有说清「为什么」，而不是复述代码？ |
| 是否有安全问题 | 这段改动有没有引入安全问题？ |
| 是否影响非本次需求的功能 | 这次改动有没有误伤本次需求之外的东西？ |
| 是否有需求未实现 | 对照需求本身，有没有漏做的地方？ |
| 是否有需求过度实现 | 有没有做了需求根本没要求的事？ |
| 是否有过度操作的代码 | 有没有画蛇添足、没必要的动作？ |

每个维度带一份**检查清单**，agent 照着清单查而不是自由发挥。多轮评审全部留 history。
工作台**不判断代码好不好** —— 它只负责「不评完不许结项」和「每轮结论都留痕」。

### 4. 文档解读工具

完整解析 Markdown / DOCX / PDF / diff，**保留语义**而不是拍平成纯文本：

| 语义 | Markdown | DOCX | diff / patch |
|---|---|---|---|
| **删除线** | `~~text~~` | run 的 `w:strike` / `w:dstrike` | — |
| **修订（补丁）** | ` ```diff ` 代码块 | `w:ins` / `w:del` / `w:moveFrom` / `w:moveTo`（带作者+时间） | unified diff 的 +/- 行 |

删除线内容语义是「已废弃」，agent 读时不会当需求；修订清单给出「谁在何时改了什么」。

### 5. 审批与审计

- **读**：默认放心读，直接执行，只记审计日志
- **写 / 改 / DDL / git 写 / shell**：必须人工审批通过后才真正执行
- `shell_run` 额外受**项目白名单**约束：不在白名单里的命令直接拒，连审批都不进
- 审批请求携带 SQL 原文、涉及表、预估影响行数、发起 agent、关联任务上下文
- 所有工具调用与所有审批决定写 append-only 审计表
- 默认 30 分钟未处理自动过期，防僵尸审批堆积
- 项目可配 webhook（钉钉 / 飞书 / 通用），有人请求审批时推一条消息——人不在电脑前时审批不会静静躺到过期

**审批绑在具体操作上。** 一条批准不是「万能令牌」：它同时绑定工具名、参数指纹与项目。
批了「提交 A」拿去提交「提交 B」、批了 `shell_run` 拿去调 `git_commit`，都会被拒。
同一条审批也只能执行一次（`Approved → Executing → Executed`，条件更新原子消费）。

**REST 敏感端点要人工凭证。** 裁决审批、改项目配置、存数据库密码、启动 agent、
工作区搬迁 —— 这些没凭证直接 401。token 在启动横幅里打印，也存在
`WORKBENCH_HOME/.human-token`（0600）。MCP 端点不带这个门槛：
agent 走的就是它，它不能有裁决权。

**`db_query` 是真的只读。** 三层防线：连接层只读（`ApplicationIntent=ReadOnly` +
显式事务回滚）、**AST 分类**（用 ScriptDom 把 T-SQL 解析成树，按语句类型判读写）、
写走审批。分类只有三档：确认只读才放行；确认是写就拦；**认不出来（EXEC、解析失败、
`OPENROWSET`）也拦** —— 一律转人工审批，不猜。

---

## 快速开始

### 环境要求

- .NET 8 SDK（`dotnet --version` 应为 8.0.x）
- git（可选，用于项目级 git 检视）

### 启动

```bash
git clone <repo-url> workbench
cd workbench
dotnet run --project src/Workbench.Web
```

打开 http://127.0.0.1:5177 ，MCP 端点在 http://127.0.0.1:5177/mcp 。

### 接入外部 agent

服务起来后打开 **`/connect`** 页面，里面有两份可以直接复制粘贴的配置（地址按你实际访问的
地址算好），外加当前暴露的工具数和典型流程。

支持 Streamable HTTP 的 MCP 客户端直接连：

```json
{
  "mcpServers": {
    "workbench": {
      "type": "http",
      "url": "http://127.0.0.1:5177/mcp"
    }
  }
}
```

只支持 stdio 的客户端用桥接：

```bash
wb mcp --url http://127.0.0.1:5177
```

> `wb mcp` 桥接是 CLI 唯一不可替代的能力 —— 只支持 stdio 的 MCP 客户端全靠它接进来。
> 改这个桥接时要格外小心（stdout 只能输出 JSON-RPC，任何多余输出都会污染协议流）。

### CLI

```bash
wb serve                              # 启动 Web
wb project list                       # 列出项目
wb project add "AIChat" --desc "桌面端重构"
wb task create <项目ID> "写登录接口" --type requirement --branch feature/login
wb task claim <任务ID> --agent claude
wb task show REQ-001 --project <项目ID>       # 按编号看任务
wb status set <任务ID> Planned --note "计划已出"
wb doc upload <任务ID> ./PRD.md --kind prd
wb approval list
wb approval approve <审批ID>

wb milestone list <项目ID>              # 里程碑
wb milestone create <项目ID> "第一期"
wb metrics [<项目ID>]                   # 度量：流速 / 周期 / 通过率 / 阻塞

wb review start <任务ID>                        # 开一轮评审（7 个维度）
wb review list <任务ID>                         # 看所有轮次与逐维度结论
wb review submit <任务ID> security --fail --findings "有注入风险" --evidence "LoginService.cs:42"

wb project config <项目ID> --repo /path --agent "claude -p \"{title}\"" --allow "dotnet test,npm run build"
wb task launch <任务ID>                 # 拉起外部 agent

wb workspace export /tmp/wb-backup       # 换机：整目录导出（库+文档+密钥）
wb workspace import /tmp/wb-backup --home ~/.workbench   # 本地搬文件，不必服务在跑

wb tool list                             # MCP 工具清单（按分类分组）
wb tool disable shell_run                # 关掉某个 MCP 工具
wb tool reset                            # 全部恢复默认
```

全局参数 `--url`（或环境变量 `WORKBENCH_URL`）指定服务地址。

---

## 典型流程：从一句话到结项

人只说一句自然语言：

> 「你看下工作台中的当前项目 12月版本的 req001，做一下开发」

agent 连上 MCP 之后自己走完：

| 步 | agent 调什么 | 拿到什么 |
|---|---|---|
| 1 | `workbench_get_task_by_key(projectId, "req001")` | 落到具体任务（编号匹配不认连字符写法） |
| 2 | `workbench_claim_task` | 领到租约，多 agent 不抢同一个任务。**任务绑了分支而当前分支不对时，这里就会被拒** |
| 3 | `workbench_get_context` | 任务、文档、计划、产物、事件、允许的状态迁移、git 分支 —— 一次取全 |
| 4 | `workbench_set_plan` | 回写需求理解与开发计划（工作台只存不生成） |
| 5 | 干活 → `workbench_report_test_result` / `workbench_add_artifact` | 测试结果与产物入库 |
| 6 | `workbench_update_status(Testing → Review)` | 进评审态 |
| 7 | `workbench_start_review` | 开一轮，7 个维度各一条待办 |
| 8 | `workbench_get_review_pack` | 每个维度的检查清单 + 任务上下文 + 变更文件列表 |
| 9 | 逐维度评审（可开子 agent 并行）→ `workbench_submit_review` × 7 | 每条结论单独回写 |
| 10 | `workbench_update_status(Done)` | **七个维度全过才放行**；否则返回「还差哪几个维度」 |

第 9 步是 agent 的事：工作台只给骨架、清单、门禁、历史，不替它判断代码好不好。

---

## MCP 工具

**工作台组**

| 工具 | 说明 |
|---|---|
| `workbench_list_projects` | 项目列表（进度、停滞标记、git 分支） |
| `workbench_get_project` | 项目详情 |
| `workbench_create_task` | 建任务（可带 `type` 类型与 `branch` 绑定分支） |
| `workbench_list_tasks` | 按项目 / 状态 / 关键字 / 编号 / 类型列任务 |
| `workbench_get_task` | 任务详情 |
| **`workbench_get_task_by_key`** | **按编号找任务**（`req001` / `REQ-001` 都认） |
| `workbench_claim_task` | 领取任务（带租约，防止多 agent 抢同一任务） |
| **`workbench_get_context`** | **一次取回干活所需的全部上下文**，避免反复零散调用 |
| `workbench_set_plan` | 回写需求理解与开发计划（版本化） |
| `workbench_update_status` | 推进状态（非法迁移会带出允许列表；评审没过不许结项） |
| `workbench_add_artifact` | 上传产物（代码变更、测试报告、commit …） |
| `workbench_report_test_result` | 回写单元测试结果 |
| `workbench_post_event` | 写一条事件流水 |
| **`workbench_start_review`** | **开一轮评审**（7 个维度各建一条待办） |
| **`workbench_get_review_pack`** | **取评审包**：维度清单 + 任务上下文 + 变更文件 |
| **`workbench_submit_review`** | **逐维度回写结论**（通过 / 不通过 + 发现 + 证据） |
| **`workbench_list_reviews`** / **`workbench_get_review`** | 评审历史 / 当前轮汇总 |
| `workbench_list_approvals` / `workbench_get_approval` | 查审批 |
| `workbench_await_approval` | 阻塞等待人工审批 |
| `workbench_list_milestones` / `workbench_assign_milestone` | 里程碑归组 |
| `workbench_get_metrics` | 度量（流速 / 周期 / 通过率 / 阻塞） |
| `workbench_get_project_config` | 项目配置（仓库路径 / 白名单 / agent 命令 / 通知地址） |
| **`workbench_list_events`** | **任务的事件流水**（账本，独立端点，不只从上下文里捞） |
| **`workbench_list_audit`** | **工具调用审计**（谁在什么时候调了哪个工具、结果如何） |
| **`workbench_archive_task`** | **归档任务**（不是删除 —— 流水、产物、评审全留着） |
| **`workbench_assign_task`** | **分派**：负责人 / 优先级 / 截止日 / 权重 |
| **`workbench_list_dependencies`** | 任务的前置依赖与它阻塞了谁 |
| **`workbench_add_task_dependency`** / **`workbench_remove_task_dependency`** | 增删任务依赖（成环会被拒） |
| **`workbench_export_events`** / **`workbench_export_review`** | 导出事件流水 / 评审报告（Markdown / CSV / JSON） |

**文档组**

| 工具 | 说明 |
|---|---|
| `doc_parse` | 解析文档 → 结构化内容（带缓存） |
| `doc_get_outline` | 大纲 |
| `doc_get_text` | 纯文本 |
| `doc_get_revisions` | 修订（补丁）清单：谁、何时、改了什么 |
| `doc_list_struck` | 列出所有删除线内容 |
| `doc_search` | 块级检索 |
| `doc_list_formats` | 格式能力矩阵 |

**数据库组**

| 工具 | 审计策略 |
|---|---|
| `db_query` | 只读，**默认放行**，只记审计日志 |
| `db_execute` | 写 / 改 / DDL，**必须人工审批** |
| `db_list_connections` | 列连接（不含密码） |
| `db_schema` | 表结构（表 / 列 / 类型 / 主键），默认放行 |

**通用组**

| 工具 | 审计策略 | 说明 |
|---|---|---|
| `fs_list` / `fs_read` | 只读，放行 | 项目仓库目录内；越界路径直接拒 |
| `git_commit` / `git_push` | **必须人工审批** | 项目级 git 写操作 |
| `shell_run` | **白名单 + 强制审批** | 命令拆成 exec 参数直接跑，**不经过 shell**，不支持管道与重定向 |

> 写类工具统一是「先建审批 → 等待人工决定 → 批准后带 `approvalId` 再次调用即执行」。
>
> **工具不抛异常**：MCP SDK 会把异常吞成一句 `An error occurred invoking 'xxx'.`，
> agent 就看不到错在哪。所有失败都返回带 `error` 的结构化结果（查不到编号还会把现有编号带回去）。

### 工具开关（`/mcp-tools`）

工作台页面 `/mcp-tools` 可以**按分类**（工作台组 / 文档组 / 数据库组 / 通用组）逐个开关 MCP 工具：

- 关掉的工具**不出现在 `tools/list`** 里 —— agent 看不到，也就不会去调
- 就算客户端缓存了工具清单硬调，服务端也会拒（返回 `isError` 与中文提示）
- 开关落库（只存「被显式改过的」），重启后仍然生效；以后加新工具不用写迁移脚本
- 写类工具（`db_execute` / `git_commit` / `git_push` / `shell_run`）标着「需审批」

作用域是**工作台级**，不是项目级：MCP 的 `tools/list` 是会话级的，没有项目参数，
没法按项目返回不同的工具清单。要按项目管执行范围，用项目配置里的**命令白名单**。

---

## 目录结构

```
Workbench.sln
├─ src/
│  ├─ Workbench.Domain          实体、任务状态机、规则
│  ├─ Workbench.Application     用例服务、DTO、事件、审批编排
│  ├─ Workbench.Documents       文档解析引擎（MD/DOCX/PDF/diff → 统一模型）
│  ├─ Workbench.Infrastructure  EF Core/SQLite、文档存储、Git 检视、SQL Server 执行器
│  ├─ Workbench.Mcp             MCP 工具集 + 注册
│  ├─ Workbench.Web             Blazor UI + REST API + /mcp 端点（宿主进程）
│  └─ Workbench.Cli             wb 命令行 + stdio↔HTTP MCP 桥接
└─ tests/Workbench.Tests
```

---

## 数据与迁移

工作台数据全部在 `WORKBENCH_HOME`（默认 `~/.workbench`）：

```
~/.workbench/
├─ workbench.db        SQLite
├─ documents/          上传的文档原件
├─ secret.key          数据库密码的加密密钥（AES-GCM，0600）
├─ runs/               外部 agent 的运行日志（按 taskId 分目录）
```

换电脑时：

```bash
wb workspace export /tmp/wb-backup          # 导出（库用 VACUUM INTO 做一致性快照）
# 把 /tmp/wb-backup 整个目录拷到新机器
wb workspace import /tmp/wb-backup --home ~/.workbench
export WORKBENCH_HOME=~/.workbench
```

导出的目录含 `workbench.db`、`documents/`、`secret.key` 与 `manifest.json`。
密钥必须一起搬，否则老库的密码解不开。项目配置里的仓库绝对路径需要重新指向。

- **导出**必须走服务：库要用 SQLite 的 `VACUUM INTO` 做一致性快照，边跑边复制会拿到撕裂的页
- **导入**是纯文件搬运，CLI 本地执行即可：换机时服务还没起，先搬完再设 `WORKBENCH_HOME` 就行
  （目标目录正被服务占用时会明确报错，让你先停服务）

---

## 开发

```bash
dotnet build Workbench.sln
dotnet test Workbench.sln
./tests/e2e/run.sh          # 端到端：自起临时服务，跑完 MCP 全链路即收摊
```

端到端脚本 `tests/e2e/mcp_e2e.py` 模拟 agent 走完「按编号找任务 → claim → 取上下文 →
回写计划 → 推进到 Review → 开评审轮 → 取评审包 → 逐维度回写 → 结项」，
外加两条拦截链路（只评 6 个维度就结项、有一个维度不通过就结项，**两种都必须被拒**）。
改完 MCP 工具或状态机后跑一遍。

## License

MIT
