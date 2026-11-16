# 开发工作台 · 使用说明

> 对应代码：`main` @ `d3e3463`。一键验收：`./tests/verify.sh`（编译 → 单测 → 七条端到端链路）。
> 企业验收逐条证据见 [ENTERPRISE.md](ENTERPRISE.md)，本文只讲「怎么用」。

---

## 0. 它是什么，不是什么

工作台是**状态中枢 + 事件账本 + 审批门禁 + 评审骨架**：

- **追踪任务进度**：任务从草稿到结项的每一步都过状态机，非法迁移被拒并带回「当前允许的迁移列表」。
- **记录开发产出**：代码改动由工作台自己从 git 数出来（提交后、进评审时各采集一次），不靠 agent 自报。
- **留下调用记录**：agent 的**每次** MCP 工具调用都进审计表，串成哈希链；写操作另记审批号、SQL 原文、影响行数。

**它不提供 AI 服务**：不调模型、不跑 agent、不生成需求理解与开发计划。智能在你自己的外部 agent 进程里
（Claude Code / Codex / 自研脚本都行），它们通过 MCP 工具或 CLI 驱动工作台。
评审同理：工作台给阶段、7 个维度的定义与检查清单、结项门禁；**判断由 agent 做**，再逐维度回写结论。

三个平行入口，共用同一批应用服务：

| 入口 | 定位 |
|---|---|
| Web UI | 主路径之一：人看进度、批审批、验审计 |
| MCP 工具集（`/mcp`） | 主路径之一：agent 干活 |
| CLI（`wb`） | 次要入口；但 `wb mcp` 的 stdio 桥接不可替代（只支持 stdio 的客户端全靠它） |

---

## 1. 启动与访问

```bash
./tests/verify.sh                          # 先验收（可选，但建议）
./.dotnet/dotnet run --project src/Workbench.Web   # 仓库自带隔离的 .NET 8 SDK
# 没有 .dotnet/ 时用 PATH 上的 dotnet（需 8.0.x）：
dotnet run --project src/Workbench.Web
```

- 默认地址 **http://127.0.0.1:5177**（UI + REST + `/mcp` 同一个进程）。
  换端口：`dotnet run --project src/Workbench.Web --urls http://127.0.0.1:8080`。
- 只监听回环。内网给别人访问时**必须**显式放行 Host（见 §2），否则 DNS rebinding 防护会拒。
- 启动横幅会打印**人工凭证**（§3）。凭证同时落在 `WORKBENCH_HOME/.human-token`（权限 0600）。
- 老库不用手动迁移：启动时 `SchemaUpgrader` 从 EF 模型推导缺的表/列/索引自动补（不用 EF Migrations）。

CLI 也能起服务：`wb serve`。

---

## 2. 数据目录与配置

数据目录由 **`WORKBENCH_HOME`** 决定，默认 `~/.workbench`：

```
~/.workbench/
├── workbench.db        # SQLite（WAL），唯一写者是 Web 进程
├── documents/          # 上传文档的原件
├── secret.key          # 数据库密码等敏感值的加密密钥
├── runs/               # 外部 agent 的运行日志（按任务分目录）
├── backups/            # 定时备份（每 6 小时）
└── .human-token        # 人工凭证，0600
```

配置项（`appsettings.json`、环境变量双下划线形式、或命令行均可）：

| 键 | 作用 | 默认 |
|---|---|---|
| `Workbench:Home` | 数据目录（等价于 `WORKBENCH_HOME`） | `~/.workbench` |
| `Workbench:AllowedHosts` | 允许的 Host 头清单（数组或逗号串） | 仅回环 |
| `Workbench:AuditArchivePath` | 审计链头的外部归档位置（文件或目录） | 不归档 |

环境变量：`WORKBENCH_HOME`（数据目录）、`WORKBENCH_URL`（CLI 默认服务地址）、
`WORKBENCH_HUMAN_TOKEN`（CLI 人工凭证）。

**归档路径必须落在 `WORKBENCH_HOME` 之外**，落在里面启动后会被拒——那种归档等于没归档。
归档位置本身能不能被同样的人改（只读挂载 / 另一台机器 / 另一个权限域）是**部署方的责任**。

REST 有每分钟 300 次的限流；`/api` 写方法要求 `Origin` 与 `Host` 同源。

---

