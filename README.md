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

### 3.1 任务编号与版本归组

- 任务有**可读编号**：项目内自动分配 `REQ-001` / `REQ-002`…，可手工指定，项目内不重号。
  人说「看下 req001」，agent 就靠它落到具体任务上。
- 编号匹配**认得人嘴里的写法**：`req001` / `REQ-001` / `Req-001` 归一化后是同一个。
  查不到时返回项目里现有的编号清单，agent 能自己纠正。
- **版本用里程碑承载**：「12月版本」就是一个里程碑，看板上可筛、可看进度、可算完成率。

### 4. 评审工作流（7 维度专家团）

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

### 5. 文档解读工具

完整解析 Markdown / DOCX / PDF / diff，**保留语义**而不是拍平成纯文本：

| 语义 | Markdown | DOCX | diff / patch |
|---|---|---|---|
| **删除线** | `~~text~~` | run 的 `w:strike` / `w:dstrike` | — |
| **修订（补丁）** | ` ```diff ` 代码块 | `w:ins` / `w:del` / `w:moveFrom` / `w:moveTo`（带作者+时间） | unified diff 的 +/- 行 |

删除线内容语义是「已废弃」，agent 读时不会当需求；修订清单给出「谁在何时改了什么」。

### 6. 审批与审计

- **读**：默认放心读，直接执行，只记审计日志
- **写 / 改 / DDL / git 写 / shell**：必须人工审批通过后才真正执行
- `shell_run` 额外受**项目白名单**约束：不在白名单里的命令直接拒，连审批都不进
- 审批请求携带 SQL 原文、涉及表、预估影响行数、发起 agent、关联任务上下文
- 所有工具调用与所有审批决定写 append-only 审计表
- 默认 30 分钟未处理自动过期，防僵尸审批堆积
- 项目可配 webhook（钉钉 / 飞书 / 通用），有人请求审批时推一条消息——人不在电脑前时审批不会静静躺到过期

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
wb task create <项目ID> "写登录接口"
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
| 2 | `workbench_claim_task` | 领到租约，多 agent 不抢同一个任务 |
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
| `workbench_create_task` | 建任务 |
| `workbench_list_tasks` | 按项目 / 状态 / 关键字列任务 |
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
```

## License

MIT
