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
   **结果形状全局统一**：所有工具都返回 `ok` + `error`（成功时 `error` 为空），
   agent 只用一套规则读所有工具。别返回裸字符串 / 裸 bool / `object` ——
   `false` 是「结果是 false」还是「失败了」，agent 分不出来。
   工具真抛异常时，全局 `CallTool` 兜底过滤器会转成 `{ok:false,error}`（`isError=true`），
   但工具自己判出来的失败也要给同样的形状。契约由 `McpToolContractTests` 钉住。
4. **写操作必须审批**：读直通，写走 `db_execute` / `git_commit` / `git_push` /
   `shell_run` → 审批 → 批准后带 `approvalId` 续跑执行。这几个工具共用同一套
   闸门（`IApprovalGate`），别各写各的。
   **审批必须绑在具体操作上** —— 闸门比对 `ToolName` + `PayloadHash`（参数指纹）
   + `ProjectId`，一条审批换个命令、换个工具、换个项目都用不了；
   状态流转是 `Approved → Executing → Executed`，靠条件更新原子消费，
   一条审批只执行一次。别改回「只看 Status == Approved」。
   **指纹要把「执行对象」一起算进去**：`db_execute` 绑 `connectionId + server +
   database + sql`（换个库执行就对不上）；`git_commit` 绑**待提交内容的指纹**，
   而且批准之后、执行之前要**再算一次**——等审批期间工作区变了就拒。
   人批的是「那些内容」，不是「提交这个动作」。
   **改闸门必须跑 `tests/e2e/approval_e2e.py`**（链路 D）——单测测不到「不批不执行」
   这条真正的闸门语义，只能靠端到端守。
5. **SQLite 不能对 `DateTimeOffset` 列排序**：所有 DateTimeOffset 一律经
   `UtcDateTimeOffsetConverter` 转成 UTC DateTime 存储，别绕过这个约定。
6. **shell 不走 shell 解析**：`shell_run` 的命令被拆成可执行文件 + 参数直接 exec，
   杜绝 `;` / `&` / `|` 拼接注入；且必须命中项目白名单（`Project.AllowedCommandsJson`）。
   别改成 `ProcessStartInfo.Arguments` 或 `cmd /c`。
   白名单条目是**按词的前缀**（`ShellCommandPolicy`）：`"git status"` 只放行
   `git status`，不误伤 `git status-tree`；想收窄就写子命令，别靠字符串前缀。
6.1 **`db_query` 的只读判定走 AST，不看首关键字**：`SqlStatementClassifier` 用
   ScriptDom 把 T-SQL 解析成 AST，按语句类型分三档 —— `Read` 放行 / `Write` 拦 /
   **`Unknown` 也拦**（认不出来一律转人工审批）。
   关键区别：`WITH c AS (...) SELECT` 与 `WITH c AS (...) UPDATE` 首关键字都是 WITH，
   `SELECT ... INTO` 首关键字是 SELECT 但会建表，只有看 AST 才分得开。
   别退回「剥注释取首关键字」那套 —— 那是上一版的实现，漏过。
   **判定是逐语句的**：一个批处理里只要有一条写（或认不出来），整条就不是只读。
   `OPENROWSET` / `OPENQUERY` / `OPENDATASOURCE` / `OPENXML` 这类远端执行通道
   一律判 `Unknown` —— 外层看着是 SELECT，实质能跑任意 SQL。
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
    `EnsureCreated` **不会给已存在的表补列** —— 所以有 `SchemaUpgrader`，
    而它**完全从 EF 模型推导**（`db.Database.GenerateCreateScript()` 解析出表/列/索引，
    缺什么补什么）。**不要**再维护手写清单：手写清单漏过（只覆盖 tasks 两列，
    老库一升级就炸）。加了字段/表/索引不用改升级器，模型变了下次启动自动跟上。
15. **渲染 agent 产出的 Markdown 要关掉原始 HTML**：`MarkdownRenderer` 用了
    `DisableHtml()`，内容来自外部输入，这是 XSS 口子，别去掉。
    URL scheme 也要管（`LinkUrlPolicy`）：`javascript:` / `data:` / `vbscript:` /
    `file:` / 协议相对链接一律降级成纯文本，只放行 `http` / `https` / `mailto` / 相对路径。
16. **路径边界统一走 `PathBoundary`**（`Workbench.Application.Services`）：
    比较必须**带目录分隔符**（`/tmp/wb-root-secrets` 不是 `/tmp/wb-root` 的子目录），
    并且要**解析符号链接后再判一遍**（根目录自身也要解，否则 macOS 上 `/tmp → /private/tmp`
    会误判）。自己写 `StartsWith` 判路径就是在挖洞。
