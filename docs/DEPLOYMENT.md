# 部署手册（团队形态）

> 这是一份**能照着做**的手册。观复的形态是：
> **一台服务器上的一个部署 = 一个团队**。
>
> 人与 agent 都在**各自本地**干活，服务器上跑的是**过程与记录中枢**：
> 看板、审批、审计、归档、交付。

---

## 一、部署形态

```
   人的浏览器 ─┐
                ├─ 反代(TLS) ─ 观复(127.0.0.1:5177) ─ SQLite + 文档 + 交付包
 agent 客户端 ─┘                   ▲
   (MCP/HTTP)                      │
                        人工凭证 / 成员会话 / agent 凭证
```

**观复不碰代码** —— agent 用自己的客户端工具改代码、改库、跑命令。
观复这边只发生三件事：

| 事 | 在哪发生 | 说明 |
|---|---|---|
| 过程记录 | 观复 | 任务、状态、计划、产出、文档、流水、审计 |
| 审批 | 观复 | 危险的操作（主要是数据库写）要人点头 |
| 归档与交付 | 观复 | 文档 + 产物 → 打包 → 分享链接 |

所以服务器上**不需要有仓库、不需要有 git、不需要装 agent** —— 一台内网小机器就够。

> 曾经观复自带「产出采集 / 分支守卫 / 工位」这类能力，它们的前提是「代码在观复旁边」。
> 现在**全部作废**（见 `docs/REFACTOR.md` §4）：代码是 agent 的事，观复不猜。

---

## 二、装

### 2.1 拿发布包

```bash
./scripts/publish.sh linux-x64        # 在有源码的机器上打
# 产物：dist/guanfu-linux-x64-<日期>.tar.gz
```

包里两个自包含二进制（**不需要装 .NET**）：

```
bin/guanfu-server/   服务端（界面 + REST + /mcp）
bin/guanfu-cli/      命令行与 stdio 桥接
bin/guanfu           命令行入口（转发给上面那个）
deploy/              systemd 单元 / nginx 配置 / 环境变量示例
README.md            交付说明
```

### 2.2 放上去

```bash
sudo mkdir -p /opt/guanfu
sudo tar -xzf guanfu-linux-x64-*.tar.gz -C /opt/guanfu --strip-components=1
sudo mkdir -p /var/lib/guanfu
sudo useradd -r -d /var/lib/guanfu -s /usr/sbin/nologin guanfu || true
sudo chown -R guanfu:guanfu /var/lib/guanfu
```

`/var/lib/guanfu` 是**数据目录**（`GUANFU_HOME`）：库、文档原件、交付包、密钥、
运行日志、备份都在这儿。**换机就搬它**。

### 2.3 常驻运行

```bash
sudo cp /opt/guanfu/deploy/guanfu.service /etc/systemd/system/
sudo mkdir -p /etc/guanfu
sudo cp /opt/guanfu/deploy/env.example /etc/guanfu/env
sudo vi /etc/guanfu/env                    # 改这两行 ↓
sudo chmod 600 /etc/guanfu/env
sudo systemctl daemon-reload
sudo systemctl enable --now guanfu
journalctl -u guanfu -f
```

必须改的两行：

```ini
GUANFU_HOME=/var/lib/guanfu
Guanfu__AllowedHosts=guanfu.corp.internal          # 反代用的域名
```

> **人工凭证**（owner 的钥匙）在启动横幅里打印，落在 `/var/lib/guanfu/.human-token`。
> 丢了用 `guanfu token` 找回（同一个，重启不变）。

---

## 三、给内网用（反向代理）

观复默认只听 `127.0.0.1`。对外暴露交给反代 —— TLS、证书、WebSocket 都归它。

```bash
sudo cp /opt/guanfu/deploy/nginx.conf /etc/nginx/conf.d/guanfu.conf
sudo vi /etc/nginx/conf.d/guanfu.conf       # 改 server_name 与证书路径
sudo nginx -t && sudo systemctl reload nginx
```

**三处必须对上**：

| 位置 | 要是什么 | 不对会怎样 |
|---|---|---|
| 反代 `server_name` | 团队访问的域名 | 访问不到 |
| `Guanfu__AllowedHosts` | 同一个域名（可带端口） | **Host 头被拒**（跨站与 DNS rebinding 的防线） |
| 反代 `proxy_set_header Host $host` | 保留原 Host | 同上 |

MCP 走的是 **WebSocket / 长连接**，反代**必须放行 Upgrade**（模板里已经带上了）。
`proxy_read_timeout` 默认 60 秒会把长连接掐断 —— 模板里调到了 3600 秒。

