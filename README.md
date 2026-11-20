# 观复 Guanfu

[![验收](https://github.com/Nyc-Hy/guanfu/actions/workflows/verify.yml/badge.svg?branch=main)](https://github.com/Nyc-Hy/guanfu/actions/workflows/verify.yml)

**面向开发 agent 的自托管项目管理工具。** 团队在 Web 上管任务、看进度和验收版本，开发 agent 通过 MCP 领任务、取需求与规约、回写测试和评审。交付时，发布负责人可以按版本导出离线 HTML 报告和包含 SQL 的发版工具包。

| 入口 | 用法 |
| --- | --- |
| **Web** | 分配任务、管理成员、核对评审证据、审批和验收版本。 |
| **MCP** | 给 Codex 等开发 agent 读写任务、计划、测试和评审记录。支持 Streamable HTTP。 |
| **CLI** | 在开发机或 CI 上传本地文件、导出报告和下载交付包，也可以给 stdio 客户端做桥接。 |

观复用 ASP.NET Core、Blazor 和 SQLite 提供共享服务。代码读写、Git、测试执行和评审判断由开发机上的 agent 完成。观复保留结果、证据和操作人的身份。

## 快速开始

本机从源码体验需要 **.NET 8 SDK** 和 Git。

```bash
git clone https://github.com/Nyc-Hy/guanfu.git
cd guanfu
dotnet run --project src/Guanfu.Web --no-launch-profile -- --urls http://127.0.0.1:5177
```

跟着上手清单建项目和第一个管理员账号。到「访问凭证」为 agent 签发凭证，选择归属成员和授权项目。最后建一个迭代版本和任务。 [打开本机上手清单](http://127.0.0.1:5177/start)

团队部署可以用 nginx 和 systemd 模板。Linux 或 macOS 可以从源码构建自包含包，运行包里的 `bin/guanfu serve`。目标机器无需安装 .NET。

```bash
./scripts/publish.sh linux-x64
```

产物是 `dist/guanfu-linux-x64-YYYYMMDD.tar.gz`。脚本也支持 `linux-arm64`、`osx-arm64` 和 `osx-x64`。内网 HTTPS、首位管理员、反代和备份步骤见 [部署手册](docs/DEPLOYMENT.md)。

## 接入开发 agent

在观复的 `/connect` 页面复制连接信息。HTTP 客户端连接 `http://127.0.0.1:5177/mcp`，用 `X-Guanfu-Token` 传入该 agent 的凭证。团队部署时换成观复服务器的 HTTPS 地址。

只支持 stdio 的客户端可以用 `guanfu mcp --url <观复地址>` 启动桥接，通过环境变量 `GUANFU_TOKEN` 提供凭证。需要让 `guanfu` 在客户端的 `PATH` 中可用。

### 从领任务到提交评审

1. 用 `guanfu_get_task_by_key` 找任务，用 `guanfu_claim_task` 领取，再用 `guanfu_get_context` 读取需求、依赖和验收标准。
2. 用 `guanfu_set_plan` 回写计划。在本机开发和跑测试，用 `guanfu_report_test_result` 记录结果。
3. 用 `guanfu_get_review_pack` 取评审上下文，用 `guanfu_submit_review` 和 `guanfu_record_review_evidence` 提交结论及证据。
4. 用 `guanfu_export_review` 导出 HTML、Markdown 或 JSON。正式结项和版本验收会检查相应门禁。

成员账号决定人的可见范围，agent 凭证可以限定项目。绑定的成员被停用后，名下的凭证随之失效。配置示例和权限说明见 [使用手册](docs/USAGE.md) 和 [团队部署](docs/DEPLOYMENT.md#四身份怎么配)。

## 使用 CLI

CLI 联机前需要显式指定服务地址。可以保存默认地址，也可以用 `--url` 或 `GUANFU_URL`。成员会话按地址隔离。

下面在解压后的目录中使用 `bin/guanfu`，以 `admin` 为登录名。

```bash
./bin/guanfu configure --url http://127.0.0.1:5177
./bin/guanfu login admin
./bin/guanfu project list
```

源码目录也可以直接运行 CLI。

```bash
dotnet run --project src/Guanfu.Cli -- --url http://127.0.0.1:5177 project list
```

上传本地文件时可用 `guanfu report artifact <任务ID> --file <文件>`。MCP 调用使用 `GUANFU_TOKEN`，需要 owner 人工凭证的 REST 操作可以用 `GUANFU_HUMAN_TOKEN`。 [命令行速查](docs/USAGE.md#12-cliguanfu速查)

## 从任务到发版

- **计划与进度**。按项目、版本和需求组织任务，查看依赖、可开工项、认领租约和交接记录。状态流转由服务端校验。
- **评审与验收**。按维度认领评审，记录 PRD 对照、按需求拆分的 diff、性能与兼容性、测试和复验结果。每轮评审可导出 HTML。已验收版本锁定任务范围。
- **权限与审批**。数据库操作按表级规则处理，写入可要求人工批准。批准与项目和具体操作绑定，工具调用留审计。
- **文档与产物**。上传需求、用例、报告和 SQL，管理产物修订版本。交付包可重复下载，也可以签发有期限的分享链接。

进入「迭代版本 → 打包交付」，勾选「生成发版工具包」。ZIP 中的 `index.html` 汇总版本任务、需求完成度、评审和测试，打开后可以离线查看各任务的详细报告。选中的 SQL 按确认的上线、核验和回滚顺序归档。`manifest.json` 记录评审轮次、产物版本和文件校验值。

```bash
guanfu delivery candidates "v1.4.0"
guanfu delivery pack "v1.4.0" --all --release-plan release-plan.json --output v1.4.0.zip
```

包中有 SQL 时，发布计划填写脚本条目、阶段和目标数据库。所选产物无需 SQL 计划时，可以用 `--release`。JSON 字段和操作步骤见 [发版工具包](docs/USAGE.md#发版工具包)。

## 部署与开发

运行数据保存在 `GUANFU_HOME`，默认使用 `~/.guanfu`。数据库、文档、历史交付包和加密密钥随工作区备份保存。部署和迁移步骤见 [部署手册](docs/DEPLOYMENT.md)。

应用层负责业务规则，Web 宿主管理 SQLite，MCP 和 CLI 复用服务端用例。 [源码](src/) · [测试](tests/) · [部署模板](deploy/)

```bash
dotnet build Guanfu.sln
dotnet test Guanfu.sln
./tests/verify.sh
```

一键验收需要 Bash、Python 3、Git 和 .NET 8 SDK。它会启动临时服务，验证 MCP 流程、审批门禁、CLI、团队隔离及备份恢复。产品设计与技术取舍见 [设计文档](docs/REFACTOR.md)。

欢迎通过 [Issues](https://github.com/Nyc-Hy/guanfu/issues) 报告问题，附上运行环境和复现步骤。

## 许可证

[MIT](LICENSE)。观复之名取自《老子》「万物并作，吾以观复」。