17. **REST 敏感端点要人工凭证**：裁决审批（含批量）、改项目配置、存数据库密码、
    启动 agent、工作区搬迁 —— 这些没 token 直接 401（`HumanTokenMiddleware`）。
    token 落在 `WORKBENCH_HOME/.human-token`（0600），启动横幅打印。
    **新增敏感端点时记得在 `HumanTokenPolicy.IsSensitiveEndpoint` 里补规则**，
    漏一条就等于开了一扇门。
18. **应用层不依赖 Hosting**：`BackgroundService` / `IHostedService` 只能放 `Workbench.Web`。
    应用层只放业务用例与 DTO。
19. **别在 SQL 的 Where 里写 C# 逻辑**：归一化（`NormalizeKey`）之类的转换
    EF 翻译不了会直接抛异常。可以在 SQL 里做**粗筛**（如 `StartsWith` 前缀），
    精确比较放内存里。
20. **事件账本要机器可读**：`StatusEvent.EventType` 是固定标识
    （`TaskEventTypes`），`Payload` 是结构化 JSON。消费方按 `EventType` 判定，
    不要去猜 `Note` 的自由文本。
    **每条事件都要带 `Payload`**（note 事件也要），自由文本只留在 `Note` 里当人看的补充。
21. **Blazor 电路里别共用一个 DbContext**：`InteractiveServer` 注入进来的服务是
    **电路级**的（电路一直活到连接断开），那个 DbContext 会被「定时自动刷新」
    「人工点裁决」共用。两个操作撞上就是
    "A second operation was started on this context instance"。
    每个操作单开一个 scope（`ScopeFactory.CreateScope()`）。
    **页面上新增的定时刷新也一样**：总览、看板的 15 秒自动刷新若跟页面上的
    写操作共用电路级 DbContext，人正拖拽流转状态时定时器来查库，就是同一个错。
    测试里同理：`WorkbenchTestHost` 的审批服务每次给新的 DbContext，
    别让「等审批」的轮询和「裁决」的写共用一个。
22. **REST 端点的敏感分类有测试兜底**：`EndpointClassificationTests` 会枚举所有
    `/api` 写端点，断言该要人工凭证的要、读端点别误伤。
    新增写端点要在 `HumanTokenPolicy.IsSensitiveEndpoint` 补规则（漏一条 = 开一扇门）。
23. **跨站与 DNS rebinding 要挡**：`OriginHostGuardMiddleware` 做两道检查 ——
    `/api` 写方法的 `Origin` 必须与 `Host` 同源；`Host` 头必须在允许清单里
    （默认回环）。只监听回环挡不住 rebinding，浏览器发出来的 `Host` 就是攻击者的域名。
    内网部署用 `Workbench:AllowedHosts` 显式放行。
24. **架构约定要靠测试钉住，不能只写在文档里**：
    `McpToolContractTests`（工具名 / 结果形状 / 写工具的审批标记）、
    `EventLedgerContractTests`（事件必带 Payload）、
    `EndpointClassificationTests`（端点分类）。
    加新工具、新事件类型、新 REST 端点时，对应的测试要跟着过。

25. **绑定键是仓库目录，不是项目 GUID**：
    项目创建时绑定本地仓库（`Project.RepoPath` + 规范化后的 `Project.RepoRoot`），
    agent 的工作目录落在哪个项目绑定的仓库范围内，就绑到哪个项目。
    用**包含关系**而不是字符串相等 —— agent 在仓库子目录里干活也算同一个项目。
    反查时取**最具体**的那个（路径最长）；一样长就是两个项目绑了同一个仓库，
    **分不出来就不绑**，让调用方显式指定，别猜。
    `RepoPath` 存人填的路径（绝对化），`RepoRoot` 存规范化的绑定键（解符号链接）——
    macOS 上 `/tmp` 其实是 `/private/tmp`，不改写用户填的路径，但绑定键必须规范化。
    **同一目录绑两个项目是允许的**（前后端同仓就各绑各的子目录，反查按最具体的那个分开），
    但**完全相同**的目录会让「agent 按目录认项目」分不出来 ——
    这时要在绑定的那一刻提醒人（`ProjectConfigDto.RepoBindingWarning`），
    别等 agent 撞了「同时落在多个项目绑定的仓库范围内」才发现。