## 3. 人工凭证（human token）

读操作直通；**敏感操作没凭证直接 401**：

- 裁决审批（含批量）、撤销审批
- 改项目配置（仓库 / 启动命令 / 白名单 / webhook）
- 存数据库连接密码
- 启动 agent
- 工作区导出 / 导入
- 触发审计链头归档

怎么拿：

- **本机开网页**：自动获取，不用管。
- **远程开网页**：从启动控制台复制那串，粘贴到页面上的 token 输入框（如项目设置页）。
- **CLI**：`wb token` 打印；或 `--token <凭证>`；或环境变量 `WORKBENCH_HUMAN_TOKEN`；
  或默认读 `WORKBENCH_HOME/.human-token`。优先级：`--token` > 环境变量 > 文件。

凭证是「证明屏幕前是人」的共享秘密，**不要提交进仓库、不要贴进聊天群**。

---

## 4. 十分钟上手：一条完整链路

以「人 + 外部 agent 协作做一个需求」为例。

1. **建项目、绑仓库**（侧边栏「项目」组里的「＋ 新建项目」）。
   仓库路径填**绝对路径**。绑了之后 agent 在该目录（含子目录）里干活自动落到本项目，不用传项目 ID。
   同一目录绑两个项目是允许的（前后端同仓各绑子目录）；**完全相同**的目录会在保存时给警告——
   那种情况下「按目录认项目」分不出来，调用方得显式指定项目。
2. **建任务**（项目页）。类型三选一：需求 / 缺陷 / 优化，编号自动按类型分前缀
   （`REQ-001` / `BUG-001` / `OPT-001`），各类型独立计数。**改类型不改编号**——编号已被引用。
   可绑分支（之后领取 / 推进开发 / 启动 agent 三处都会校验当前分支）。
3. **传文档**（任务页）：PRD / 测试用例 / API 文档 / 其它。Markdown / DOCX / PDF / diff 都走 AST 解析，
   删除线与修订的语义保留（§10）。传完把任务推到「文档就绪」。
4. **接 agent**：打开 `/connect`，复制现成配置进你的客户端（§11）。
   页面上「接上了吗」会显示最近会话心跳；连没连上也可以看 `/audit`。
5. **agent 领任务干活**：`workbench_claim_task`（带租约，默认 30 分钟，别人抢不走）→
   `workbench_get_context` 一次取回文档 / 计划 / 产物 / 流水 / 允许迁移 →
   `workbench_set_plan` 回写需求理解与开发计划 → 改代码、跑测试 →
   `workbench_report_test_result` 回写测试结果 → `workbench_update_status` 推进。
6. **写操作来审批**：agent 调 `db_execute` / `git_commit` / `git_push` / `shell_run` 时不会直接执行，
   而是返回审批号；你在 `/approvals` 点头后，agent 带审批号再调一次才真正执行（§7）。
7. **评审与结项**：推进到「评审中」后开一轮评审（`workbench_start_review`），
   agent 取评审包、逐维度回写结论；**7 个维度全过**才允许「评审中 → 已完成」（§8）。

人全程只做三件事：建项目建任务传文档、批审批、看审计与度量。

---

## 5. 页面导览

