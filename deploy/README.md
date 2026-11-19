# 观复 — 交付说明

给部署的人（不是给 agent 看的）。

观复是**给 agent 用的项目管理工具**：任务、迭代版本、依赖、状态机是内核，
MCP 工具把这些原语喂给 agent，审计与审批是让人敢信这个看板的底座。

**观复不提供 AI 服务** —— 它不调模型、不跑 agent，只做**过程记录 + 审批 + 归档**。
代码是 agent 用自己的客户端工具改的，观复不碰。

---

## 一、装

包解开就有两个自包含二进制，**不需要装 .NET**：

```
bin/guanfu-server/   服务端（界面 + REST + /mcp）
bin/guanfu-cli/      命令行与 stdio 桥接
bin/guanfu           命令行的入口（转发给上面那个）
```

放到服务器上：

```bash
sudo mkdir -p /opt/guanfu
sudo cp -r bin /opt/guanfu/
sudo mkdir -p /var/lib/guanfu
sudo useradd -r -d /var/lib/guanfu -s /usr/sbin/nologin guanfu || true
sudo chown -R guanfu:guanfu /var/lib/guanfu
```

> `/var/lib/guanfu` 是**数据目录**：库、文档原件、密钥、交付包、运行日志都在这儿。
> 换机就搬它（或用 `guanfu workspace export` 打包带走）。

## 二、跑

```bash
cd /opt/guanfu
./bin/guanfu-server
```

启动后终端会打印：

```
===================== 观复已启动 =====================
  监听地址：http://127.0.0.1:5177
  人工 token：xxxxxxxx
    请求头：X-Guanfu-Human-Token: xxxxxxxx
    或 Cookie：guanfu_human_token=xxxxxxxx
======================================================
```

**那把人工 token 是 owner 的万能钥匙** —— 留着，别丢：丢了用
`guanfu token` 找回（同一个 token，重启不变）。

第一次打开 <http://127.0.0.1:5177> 会有一个**上手清单**（`/start`），
按着走一遍：建项目 → 建成员 → 发一把 agent 凭证 → 接上 agent → 跑第一个活。

### 常驻运行（systemd）

```bash
sudo cp deploy/guanfu.service /etc/systemd/system/
sudo mkdir -p /etc/guanfu
sudo cp deploy/env.example /etc/guanfu/env   # 按需改
sudo chmod 600 /etc/guanfu/env
sudo systemctl daemon-reload
sudo systemctl enable --now guanfu
journalctl -u guanfu -f
```

### 给内网用

观复默认只听本机。对外暴露要走反向代理（TLS 与 WebSocket 都得转）：

```bash
sudo cp deploy/nginx.conf /etc/nginx/conf.d/guanfu.conf
sudo nginx -t && sudo systemctl reload nginx
```

**两处必须改**：

1. 反代里的 `Host` 头要出现在 `Guanfu__AllowedHosts` 里 ——
   不在就拒（跨站与 DNS rebinding 的防线）。
2. `Guanfu__AuditArchivePath` 指到**数据目录之外** ——
   归档到数据目录里等于没归档。

## 三、接 agent

两种接法：

**HTTP**（Claude Code / Codex 等）：在观复的「接入 agent」页复制配置片段，
把 `<凭证>` 换成 `POST /api/credentials` 签发出来的那把（绑到成员名下的）。
客户端在请求头 `X-Guanfu-Token` 里带上它。

**stdio**（只支持 stdio 的客户端）：把 `guanfu` 放进 PATH，配置：

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

## 四、备份

三样东西要留：

| 什么 | 在哪 | 怎么备 |
|---|---|---|
| 数据库与文档 | 数据目录 | `guanfu workspace export <目录>`（库 + 文档 + 密钥 + 运行日志打成一份） |
| 审计链头 | `Guanfu__AuditArchivePath` 指向的文件 | 这个**必须在数据目录之外**，单独备份 |
| 配置 | `/etc/guanfu/env` | 手动留一份 |

观复自己也会定时备份数据库（写在数据目录的 `backups/`）。

## 五、升级

```bash
# 1. 备份
./bin/guanfu workspace export /tmp/guanfu-backup-$(date +%Y%m%d)

# 2. 停
sudo systemctl stop guanfu

# 3. 换二进制（数据目录不动 —— 库结构由观复自己升级）
sudo cp -r 新包/bin/. /opt/guanfu/bin/

# 4. 起
sudo systemctl start guanfu
journalctl -u guanfu -f
```

库结构的升级是**自动**的：观复启动时按模型补齐缺的表与列，不用手动跑迁移。

## 六、出问题

| 现象 | 大概率是 |
|---|---|
| 打开页面 401 / 提示要人工凭证 | 远程访问要走反代；本机直接开就自动认 |
| 反代后打不开 / Host 被拒 | `Guanfu__AllowedHosts` 少了这个域名 |
| agent 连不上 `/mcp` | 反代没放行 WebSocket；或凭证没带 `X-Guanfu-Token` |
| 「有人走过」但看不出是谁 | 审计页的「谁的人」一列 —— 凭证要**绑到成员**才有 |
| 验哈希链报断裂 | 审计链头归档没配，或数据目录被人动过 |
| 交付包下载不了 | 分享链接过期了（默认 7 天）或被撤销了 |

更多：`docs/DEPLOYMENT.md`（部署手册）、`docs/USAGE.md`（怎么用）。
