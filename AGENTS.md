# AGENTS.md

给后续在这个仓库里干活的 AI agent 与协作者。

## 这是什么

开发工作台：**状态中枢 + 事件账本 + 审批门禁**。

**工作台不提供 AI 服务** —— 不调模型、不跑 agent、不生成需求理解与开发计划。
所有智能动作发生在外部 agent 进程里，它们通过 MCP 工具与 CLI 驱动工作台的状态流转。
改动时不要往工作台里塞模型调用。

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

## 常用命令

```bash
dotnet build Workbench.sln
dotnet test Workbench.sln
dotnet run --project src/Workbench.Web          # 起服务（UI + REST + /mcp）
dotnet run --project src/Workbench.Cli -- --help # CLI
```

数据目录由 `WORKBENCH_HOME` 决定（默认 `~/.workbench`），里面是 SQLite、文档原件、密钥文件与
外部 agent 的运行日志（`runs/`）。换机用 `wb workspace export` / `wb workspace import`。

## 代码位置

| 目录 | 职责 |
|---|---|
| `src/Workbench.Domain` | 实体、任务状态机、规则 |
| `src/Workbench.Application` | 用例服务、DTO、事件、审批编排 |
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