| 路由 | 干什么 | 值得知道的 |
|---|---|---|
| `/` | 总览 | 两个页签：**现在**（要你处理的 / 正在发生的 / 最近产出）/ **项目**（仪表盘：顶部一条全局汇总，下面每个项目一块 —— 进度、完成数、进行中、待审、平均周期、测试通过率、平均覆盖率、状态分布、近 8 周流速）。`?tab=projects` 可直接落地；15 秒自动刷新（不会拨回你的页码） |
| `/projects/{id}` | 项目任务 | 两个页签：**任务**（一张表：任务 / 状态 / 负责人 / 最后动静，元信息并到标题下面；点「状态」格弹菜单，菜单里只有当前合法的目标状态；状态筛选用下拉，50 条一页）/ **迭代版本**（添加、进度、删除）；关键词 / 状态 / 负责人 / 迭代版本 / 含归档筛选；15 秒自动刷新不拨回页码 |
| `/projects/{id}/settings` | 项目设置 | 仓库 / 远端 / 默认分支、agent 启动命令、命令白名单、审批 webhook；迭代版本的说明 / 上级 / 截止日在这里改 |
| `/tasks/{id}` | 任务详情 | 精简视图：次要区块折叠；状态按钮只列**当前允许**的迁移；计划 / 测试 / 产物 / 流水 / 评审 |
| `/approvals` | 审批 | 待批 / 历史两个页签；`?id=` 深链直接定位并高亮那一条；「已等 X · 剩余 Y」两个数分开；批量裁决；撤销 |
| `/audit` | 审计 | 每次工具调用一条；验哈希链；链头归档与对账（查出删过 / 回滚过 / 改过） |
| `/db-calls` | 数据库调用 | SQL 原文与结果摘要；`?sql=` 深链带筛选词落地 |
| `/db-connections` | 数据库连接 | 密码加密存；表级规则（免审批写 / 不许读）；试连 |
| `/mcp-tools` | 工具开关 | 关掉的工具不出现在 `tools/list`，缓存清单硬调也会被拦 |
| `/connect` | 接入 agent | 现成 HTTP / stdio 配置 + 会话心跳 |
| `/metrics` | 度量 | 已并进总览页的「项目」页签；这个路由会跳到 `/?tab=projects`，收藏不会 404 |
| `/search` | 搜索 | 任务 / 项目 / 文档 / 产出文件 / 数据库调用五个页签，各一张表（只出有结果的页签）；`?q=` 可直接带词落地 |
| `/workspace` | 工作区 | 导出 / 导入（库 + 文档 + 密钥 + 运行日志），换机用 |

界面约定：**不露代码标识与 GUID**——你看到的是「提交代码」「开发中」「REQ-001」，
不是 `git_commit` / `InDev` / 一串十六进制。例外只有 `/connect` 与 `/mcp-tools`
（给开发者按名字调 agent 用，原始工具名在次要位置或折叠区）。
列表都有上限 + 客户端翻页；跨页入口带深链，不会把你丢到一个要自己翻的列表。

---

## 6. 任务状态机与编号

状态（中文说法即界面与 CLI 都认的说法）：

```
草稿 → 文档就绪 → 已计划 → 开发中 → 测试中 → 评审中 → 已完成
                 ↘ 受阻 ↙   （受阻可回到除「已完成」外的任一状态）
任何非终态 → 已取消；已取消 → 草稿；已完成 → 受阻
测试中 → 开发中、评审中 → 开发中   （打回重做）
```

精确的允许表以 `TaskStatusMachine` 为准；非法迁移的报错里**带当前允许列表**，agent 靠它自我纠正。
进度权重：草稿 0 / 文档就绪 .1 / 已计划 .2 / 开发中 .5 / 测试中 .7 / 评审中 .85 / 已完成 1。

分支守卫拦在三处：领取任务、推进到「开发中」、启动 agent。
**读不到当前分支也拒**——「不知道在哪个分支」不等于「在对的分支上」。

---

## 7. 审批门禁：写操作怎么批

四个写工具共用同一套闸门：`db_execute`、`git_commit`、`git_push`、`shell_run`。

闭环：

1. agent 第一次调用 → **不执行**，返回 `{ok:false, approvalId, ...}` 与操作摘要；
2. 人在 `/approvals` 批准（或拒绝 / 撤销）；
3. agent 带 `approvalId` 再调同一个工具 → 闸门校验后执行，审批状态
   `Approved → Executing → Executed` 原子消费，**一条审批只执行一次**。

审批**绑在具体操作上**，不是绑在「工具名」上：比对 `ToolName + PayloadHash + ProjectId`。
换命令、换工具、换项目都用不了同一条审批。指纹还把执行对象算进去：

- `db_execute`：`connectionId + server + database + sql`——换个库执行就对不上；
- `git_commit`：**待提交内容的指纹**，且批准之后、执行之前**再算一次**——
  等审批期间工作区变了就拒。人批的是「那些内容」，不是「提交这个动作」。

其它要点：

- 审批默认 **30 分钟**过期（可申请 1 分钟 ~ 24 小时）；过期要重新申请。
- 撤销是后悔药：批了还没执行时可以撤。
- `shell_run` 不走 shell 解析：命令拆成可执行文件 + 参数直接 exec，杜绝 `;` `&` `|` 拼接；
  且必须命中项目白名单。白名单是**按词的前缀**：`git status` 只放行 `git status`，不误伤 `git status-tree`。
