# AGENTS.md

给后续在这个仓库里干活的 AI agent 与协作者。

## 这是什么

开发工作台：**状态中枢 + 事件账本 + 审批门禁 + 评审骨架**。

**工作台不提供 AI 服务** —— 不调模型、不跑 agent、不生成需求理解与开发计划。
所有智能动作发生在外部 agent 进程里，它们通过 MCP 工具与 CLI 驱动工作台的状态流转。
改动时不要往工作台里塞模型调用。

**评审同理**：工作台给的是**骨架** —— 阶段、7 个维度的定义与检查清单、结项门禁、多轮历史。
判断由 agent（可以开子 agent / 专家团并行）做，再逐维度回写结论。
别在工作台里写「这个代码好不好」这种判断逻辑。

## 技术栈与约束

- .NET 8（`net8.0`），EF Core 8 + SQLite
- ASP.NET Core Blazor Web App（`InteractiveServer` 渲染模式）
- MCP：`ModelContextProtocol` 2.4.0，工具名一律用下划线（`workbench_get_context`），
  不要用点号 —— 部分 MCP 客户端不接受点号
- 文档解析：Markdig / DocumentFormat.OpenXml / PdfPig，全部走 AST，不用正则糊

## 几条不能破的规矩

1. **单一数据通路**：`Workbench.Web` 独占 SQLite，是唯一写者。
   MCP 与 CLI 都是 Web 的 HTTP 客户端，**不要**让它们直连数据库。
2. **状态迁移必须过 `TaskStatusMachine`**：非法迁移要拒绝，并且要把
   **当前允许的迁移列表**带回给调用方（agent 靠这个自我纠正）。
3. **MCP 工具不要抛异常**：SDK 会把异常吞成一句 "An error occurred invoking ..."，
   agent 就看不到原因了。要返回带 `error` 字段的结构化结果。
4. **写操作必须审批**：读直通，写走 `db_execute` / `git_commit` / `git_push` /
   `shell_run` → 审批 → 批准后带 `approvalId` 续跑执行。这几个工具共用同一套
   闸门，别各写各的。
5. **SQLite 不能对 `DateTimeOffset` 列排序**：所有 DateTimeOffset 一律经
   `UtcDateTimeOffsetConverter` 转成 UTC DateTime 存储，别绕过这个约定。
6. **shell 不走 shell 解析**：`shell_run` 的命令被拆成可执行文件 + 参数直接 exec，
   杜绝 `;` / `&` / `|` 拼接注入；且必须命中项目白名单（`Project.AllowedCommandsJson`）。
   别改成 `ProcessStartInfo.Arguments` 或 `cmd /c`。
7. **外部命令只在白名单里跑**：git 命令走 `IGitWriter`（同样不走 shell），
   agent 启动命令走 `IAgentLauncher`。工作台不提供 AI，只执行用户配置好的外部命令。
8. **CLI 用应用层的 DTO，别自己抄一份**：CLI 引用 `Workbench.Application`，
   直接用 `TaskSummaryDto` / `ApprovalDto` / `ProjectConfigDto`。抄第二份就会漏字段
   （`MilestoneId` 漏过一次）。JSON 选项同理，统一用应用层的 `WorkbenchJson`，
   CLI 只加一层 `WriteIndented`（`CliJson.Pretty`）。
9. **评审门禁是服务层的业务前置条件，叠加在状态机之上**：
   `TaskService.ChangeStatusAsync` 在 `Review → Done` 时校验当前轮 7 个维度全过
   （`IReviewGate`）。**没开过评审的老流程不受影响**，别把这个兼容性去掉。
10. **分支守卫同理，拦在三处**：领取任务（`ClaimAsync`）、推进到 `InDev`、
    「启动 agent」（`AgentLauncher`）。**读不到当前分支时也要拒绝** ——
    「不知道当前在哪个分支」不等于「在对的分支上」。