26. **会话归属要跟显式参数对账**：
    工具的 `projectId` 变成可选（会话绑了就省掉），但**显式传了 projectId
    就要跟会话比**：不一致直接拒。否则 agent 拿着 A 项目的会话能改 B 项目，
    审计里的归属就成了 agent 自己说了算。
    认领（`claim`）成功后把任务挂到会话上，之后的调用自动带 taskId。

27. **产出文件靠采集，不靠 agent 自报**：
    采集点只有两个，都在 agent 绕不过去的地方 ——
    ① `git_commit` 审批通过、真正提交之后；② 推进到 `Review` 时快照工作区。
    `workbench_add_artifact` 只登记非代码产物，代码文件不靠它。

28. **审计用哈希链串起来**：
    每条工具审计带 `prevHash` + `hash`（内容 + 上一条的哈希）。
    改一条内容、删一条记录，链都会断，「审计」页面上点「验哈希链」能验出断点。
    序号用 `Sequence`（单调递增）定序，**不能靠时间戳** —— 同一时钟刻里写的记录
    时间戳一样，按时间排序先后就不确定，链会误报断裂。
    这是「可发现」不是「不可篡改」：能连哈希一起重算的人照样骗得过。

29. **别把接口名或方法名撞上框架约定**：
    ASP.NET Core 的 minimal API 会把服务类型上的 `BindAsync` 方法当成自定义绑定器
    （`ISessionService` 就这么炸过）；DI 也认 `ISession*` 这类命名约定。
    服务接口叫 `IAgentSessionService`、方法叫 `BindRepoAsync` —— 名字撞约定时
    报出来的错很难指向真因，改名比加特判省事。

30. **界面上不许露代码标识**：
    用户看到的是「提交代码」「开发中」「文档就绪」，不是 `git_commit`、`InDev`。
    中文说法统一走 `UiText`（`src/Workbench.Application/Contracts/UiText.cs`），
    **别在页面里各写各的映射**。GUID 同样不许出现在界面上 ——
    任务给编号（`REQ-001`），项目给名字，给不出名字就写「未命名」。
    两个例外：`/connect` 与 `/mcp-tools` 这两页是给开发者按名字调 agent 用的，
    原始工具名可以出现在那里，但要用中文说法兜底（名字在次要位置或折叠区）。
    页面区块别一铺十几块 —— 任务详情用**精简视图**（`wb-compact`）把次要区块收起来。

31. **列表接口必须有上限**：
    审批、看板、审计这些列表要一次拉进浏览器的，全都要限量 + 客户端翻页 ——
    用久了每个项目几千条任务、每次审批留一条记录，不设上限页面直接卡死。
    `ApprovalService.ListAsync` 的 `limit` 放在 `CancellationToken` 之后（不破坏位置参数调用），
    看板用 `Tasks.ListAsync(limit:)`。单测 `ListLimitTests` 钉住这条。
    **翻页与自动刷新要分开**：定时刷新不能把页码拨回第一页（`RefreshAsync(resetPage:)`），
    只有换筛选条件才重置。

32. **跨页链接要能定位到具体那一条**：
    「去处理」「已审批」这类入口**别把人丢到一个要自己翻的列表页**。
    深链带 id（`/approvals?id=xxx`）或带筛选词（`/db-calls?sql=xxx`），
    落地页负责切页签、翻到那一页、描边高亮、滚进视野。
    目标不在当前筛选里（已被批过 → 在历史页签下）就先切页签，
    否则人点过来只看到一片空白，反而以为坏了。
    审批 DTO 要带**任务编号与项目名**（`ApprovalDto.TaskKey/TaskTitle/ProjectName`）——
    人判断该不该批的依据是「这是哪个需求的活」，界面上又不许露 GUID。

33. **读数方向不能反**：
    「已过多久」和「还剩多久」是**两个数**，别拿一个充另一个。
    以前审批卡上写「等待 N 分钟」，算的却是 `ExpiresAt - now`（剩余有效期）——
    刚发起的显示「等待 30 分钟」，快过期的显示「等待 0 分钟」，跟字面意思正好相反。
    现在写成「已等 X · 剩余 Y」，两个数都摆出来，快过期的染红。
    相对时间（「3 天前」）后面要跟 `title` 给绝对时间。

34. **审计链头归档必须落在工作台数据目录之外**：
    库里的哈希链是应用层算的 —— 拿到库文件的人**连哈希一起重算**，链照样能对上。
    链头归档到工作台够不着的地方（`Workbench:AuditArchivePath`），对账才能发现
    「记录被删过 / 库被回滚过 / 同一条被改过」。
    **归档路径落在 `WORKBENCH_HOME` 里面要拒掉** —— 那种归档等于没归档，
    拿到库文件的人连归档文件一起改就白搭。
    归档位置本身能不能被同样的人改，是**部署方的责任**（只读挂载、另一台机器、
    另一个权限域），别在工作台里假装能挡住。
    链头没变时不要重复追加 —— 定时任务每 10 分钟跑一次，
    一次没写入就等于把归档文件灌成流水账。

