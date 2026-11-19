# 观复（Guanfu）

> **这是一个给 agent 用的项目管理工具。** 第一公民是 agent，人也要用，但人不是唯一用户。
>
> 名字取自《老子》「万物并作，吾以观复」—— 事情一轮轮转回来，观复看着它们转。

**观复只管过程与记录**：做到哪一步了、谁做的、哪些操作要人点头、产出的文档与脚本怎么归档与交付。

它**不提供 AI 服务**（不调模型、不跑 agent），也**不碰代码** —— 代码是 agent 用自己的客户端工具改的，
观复不去数「这个任务改了哪些文件」。

三样齐了，agent 才真的能当项目管理的执行者：
**没人写的状态机叫流程图，没人签的字叫摆设，没人能验的账叫日志。**

---

## 快速开始

### 装（服务器上）

发布包解开就能跑，**不需要装 .NET**：

```bash
tar -xzf guanfu-linux-x64-20261119.tar.gz
cd guanfu-linux-x64-20261119
./bin/guanfu-server                 # 服务端：界面 + REST + /mcp
```

启动横幅里会打印**人工 token**（owner 的万能钥匙，重启不变，丢了用 `guanfu token` 找回）。

内网部署、反代、备份、升级：**[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** 与包里的 `README.md`。

### 从源码跑

```bash
git clone <repo-url> guanfu && cd guanfu
dotnet run --project src/Guanfu.Web
```

打开 <http://127.0.0.1:5177>，MCP 端点在 <http://127.0.0.1:5177/mcp>。

### 上手六步

界面上有 **[上手清单](/start)**，照着走：

1. **建一个项目** —— 看板的容器
2. **建一个成员（管理员）** —— 团队部署时「谁在操作」必须是真的
3. **给 agent 发一把凭证** —— 身份在签发时就绑上
4. **把接入配置给 agent** —— HTTP 或 stdio 桥接
5. **建一个迭代版本，挂上第一个任务** —— 版本 → 需求组 → 任务
6. **跑通第一个活，打个包发出去** —— zip + 清单 + 分享链接

---

## 三条主线

### 一、进度：任务、迭代版本、依赖、状态机

```
项目 → 迭代版本（release）→ 需求组（group）→ 任务
```

任务类型决定编号前缀（`REQ-` / `BUG-` / `OPT-`），各类型独立计数，**改类型不改编号**。

状态机是唯一的真相：

```
草稿 → 文档就绪 → 已计划 → 开发中 → 测试中 → 评审中 → 已完成
                                        ↑___________（打回重做）
```

非法迁移的报错里**带当前允许的迁移列表** —— agent 靠它自我纠正，不用人来翻译。

还有：三层规约（工作区 → 项目 → 迭代版本）、评审的 7 个维度与逐条判定 DoD、
交接包与交接链、调度板（「现在什么能干」「先干哪个」并带理由）。

### 二、留痕与审批：让人敢信这个看板

- **审计**：每次工具调用都留一笔（谁调的、哪个项目、哪个任务、结果如何、花了多久）；
  带**哈希链**，验链能查出「有人直接改了库文件」；链头归档到数据目录之外。
- **审批**：危险的操作（主要是数据库写操作）要人点头。批准绑在**具体操作**上
  （工具 + 参数指纹 + 项目），批一次只能用一次；换条 SQL 就不算数。
- **身份**：成员用账号密码登录，agent 用凭证；凭证归属成员，**停用成员 → 名下钥匙立刻作废**。
  审计里有一列「谁的人」—— agent 自报的名字不算数。

### 三、归档与交付：过程记下来的东西能交出去

- **两套都留**：文档（需求 / 用例 / 接口 / 交付文件）与产物（脚本 / 报告 / SQL）。
- **产物能改**：AI 写的脚本能跑但不一定合乎生产口径 —— 人改是常态，
  改一次多一版，**旧版永远留着**。
- **文档看的是渲染结果**：不把原始 md 摊给用户。
- **打包交付**：按迭代版本圈候选池（该版本下任务的文档 + 产物）→ 勾选 → 打成
  **一个 zip + 一份清单**（`清单.md` 给人看、`manifest.json` 给机器读）；
  打完的包留在账上，可再下载。
- **分享链接**：项管没有观复账号 —— 一条带时效的链接（默认 7 天，可撤销）就能下载。

---

## 接入 agent

两种接法，页面上都有现成配置（`/connect`）：

**HTTP**（Claude Code / Codex 等）：

```json
{
  "mcpServers": {
    "guanfu": {
      "type": "http",
      "url": "http://127.0.0.1:5177/mcp",
      "headers": { "X-Guanfu-Token": "<凭证>" }
    }
  }
}
```

**stdio**（只支持 stdio 的客户端）：把 `guanfu` 放进 PATH

```json
{
  "mcpServers": {
    "guanfu": {
      "command": "guanfu",
      "args": ["mcp", "--url", "http://127.0.0.1:5177"]
    }
  }
}
```

> `guanfu mcp` 桥接是 CLI 唯一不可替代的能力 —— 只支持 stdio 的 MCP 客户端全靠它接进来。
> 改这个桥接时要格外小心（stdout 只能输出 JSON-RPC，任何多余输出都会污染协议流）。

agent 的典型流程：

```
guanfu_get_task_by_key   按编号找任务（人嘴里的 req001 也认）
guanfu_claim_task        领任务（带租约）
guanfu_get_context       取全量上下文（文档、计划、产出、事件、当前允许的状态迁移）
guanfu_set_plan          回写需求理解与开发计划
guanfu_add_artifact      交产出（脚本、报告）
guanfu_update_status     推进状态（过状态机）
guanfu_submit_review     逐维度回写评审结论
```

工具开关在 `/mcp-tools`：关掉的工具**不出现在 `tools/list`**，客户端缓存了硬调也会被拒。

---

## 命令行

```bash
guanfu serve                              # 启动 Web（UI + REST + MCP）
guanfu mcp --url http://127.0.0.1:5177    # stdio ↔ HTTP 桥接
guanfu project list                       # 列项目
guanfu task list <项目ID>
guanfu task claim <任务ID> --agent agent-a
guanfu approval list                      # 待批与历史
guanfu audit verify                       # 验哈希链
guanfu workspace export /tmp/guanfu-backup   # 备份（库 + 文档 + 密钥 + 日志）
guanfu token                              # 打印人工凭证
```

敏感命令（改配置、裁决审批、导出工作区）没凭证就是 401。

---

## 目录结构

```
Guanfu.sln
├─ src/
│  ├─ Guanfu.Domain          实体、任务状态机、规则
│  ├─ Guanfu.Application     用例服务、DTO、事件、审批编排
│  ├─ Guanfu.Documents       文档解析引擎（MD/DOCX/PDF/diff → 统一模型）
│  ├─ Guanfu.Infrastructure  EF Core/SQLite、文档存储、Git 读写、SQL Server 执行器
│  ├─ Guanfu.Mcp             MCP 工具集 + 注册
│  ├─ Guanfu.Web             Blazor UI + REST API + /mcp 端点（宿主进程）
│  └─ Guanfu.Cli             guanfu 命令行 + stdio↔HTTP MCP 桥接
├─ tests/
│  ├─ Guanfu.Tests           单元与契约测试
│  └─ e2e/                   七条端到端链路
├─ scripts/publish.sh        打出可交付的发布包
└─ deploy/                   systemd / nginx / 环境变量模板
```

---

## 数据与迁移

观复数据都在 `GUANFU_HOME`（默认 `~/.guanfu`；旧的环境变量 `WORKBENCH_HOME` 与旧目录
`~/.workbench` 仍然认）：

```
~/.guanfu/
├─ guanfu.db             SQLite（老的 workbench.db 也照样用）
├─ documents/            上传的文档原件
├─ packages/             交付包（zip）
├─ secret.key            数据库密码的加密密钥（AES-GCM，0600）
├─ .human-token          人工凭证（owner 的钥匙）
├─ runs/                 外部 agent 的运行日志
└─ backups/              定时备份
```

换电脑：

```bash
guanfu workspace export /tmp/guanfu-backup
# 把整个目录拷到新机器
guanfu workspace import /tmp/guanfu-backup --home ~/.guanfu
```

**库结构的升级是自动的** —— 观复启动时按模型补齐缺的表与列，不用手动跑迁移。

---

## 开发

```bash
./tests/verify.sh              # 一键验收：编译 → 单测 → 端到端，给总账
dotnet build Guanfu.sln
dotnet test Guanfu.sln
./tests/e2e/run.sh              # 端到端：自起临时服务，跑完即收摊
./scripts/publish.sh            # 打出可交付的发布包
```

端到端有七条链路（`tests/e2e/`）：

- **A/B/C**（`mcp_e2e.py`）—— 模拟 agent 走完「按编号找任务 → claim → 取上下文 →
  回写计划 → 推进到 Review → 开评审轮 → 取评审包 → 逐维度回写 → 结项」，
  外加两条拦截链路（只评 6 个维度就结项、有一个维度不通过就结项，**两种都必须被拒**）。
- **D**（`approval_e2e.py`）—— **写操作的人工审批门禁**：白名单外的直接拒、
  没批准不执行、批准后带 `approvalId` 续跑、一条审批只能执行一次、
  换个命令或换个项目都用不了、撤销是后悔药、只读通道不许写、全程审计留痕。
- **E**（`cli_smoke.py`）—— CLI 常用命令、敏感命令没凭证是 401、
  `guanfu mcp` 的 stdio 桥接真能转发 JSON-RPC。
- **F**（`session_e2e.py`）—— **会话绑定链路**：绑定项目 → 不传 `projectId` 也能干活 →
  跨项目被拒 → 按编号认领 → 审计自动带任务。
- **G**（`restart_e2e.py`）—— **重启对账**：执行途中杀进程 → 同一个数据目录再起 →
  那次写操作标成「结果未知」交人工确认。

改完 MCP 工具、状态机**或审批闸门**后跑一遍。

**企业验收**看 [`docs/ENTERPRISE.md`](docs/ENTERPRISE.md)——
逐条能力 → 落地位置 → 自动化证据，外加一份诚实的缺口清单。

**产品定位与取舍**看 [`docs/REFACTOR.md`](docs/REFACTOR.md)（观复是什么、不管什么）、
[`docs/PLAN.md`](docs/PLAN.md)（每一步做了什么、为什么）。

## License

MIT