11. **编号匹配要归一化**：人嘴里的 `req001` 和库里的 `REQ-001` 是同一个编号。
    归一化（只留字母数字、转小写）是 C# 逻辑，**不能放进 SQL 的 Where** ——
    EF 翻译不了会直接抛异常，过滤放内存里做。
12. **编号按类型分前缀**（`REQ-` / `BUG-` / `OPT-`），各类型独立计数；
    **改类型不改编号** —— 编号已被 PR 和 agent 引用，改了就找不到。
13. **读 git 输出别整段 `Trim()`**：`git status --porcelain` 第一行常以空格开头
    （`" M src/x.cs"`），`Trim()` 会把前导空格吃掉，第一条路径就变成 `rc/x.cs`。
    只 `TrimEnd()`。
14. **这个项目不用 EF Migrations，用 `EnsureCreated` 建表**。但 SQLite 的
    `EnsureCreated` **不会给已存在的表补列** —— 加字段时必须同步改
    `SchemaUpgrader` 的补列清单，否则老库一查就报 `no such column`。
15. **渲染 agent 产出的 Markdown 要关掉原始 HTML**：`MarkdownRenderer` 用了
    `DisableHtml()`，内容来自外部输入，这是 XSS 口子，别去掉。

## 常用命令

```bash
dotnet build Workbench.sln
dotnet test Workbench.sln
dotnet run --project src/Workbench.Web          # 起服务（UI + REST + /mcp）
dotnet run --project src/Workbench.Cli -- --help # CLI

./tests/e2e/run.sh                              # 端到端验证（自起临时服务，跑完即收）
```

端到端脚本 `tests/e2e/mcp_e2e.py` 走 MCP 全链路：按编号找任务 → claim → 取上下文 →
回写计划 → 推进到 Review → 开评审轮 → 取评审包 → 逐维度回写 → 结项，
以及两条拦截链路（只评 6 个维度、有一个维度不通过）。
改完 MCP 工具或状态机后跑一遍，比只看单测靠谱。

数据目录由 `WORKBENCH_HOME` 决定（默认 `~/.workbench`），里面是 SQLite、文档原件、密钥文件与
外部 agent 的运行日志（`runs/`）。换机用 `wb workspace export` / `wb workspace import`。

## 三个前端，权重不一样

`Web UI` / `MCP 工具集` / `CLI (wb)` 是**平行**的三个入口，共用同一批应用服务。

**主路径是 Web UI + MCP**（人看界面，agent 走工具）。CLI 是次要入口——
功能上大多被前两者覆盖，但 **`wb mcp` 的 stdio 桥接不可替代**（只支持 stdio 的
MCP 客户端全靠它接进来），改动桥接时要格外小心。

CLI 命令**全部保留**（用户 2026-11-12 决定），不因为「大概率不用」就精简——
但也别在 CLI 上花与主路径不成比例的功夫。

## 代码位置

| 目录 | 职责 |
|---|---|
| `src/Workbench.Domain` | 实体、任务状态机、评审维度定义、规则 |
| `src/Workbench.Application` | 用例服务、DTO、事件、审批编排、评审工作流 |
| `src/Workbench.Documents` | 文档解析引擎（MD/DOCX/PDF/diff → 统一模型） |
| `src/Workbench.Infrastructure` | EF Core/SQLite、文档存储、Git 读写、SQL Server 执行器、工作区搬迁 |
| `src/Workbench.Mcp` | MCP 工具集 |
| `src/Workbench.Web` | Blazor UI + REST API + /mcp 端点（宿主进程） |
| `src/Workbench.Cli` | `wb` 命令行 + stdio↔HTTP MCP 桥接 |

## 文档语义别弄丢

文档解析的重点是保留语义：

- **删除线**（Markdown `~~x~~`、DOCX `w:strike`）→ 语义是「已废弃」，agent 不能当需求读
- **修订**（DOCX `w:ins`/`w:del`/移动标记、diff 的 +/- 行）→ 带作者与时间
- PDF 没有语义级的删除线/修订，别假装能解析出来