## 常用命令

```bash
./tests/verify.sh                               # 一键验收：编译 → 单测 → 端到端，给总账
./.dotnet/dotnet build Workbench.sln     # 用仓库里隔离的 .NET 8 SDK
./.dotnet/dotnet test Workbench.sln
dotnet run --project src/Workbench.Web          # 起服务（UI + REST + /mcp）
dotnet run --project src/Workbench.Cli -- --help # CLI

./tests/e2e/run.sh                              # 端到端验证（自起临时服务，跑完即收）
```

CI（`.github/workflows/verify.yml`）每次推送与 PR 都跑 `verify.sh`。
仓库里没有 `.dotnet/` 时脚本会退回用 PATH 上的 dotnet（CI 里版本固定在 8.0.x）——
**别把 SDK 重新塞回仓库**。

端到端有**七条链路**，都在 `tests/e2e/` 下，`run.sh` 一次跑完：

| 链路 | 脚本 | 验什么 |
|---|---|---|
| A/B/C | `mcp_e2e.py` | MCP 主链路（按编号找任务 → claim → 上下文 → 回写计划 →推进到 Review → 开评审轮 → 取评审包 → 逐维度回写 → 结项）、评审拦截（只评 6 个维度 / 有一个维度不通过）、类型编号与分支守卫 |
| D | `approval_e2e.py` | **写操作的人工审批门禁**：白名单外的直接拒 / 没批准不执行 / 批准后续跑 / 一条审批只能执行一次 / 换命令换项目都用不了 / 撤销是后悔药 / 只读通道不许写 / db_execute 也要审批 / 全程审计留痕 |
| E | `cli_smoke.py` | CLI 常用命令、敏感命令没凭证是 401、`wb mcp` 的 stdio 桥接真能转发 JSON-RPC |
| F | `session_e2e.py` | **会话绑定链路**：项目绑仓库 → 不传 projectId 也能干活 → 跨项目被拒 → 按编号认领 → 审计自动带任务 → 提交后产出文件自动落库 |
| G | `restart_e2e.py` | **重启对账**：真起服务 → 执行途中杀进程 → 同一个 WORKBENCH_HOME 再起一次 → 那次写操作标成「结果未知」交人工确认 |

改完 MCP 工具、状态机**或审批闸门**后跑一遍，比只看单测靠谱。
新增写工具时，`approval_e2e.py` 里要跟着加一条「不批就不执行」的断言。

企业验收看 `docs/ENTERPRISE.md`（逐条能力 → 落地位置 → 自动化证据 + 诚实的缺口清单）。

数据目录由 `WORKBENCH_HOME` 决定（默认 `~/.workbench`），里面是 SQLite、文档原件、密钥文件、
外部 agent 的运行日志（`runs/`）、人工凭证（`.human-token`）与定时备份（`backups/`）。
换机用 `wb workspace export` / `wb workspace import`（导出带库+文档+密钥+运行日志）。

审计链头的归档**不在**这里 —— 配 `Workbench:AuditArchivePath` 指向工作台数据目录之外的
文件（或目录）。不配就不归档（规矩 34）。

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
| `src/Workbench.Application` | 用例服务、DTO、事件、审批编排、评审工作流、任务依赖/分派/导出 |
| `src/Workbench.Documents` | 文档解析引擎（MD/DOCX/PDF/diff → 统一模型） |
| `src/Workbench.Infrastructure` | EF Core/SQLite、schema 升级、文档存储、Git 读写、SQL Server 执行器、工作区搬迁 |
| `src/Workbench.Mcp` | MCP 工具集 |
| `src/Workbench.Web` | Blazor UI + REST API + /mcp 端点（宿主进程）、人工凭证、定时备份 |
| `src/Workbench.Cli` | `wb` 命令行 + stdio↔HTTP MCP 桥接 |

## 文档语义别弄丢

文档解析的重点是保留语义：

- **删除线**（Markdown `~~x~~`、DOCX `w:strike`）→ 语义是「已废弃」，agent 不能当需求读
- **修订**（DOCX `w:ins`/`w:del`/移动标记、diff 的 +/- 行）→ 带作者与时间
- PDF 没有语义级的删除线/修订，别假装能解析出来