### 审计链头归档（强烈建议配）

```ini
Guanfu__AuditArchivePath=/var/lib/guanfu-audit/chain-head.txt
```

**必须在数据目录之外** —— 归档到数据目录里等于没归档（拿到库文件的人连归档一起改）。
这个路径是「库被回滚 / 记录被删」的对照物，单独备份。

---

## 四、身份怎么配

一个部署 = 一个团队。

| 谁 | 怎么进 | 在哪建 |
|---|---|---|
| **owner**（部署的人） | 人工凭证（启动横幅里那把） | 自动生成 |
| **成员**（人） | 账号 + 密码 → 成员会话 | 「成员」页签（owner 用人工凭证进） |
| **agent** | 凭证（`X-Guanfu-Token`） | 「访问凭证」页签或 `POST /api/credentials` |

要点：

- **凭证要绑成员** —— 签发时选「归属成员」。这样审计里才有「谁的人」，
  而且**停用成员 → 名下钥匙立刻作废**。
- 第一个成员建成**管理员**：管理员能分配任务给别人、能代批（代批要写理由）、看得到全部。
- 普通成员**只看得到自己的东西**（分派给自己的 + 自己认领的 + 还没分派的）。

---

## 五、备份

| 什么 | 在哪 | 怎么备 |
|---|---|---|
| 库 + 文档 + 密钥 + 运行日志 | 数据目录 | `guanfu workspace export <目录>` |
| 审计链头 | `Guanfu__AuditArchivePath` | 单独备 |
| 配置 | `/etc/guanfu/env` | 手动 |

观复自己也会定时备份数据库（写在数据目录的 `backups/`），但那只是兜底 ——
**换机前请显式 export 一次**。

导出必须走服务（库要用 SQLite 的 `VACUUM INTO` 做一致性快照，边跑边复制会拿到撕裂的页）。

---

## 六、升级

```bash
# 1. 备份
guanfu workspace export /tmp/guanfu-backup-$(date +%Y%m%d)

# 2. 停
sudo systemctl stop guanfu

# 3. 换二进制（数据目录不动）
sudo tar -xzf guanfu-linux-x64-<新>.tar.gz -C /opt/guanfu --strip-components=1

# 4. 起
sudo systemctl start guanfu
journalctl -u guanfu -f
```

**库结构自动升级**：观复启动时按模型补齐缺的表与列（`SchemaUpgrader`），
不用手动跑迁移，也不会写手写清单。

回滚：把旧二进制放回去 + 从备份 import（注意：备份 import 会覆盖库，
先确认你要回退到哪个时间点）。

---

## 七、排障

| 现象 | 多半是 | 怎么办 |
|---|---|---|
| 打开页面提示要人工凭证 | 远程访问 | 走反代；本机直接开就自动认 |
| 反代后 400 / 打不开 | Host 头不在 `AllowedHosts` | 加上域名（可带端口） |
| agent 连不上 `/mcp` | 反代没放行 WebSocket；或没带 `X-Guanfu-Token` | 查 nginx 配置与凭证 |
| 看不到别人的活 | 你是普通成员 | 这是隔离规则；管理员看得到全部 |
| 审批批不了别人的单子 | 同上 | 管理员可代批（要写理由） |
| 审计里没有「谁的人」 | 凭证没绑成员 | 重签一把并绑定 |
| 验哈希链报断裂 | 审计链头没归档，或数据目录被人动过 | 查归档文件对账 |
| 交付包下载不了 | 分享链接过期（默认 7 天）或被撤销 | 重新签发 |
| 库报「database is locked」 | 备份任务在跑 | 等一会儿；定时备份在数据目录 `backups/` |

日志：`journalctl -u guanfu -f`；应用自己的日志在数据目录的 `runs/`。

---

## 八、这套形态的边界（诚实清单）

- **agent 走 MCP 时看到的是全局看板** —— 它要看得见整个项目才能干活。
  个人数据隔离只对**人**生效。要连 agent 一起隔离，得给凭证再加一道过滤。
- **产出是 agent 自报的** —— 观复不数「改了哪些文件」，所以「这个任务改了哪些代码」
  在观复里查不到（那是 agent 自己客户端工具的事）。
- **审计链头归档的可靠性归部署方** —— 观复挡不住「拿到归档文件的人一起改」。
- **一个部署 = 一个团队** —— 多团队（多租户）还没做，商业化时补。