- 服务在执行途中重启：那条审批会被标成**已过期 + 结果未知**，交人工确认后再重新申请
  （写操作可能已经生效，工作台不假装知道）。
- 配了审批 webhook（钉钉 / 飞书 / 通用 JSON）时，有人请求审批会推一条消息——人不在电脑前也不会静静躺到过期。

---

## 8. 评审：骨架在工作台，判断在 agent

- 推进到「评审中」后开一轮：`workbench_start_review`（或任务页按钮）。多轮历史都留着。
- `workbench_get_review_pack` 取评审包：计划、diff、产物、测试结果、上一轮结论。
- agent（可以开子 agent / 专家团并行）逐维度回写：`workbench_submit_review`，
  结论三选一：通过 / 不通过 / 不适用（`skipped=true`；「待评」是初始态，不是可回写的结论）。
  不通过必须写清 findings 与证据。任务页上也能逐维度点结论。
  注意：CLI 的 `wb review submit` 目前只有 `--pass` / `--fail`，「不适用」走界面或 MCP。
- **结项门禁**：「评审中 → 已完成」要求当前轮 7 个维度全部有结论且通过。
  「不适用」算过，但有两条约束：必须在 findings 里写明**为什么不适用**，且**每轮最多跳 2 个**
  （否则七个全标跳过就能结项，门禁形同虚设）。没开过评审的老流程不受影响。

七个维度（固定集合，回答的问题互不替代）：

| key | 名字 | 问的是 |
|---|---|---|
| `code_quality` | 代码是否合适 | 实现本身写得对不对、选型对不对 |
| `comments` | 注释是否合适 | 注释有没有说清「为什么」 |
| `security` | 是否有安全问题 | 注入 / 越权 / 凭据 / 传输存储 / 输出编码 |
| `scope_impact` | 是否影响非本次需求的功能 | 有没有误伤别的功能 |
| `missing_requirement` | 是否有需求未实现 | 对照需求有没有漏做 |
| `over_implementation` | 是否有需求过度实现 | 有没有做需求没要求的事 |
| `over_operation` | 是否有过度操作的代码 | 有没有画蛇添足的动作 |

每个维度带检查清单（定义在 `src/Workbench.Domain/ReviewDimensions.cs`），评审包里有。
`wb review export` / `workbench_export_review` 导出评审报告。

---

## 9. 数据库通道

- **连接**在 `/db-connections` 配（SQL Server）。密码加密存（`secret.key`），存密码是敏感操作要凭证。
- **`db_query` 只读通道**：只读判定走 **AST**（ScriptDom），不看首关键字。三档：
  `Read` 放行 / `Write` 拦 / **`Unknown` 也拦**（认不出来一律转人工审批）。
  `WITH c AS (...) SELECT` 与 `WITH c AS (...) UPDATE` 首关键字都是 WITH，`SELECT ... INTO` 会建表——
  只有 AST 分得开。判定逐语句：批处理里有一条写或认不出来，整条就不是只读。
  `OPENROWSET` / `OPENQUERY` / `OPENDATASOURCE` / `OPENXML` 一律 `Unknown`（外层像 SELECT，实质能跑任意 SQL）。
- **`db_execute` 写通道**：必须审批（§7）。
- **表级规则**（`db_list_rules` / 连接页）：某些表免审批写、某些表不许读——把高频低风险操作放开，把敏感表锁死。
- 每次数据库调用都留记录：SQL 原文、目标库、影响行数、审批号、结果摘要。`/db-calls` 可查、可深链。

---

## 10. 文档语义别弄丢

解析走 AST（Markdig / OpenXml / PdfPig），不用正则糊：

- **删除线**（Markdown `~~x~~`、DOCX `w:strike`）→ 语义是「已废弃」，agent 不能当需求读。
  `doc_list_struck` 专门列被划掉的内容。
- **修订**（DOCX `w:ins` / `w:del` / 移动标记、diff 的 +/- 行）→ 带作者与时间。`doc_get_revisions` 取。
- **PDF 没有语义级的删除线 / 修订**，工作台不假装能解析出来。
- 工具：`doc_upload` / `doc_parse` / `doc_get_outline` / `doc_get_text` / `doc_search` / `doc_list_formats`。
- 渲染 agent 产出的 Markdown 时**原始 HTML 关闭**、URL scheme 白名单
  （只放行 http / https / mailto / 相对路径）——内容来自外部输入，这是 XSS 口子。

