#!/usr/bin/env python3
"""灌一套演示数据 —— 给团队自己看、给客户演示时用。

    python3 scripts/seed-demo.py --url http://127.0.0.1:5177 --token <人工凭证>

造出来的东西：
- 一个项目「订单中心重构」+ 两个迭代版本
- 三个成员（张伟 / 李娜 / 王强，密码 demo-password）
- 一堆处在不同状态的任务（草稿 / 开发中 / 评审中 / 已完成 / 受阻）
- 文档（PRD、接口说明）、产物（SQL 脚本、测试报告）
- 一条待批的审批、一段审计流水

只往库里加东西，不删不改已有数据 —— 重复跑会多灌一份。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import urllib.error
import urllib.request


def call(rest: str, method: str, path: str, body=None, token: str = ""):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["X-Guanfu-Human-Token"] = token

    request = urllib.request.Request(
        rest.rstrip("/") + path, data=data, headers=headers, method=method
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read().decode()
            return json.loads(payload) if payload else {}
    except urllib.error.HTTPError as error:
        body = error.read().decode()
        print(f"  ! {method} {path} → HTTP {error.code}: {body[:160]}", file=sys.stderr)
        return None


def upload_document(rest: str, task_id: str, filename: str, content: str, kind: str = ""):
    """传一个文档（multipart/form-data）—— 文档接口只收文件，不收 JSON。"""
    boundary = "----guanfuseed" + "".join(c for c in filename if c.isascii() and c.isalnum())
    parts = []

    def field(name: str, value: str):
        parts.append(
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
        )

    parts.append(
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
        f"Content-Type: text/markdown\r\n\r\n".encode()
    )
    parts.append(content.encode())
    parts.append(f"\r\n--{boundary}--\r\n".encode())

    request = urllib.request.Request(
        rest.rstrip("/") + f"/api/tasks/{task_id}/documents",
        data=b"".join(parts),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read().decode()
            return json.loads(payload) if payload else {}
    except urllib.error.HTTPError as error:
        print(f"  ! 上传文档 {filename} → HTTP {error.code}: {error.read().decode()[:160]}", file=sys.stderr)
        return None


class Mcp:
    """MCP over HTTP —— 只用来看一眼工具面、**造一条待批审批**。

    审批单不是 REST 能造的：它只从**写工具**里长出来（工具先问人要批）。
    所以要演示「待批审批」这一页，就得真的调一次要审批的写工具。
    """

    def __init__(self, url: str, token: str = ""):
        self.endpoint = url.rstrip("/") + "/mcp"
        self.token = token
        self.session_id = ""
        self._next_id = 0

    def _post(self, body: dict) -> dict:
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": "2025-06-18",
        }
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
        if self.token:
            headers["X-Guanfu-Token"] = self.token

        request = urllib.request.Request(
            self.endpoint, data=json.dumps(body).encode(), headers=headers, method="POST"
        )

        with urllib.request.urlopen(request, timeout=60) as response:
            session = response.headers.get("Mcp-Session-Id")
            if session:
                self.session_id = session
            text = response.read().decode()

        # 走 SSE 的回包只取 data 行
        if "data:" in text:
            data_lines = [
                line.strip()[len("data:"):].strip()
                for line in text.split("\n")
                if line.strip().startswith("data:")
            ]
            if data_lines:
                text = "".join(data_lines)

        try:
            return json.loads(text) if text.strip() else {}
        except json.JSONDecodeError:
            return {}

    def initialize(self) -> None:
        self._next_id += 1
        self._post({
            "jsonrpc": "2.0",
            "id": self._next_id,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "guanfu-seed", "version": "0.1.0"},
            },
        })
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def call(self, tool: str, arguments: dict) -> dict:
        self._next_id += 1
        payload = self._post({
            "jsonrpc": "2.0",
            "id": self._next_id,
            "method": "tools/call",
            "params": {"name": tool, "arguments": arguments},
        })

        result = payload.get("result", {})
        structured = result.get("structuredContent")
        if isinstance(structured, dict):
            return structured

        content = result.get("content") or []
        if content and isinstance(content[0], dict) and "text" in content[0]:
            try:
                return json.loads(content[0]["text"])
            except json.JSONDecodeError:
                return {"text": content[0]["text"]}

        return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5177")
    parser.add_argument("--token", default="", help="人工凭证（建项目要）")
    args = parser.parse_args()

    rest = args.url

    print("==> 项目")
    project = call(rest, "POST", "/api/projects", {
        "name": "订单中心重构",
        "description": "把下单、履约、售后三条线并到一个订单模型里",
    }, args.token)
    if not project:
        print("建项目失败 —— 人工凭证对吗？", file=sys.stderr)
        return 1
    pid = project["id"]

    print("==> 迭代版本")
    release = call(rest, "POST", f"/api/projects/{pid}/milestones", {
        "name": "12 月版本",
        "description": "订单模型上线 + 履约链路打通",
    }, args.token)
    group = call(rest, "POST", f"/api/projects/{pid}/milestones", {
        "name": "订单模型",
        "kind": "group",
        "parentId": release["id"],
    }, args.token)
    patch = call(rest, "POST", f"/api/projects/{pid}/milestones", {
        "name": "11 月补丁",
        "description": "线上问题的小修小补",
    }, args.token)

    print("==> 成员")
    members = {}
    for user_name, display_name, role in [
        ("zhangwei", "张伟", "Admin"),
        ("lina", "李娜", "Member"),
        ("wangqiang", "王强", "Member"),
    ]:
        created = call(rest, "POST", "/api/members", {
            "userName": user_name,
            "displayName": display_name,
            "password": "demo-password",
            "role": role,
        }, args.token)
        if created:
            members[display_name] = created["id"]

    print("==> 任务")
    tasks = [
        ("订单模型落库", "requirement", "DocsReady", group["id"], "张伟"),
        ("下单接口改造", "requirement", "InDev", group["id"], "李娜"),
        ("履约状态机", "requirement", "InDev", group["id"], "李娜"),
        ("退款流程", "bug", "Review", release["id"], "张伟"),
        ("订单导出脚本", "optimization", "Done", release["id"], "王强"),
        ("旧接口下线", "optimization", "Blocked", patch["id"], None),
        ("压测报告归档", "optimization", "Planned", release["id"], None),
    ]

    task_ids = []
    for title, kind, status, milestone, assignee in tasks:
        created = call(rest, "POST", f"/api/projects/{pid}/tasks", {
            "title": title,
            "type": kind,
            "description": f"{title} —— 演示数据",
        }, args.token)
        if not created:
            continue
        tid = created["id"]
        task_ids.append((tid, title))

        call(rest, "POST", f"/api/tasks/{tid}/milestone", {"milestoneId": milestone}, args.token)

        if assignee:
            # 归属到**成员**（不只是名字）—— 「我负责的」与可见性都按成员算
            call(rest, "POST", f"/api/tasks/{tid}/assign", {
                "assignee": assignee,
                "assigneeMemberId": members.get(assignee),
            }, args.token)

        call(rest, "POST", f"/api/tasks/{tid}/plan", {
            "background": f"{title} 的背景",
            "understanding": f"{title} 要解决什么",
            "breakdown": "1. 读现状\n2. 改实现\n3. 补测试",
            "acceptanceCriteria": "单测全过 + 联调通过",
            "author": assignee or "agent-a",
        }, args.token)

        # 推进到目标状态（状态机要一步一步走；Blocked 不在主链上，单独走）
        path = ["Draft", "DocsReady", "Planned", "InDev", "Testing", "Review", "Done"]
        steps = ["DocsReady", "Blocked"] if status == "Blocked" else path[1:path.index(status) + 1]

        for step in steps:
            call(rest, "POST", f"/api/tasks/{tid}/status", {
                "status": step,
                "actor": assignee or "agent-a",
            }, args.token)

    print("==> 产物与文档")
    artifacts = [
        ("建表.sql", "SqlScript",
         "CREATE TABLE orders (\n"
         "  id bigint PRIMARY KEY,\n"
         "  user_id bigint NOT NULL,\n"
         "  status varchar(32) NOT NULL,\n"
         "  amount decimal(12,2) NOT NULL,\n"
         "  created_at timestamptz NOT NULL\n"
         ");"),
        ("回滚.sql", "SqlScript",
         "DROP TABLE IF EXISTS orders;\n"),
        ("压测结果.txt", "TestReport",
         "500 并发下单：P99 = 320ms，错误率 0.02%"),
    ]

    for title, kind, content in artifacts:
        tid = task_ids[0][0] if kind == "SqlScript" else task_ids[4][0]
        call(rest, "POST", f"/api/tasks/{tid}/artifacts", {
            "title": title,
            "type": kind,
            "content": content,
            "author": "agent-a",
        }, args.token)

    print("==> 文档")
    documents = [
        (task_ids[0][0], "订单模型 PRD.md", "prd", "# 订单模型 PRD\n\n## 背景\n三条线并到一个订单模型。\n\n## 范围\n- 下单\n- 履约\n- 售后\n"),
        (task_ids[1][0], "下单接口说明.md", "apidoc", "# 下单接口\n\nPOST /api/orders\n\n请求体：商品、数量、地址。\n"),
    ]
    for tid, filename, kind, content in documents:
        upload_document(rest, tid, filename, content, kind)

    print("==> 待批审批")
    # 审批单只能从**写工具**里长出来 —— REST 没有「造一条审批」的端点。
    # 所以这里真的调一次要审批的写工具（shell_run），让人看到「待批」长什么样。
    work_dir = tempfile.mkdtemp(prefix="guanfu-demo-")
    call(rest, "PUT", f"/api/projects/{pid}/config", {
        "repoPath": work_dir,
        "allowedCommands": ["touch"],
    }, args.token)

    mcp = Mcp(rest, args.token)
    mcp.initialize()
    pending = mcp.call("shell_run", {
        "projectId": pid,
        "command": "touch demo-approval-marker",
        "waitSeconds": 1,
        "callerId": "agent-a",
    })

    if pending.get("approvalStatus") == "Pending" or pending.get("approvalId"):
        print("  已造一条待批审批（在「待批操作」页）")
    else:
        print(f"  ! 没造出待批审批：{json.dumps(pending, ensure_ascii=False)[:200]}", file=sys.stderr)

    print()
    print("演示数据灌好了：")
    print(f"  项目 {pid}")
    print(f"  界面 {rest}/projects/{pid}")
    print("  成员账号：张伟 / 李娜 / 王强（密码都是 demo-password）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
