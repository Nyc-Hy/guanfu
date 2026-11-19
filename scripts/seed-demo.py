#!/usr/bin/env python3
"""灌一套演示数据 —— 给团队自己看、给客户演示时用。

    python3 scripts/seed-demo.py --url http://127.0.0.1:5177 --token <人工凭证>

造出来的东西：
- 一个项目「订单中心重构」+ 两个迭代版本
- 一堆处在不同状态的任务（草稿 / 开发中 / 评审中 / 已完成 / 受阻）
- 文档（PRD、接口文档）、产物（SQL 脚本、测试报告）
- 一条待批的审批、一段审计流水

只往库里加东西，不删不改已有数据 —— 重复跑会多灌一份。
"""
from __future__ import annotations

import argparse
import json
import sys
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

        # 推进到目标状态（状态机要一步一步走）
        path = ["Draft", "DocsReady", "Planned", "InDev", "Testing", "Review", "Done"]
        for step in path[1:]:
            call(rest, "POST", f"/api/tasks/{tid}/status", {
                "status": step,
                "actor": assignee or "agent-a",
            }, args.token)
            if step == status:
                break

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

    print("==> 待批审批")
    call(rest, "POST", "/api/approvals", {
        "toolName": "db_execute",
        "kind": "db.write",
        "payload": json.dumps({
            "sql": "UPDATE orders SET status = 'paid' WHERE id IN (SELECT id FROM payment_pending);",
        }),
        "summary": "批量把待支付订单改成已支付",
        "requestedBy": "agent-a",
    }, args.token)

    print()
    print("演示数据灌好了：")
    print(f"  项目 {pid}")
    print(f"  界面 {rest}/projects/{pid}")
    print("  成员账号：张伟 / 李娜 / 王强（密码都是 demo-password）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