---

## 11. 接 agent（MCP）

端点：`http://127.0.0.1:5177/mcp`（Streamable HTTP）。当前暴露 **62 个工具**，名字一律下划线。

**HTTP 客户端**（Claude Code / Codex 等较新客户端），`/connect` 页可一键复制：

```json
{
  "mcpServers": {
    "workbench": { "type": "http", "url": "http://127.0.0.1:5177/mcp" }
  }
}
```

**stdio 客户端**走桥接（`wb` 把 stdio 的 JSON-RPC 转发到 `/mcp`）：

```json
{
  "mcpServers": {
    "workbench": { "command": "wb", "args": ["mcp", "--url", "http://127.0.0.1:5177"] }
  }
}
```

开发时不想装 `wb`：`dotnet run --project src/Workbench.Cli -- mcp --url http://127.0.0.1:5177`。

会话与归属：

- 老协议客户端在 `initialize` 时拿到 `Mcp-Session-Id`，之后每次请求带着它——工作台靠它认出「还是那次干活」。
  新协议客户端按请求无状态服务，两种都支持。
- `workbench_bind_session` 按**仓库目录**认项目：agent 工作目录落在哪个项目绑定范围内就绑哪个
  （包含关系，子目录也算）；多个命中取**最具体**的；一样长（分不出来）**就不绑**，让调用方显式指定。
- 认领成功后任务挂到会话上，之后调用自动带 taskId。
- 工具的 `projectId` 可选（会话绑了就省），但**显式传了就要跟会话比**，不一致直接拒——
  否则拿着 A 项目的会话能改 B 项目。
- 调用方身份三层来源：工具显式 `callerId` → 会话里的 agent → 请求头 `X-Workbench-Agent`。
  自己的客户端建议在连接头里表明身份，审计里才有一列「是谁」。

结果形状全局统一：所有工具返回 `ok` + `error`（成功时 error 为空）。
工具真抛异常时中央兜底过滤器转成 `{ok:false,error}`，agent 永远看得到原因。

产出文件靠**采集**不靠自报：① `git_commit` 审批通过真正提交后；② 推进到「评审中」时快照工作区。
`workbench_add_artifact` 只登记非代码产物。

---

## 12. CLI（`wb`）速查

全局：`--url <地址>`（默认 http://127.0.0.1:5177，或 `WORKBENCH_URL`）、`--token <凭证>`（§3）。

```
wb serve / wb mcp                     起服务 / stdio↔HTTP 桥接
wb project list|add|show|config       项目；config 改仓库/启动命令/白名单/webhook（敏感）
wb task list|create|show|claim        任务；list 支持 --status/--key/--type/--branch/--milestone/--limit/--offset
wb task launch|assign                 启动 agent（敏感）/ 分派负责人、优先级、截止日、权重
wb task deps|depend|undepend          任务依赖
wb task events                        导出事件流水（markdown|csv|json）
wb status set <任务> <状态> [--note]   状态迁移（中文或枚举名都认）
wb doc upload <任务> <文件> [--kind]   传文档
wb approval list|approve|reject       审批（敏感）；batch 批量；revoke 撤销
wb review start|list|submit|export    评审；submit 带 --pass|--fail --findings --evidence
wb milestone list|create|delete|assign  里程碑；assign 传 0 摘出来
wb metrics [<项目>]                   度量
wb audit list [--tool/--project/--task/--limit]   审计
wb db connections|rules|test          数据库连接 / 表级规则 / 试连
wb tool list|enable|disable|reset     MCP 工具开关
wb workspace export|import            换机搬迁（敏感）
wb token                              打印人工凭证
```

CLI 用的是应用层同一批 DTO 与 JSON 选项，不会出现「界面有、命令行漏字段」的第二份抄写。

---

## 13. 审计与合规

- **每次工具调用都留痕**（只读也记——企业合规问的是「谁访问了什么资源」）。
  五个写工具自己记得更细（审批号 / 影响行数 / SQL 原文 / 结果摘要），中央那份跳过，不会一行两条。
