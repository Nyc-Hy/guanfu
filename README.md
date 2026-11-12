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

### CLI

```bash
wb serve                              # 启动 Web
wb project list                       # 列出项目
wb project add "AIChat" --desc "桌面端重构"
wb task create <项目ID> "写登录接口"
wb task claim <任务ID> --agent claude
wb status set <任务ID> Planned --note "计划已出"
wb doc upload <任务ID> ./PRD.md --kind prd
wb approval list
wb approval approve <审批ID>

wb milestone list <项目ID>              # 里程碑
wb milestone create <项目ID> "第一期"
wb metrics [<项目ID>]                   # 度量：流速 / 周期 / 通过率 / 阻塞

wb project config <项目ID> --repo /path --agent "claude -p \"{title}\"" --allow "dotnet test,npm run build"
wb task launch <任务ID>                 # 拉起外部 agent

wb workspace export /tmp/wb-backup       # 换机：整目录导出（库+文档+密钥）
wb workspace import /tmp/wb-backup --home ~/.workbench
```

全局参数 `--url`（或环境变量 `WORKBENCH_URL`）指定服务地址。

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
| `workbench_claim_task` | 领取任务（带租约，防止多 agent 抢同一任务） |
| **`workbench_get_context`** | **一次取回干活所需的全部上下文**，避免反复零散调用 |
| `workbench_set_plan` | 回写需求理解与开发计划（版本化） |
| `workbench_update_status` | 推进状态（非法迁移会带出允许列表） |
| `workbench_add_artifact` | 上传产物（代码变更、测试报告、commit …） |
| `workbench_report_test_result` | 回写单元测试结果 |
| `workbench_post_event` | 写一条事件流水 |
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

---

## 开发

```bash
dotnet build Workbench.sln
dotnet test Workbench.sln
```

## License

MIT