- 参数里的 `password / secret / token / api_key / access_token` 等名字的值抹成 `***` 再落表。
- 每条审计带 `prevHash + hash`（内容 + 上一条哈希），按单调 `Sequence` 定序（**不靠时间戳**）。
  `/audit` 页「验哈希链」能验出断点：改一条、删一条都会断。
- **链头外部归档**（§2 配置）：每 10 分钟把链头追加到工作台之外的 JSONL；链头没变不重复追加。
  `/audit` 页「对账」能查出**记录被删过 / 库被回滚过 / 同一条被改过**。
  这是「可发现」不是「不可篡改」：能连哈希一起重算的人照样骗得过库内链，所以才要库外归档。
- 事件账本机器可读：`EventType` 是固定标识、`Payload` 是结构化 JSON（note 事件也带），
  消费方按 `EventType` 判定，不猜自由文本。`workbench_export_events` 导出。
- **边界**：审计记的是 agent 通过 MCP 干了什么；**人点界面 / 直接调 REST 的操作不进审计表**
  （人的痕迹在任务状态事件流水里）。要给人操作留痕是另一个设计，见 ENTERPRISE.md 缺口清单。

---

## 14. 运维

- **备份**：每 6 小时自动备份到 `WORKBENCH_HOME/backups`。
- **换机**：`wb workspace export <目录>` → 拷走 → `wb workspace import <目录> --home <目标WORKBENCH_HOME>`。
  导出带库 + 文档 + 密钥 + 运行日志。归档文件不在导出里（它本来就该在别处）。
- **重启对账**：执行途中进程被杀，再起同一个 `WORKBENCH_HOME` 时，那条写操作审批被标
  「已过期 + 结果未知」，审批卡上有明显标记，交人工确认。端到端链路 G 真杀进程验这条。
- **CI**：`.github/workflows/verify.yml` 每次 push / PR 跑 `./tests/verify.sh`。
  仓库还没有 remote 时这份 CI 是待命的——推上去才生效。
- **验收**：`./tests/verify.sh` = 编译（0 警告门槛）→ 单测 → 七条端到端链路
  （A/B/C MCP 主链路与评审拦截与编号分支、D 审批门禁、E CLI 与 stdio 桥接、F 会话绑定、G 重启对账）。

---

## 15. 常见问题

| 症状 | 原因与处理 |
|---|---|
| 敏感操作 401 | 没带人工凭证。本机网页自动带；远程粘贴 token；CLI 用 `--token` 或 `wb token` |
| 批了还是拒 | 审批绑操作指纹：命令 / SQL / 目标库 / 项目换了，或 `git_commit` 等待期间工作区变了，或已执行过。重新申请 |
| 审批卡显示「结果未知」 | 服务在执行途中重启过。人工确认那次写操作到底生效没有，再重新申请 |
| 领取 / 推进 / 启动被分支守卫拒 | 仓库当前分支与任务绑定分支不一致，或读不到分支。切到对的分支 |
| agent 看不到某个工具 | `/mcp-tools` 里被关了。打开即恢复（`tools/list` 与硬调两道都拦） |
| 状态推进被拒 | 非法迁移。报错里带当前允许列表，照着走 |
| 结项被拒 | 当前评审轮有维度没结论或不通过。补齐 / 改完再结 |
| 两个项目绑了同一目录，agent 认不出项目 | 设计如此（分不出来就不绑）。调用时显式传 `projectId`，或改绑子目录 |
| 远程访问网页被拒 / 接口报错 | Host 不在允许清单。配 `Workbench:AllowedHosts`；写接口还要求 Origin 同源 |
| 老库升级后缺列 | 不该发生：`SchemaUpgrader` 从 EF 模型自动补。若真遇到，看启动日志 |
| 端口被占 | 换 `--urls`；或先确认是不是上一个实例没退 |

---

## 16. 已知边界（诚实清单）

完整逐条见 [ENTERPRISE.md](ENTERPRISE.md)。摘要：

- 审计覆盖 agent 的 MCP 调用，**不覆盖人的界面 / REST 操作**。
- 重启对账只**标记**结果未知，没有「人工确认已生效 / 未生效」的回写动作。
- 哈希链 + 库外归档是「可发现篡改」，不是「不可篡改」；归档位置的权限域由部署方保证。
- SQL Server 真库路径目前只有单测与 AST 判定覆盖，未接过生产真库。
- CI 只配了 GitHub Actions；仓库无 remote 时待命。