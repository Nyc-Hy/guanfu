#!/usr/bin/env python3
"""写操作的人工审批门禁 —— 企业落地最关键的一条链路。

观复的卖点不是「记录状态」，是「写操作不会在没人点头的情况下发生」。
这条链路端到端验：

  D1 白名单外的命令直接拒 —— 连审批单都不建
  D2 没批准不执行（命令根本没跑）
  D3 人工批准后带 approvalId 续跑才执行
  D4 一条审批只能执行一次（复用被拒）
  D5 审批绑的是「那一条命令」—— 换个命令用同一条审批被拒
  D6 审批绑的是「那个项目」—— 换个项目用同一条审批被拒
  D7 撤销是后悔药（批错了但还没执行）
  D8 只读通道不许写（db_query 走 AST 判定，不碰真库）
  D9 db_execute 也要审批，不批就不执行
  D10 全程有审计留痕
  D11 只读调用也留痕

不需要 SQL Server：只读判定发生在真正连库之前，写操作在审批前根本不会下发。

用法:
    python3 tests/e2e/approval_e2e.py --url http://127.0.0.1:5191
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile
import time
import urllib.error
import urllib.request

from mcp_e2e import Mcp, expect, section, rest_json, FAILURES

MARKER = "approved-marker.txt"
OTHER_MARKER = "other-marker.txt"
THIRD_MARKER = "third-marker.txt"
CROSS_MARKER = "cross-marker.txt"
REVOKE_MARKER = "revoke-marker.txt"


def rest_status(
    rest: str,
    method: str,
    path: str,
    body: dict | None = None,
    with_token: bool = True,
) -> tuple[int, str]:
    """返回 (HTTP 状态码, 响应正文)。敏感端点没凭证时是 401 —— 这里要看得见。"""
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}

    token = os.environ.get("WB_HUMAN_TOKEN", "") if with_token else ""
    if token:
        headers["X-Guanfu-Human-Token"] = token

    request = urllib.request.Request(
        rest.rstrip("/") + path, data=data, headers=headers, method=method
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def rest_login(rest: str, user_name: str, password: str) -> tuple[int, str, str | None]:
    """登录并拿会话 Cookie。返回 (状态码, 正文, Cookie 头)。"""
    data = json.dumps({"userName": user_name, "password": password}).encode()
    request = urllib.request.Request(
        rest.rstrip("/") + "/api/auth/login",
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode(), response.headers.get("Set-Cookie")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(), None


def rest_with_cookie(
    rest: str,
    method: str,
    path: str,
    cookie: str | None,
    body: dict | None = None,
) -> tuple[int, str]:
    """带会话 Cookie 发一个请求（不带人工凭证）。"""
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}

    if cookie:
        headers["Cookie"] = cookie.split(";")[0]

    request = urllib.request.Request(
        rest.rstrip("/") + path, data=data, headers=headers, method=method
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5191")
    parser.add_argument("--rest", default=None, help="REST 根地址，默认与 --url 相同")
    args = parser.parse_args()

    rest = args.rest or args.url
    # 客户端在连接头上表明自己是谁 —— 真实客户端（Claude Code 等）不会给
    # 工具显式传 callerId，审计要能从连接头认出「谁调的」。
    mcp = Mcp(args.url, headers={"X-Guanfu-Agent": "e2e-agent"})
    mcp.initialize()

    suffix = str(int(time.time()))[-6:]
    project_id = rest_json(rest, "POST", "/api/projects", {"name": f"审批门禁 e2e {suffix}"})["id"]
    other_project_id = rest_json(rest, "POST", "/api/projects", {"name": f"别的项目 {suffix}"})["id"]

    # shell_run 要有仓库路径（命令在仓库目录里跑）和命令白名单。
    work_dir = tempfile.mkdtemp(prefix="wb-e2e-shell-")

    def marker(name: str) -> str:
        return os.path.join(work_dir, name)

    def request_approval(command: str, task_id: str | None = None) -> tuple[str, dict]:
        """申请一条审批（不批），返回 (approvalId, 工具返回)。"""
        result = mcp.call(
            "shell_run",
            {
                "projectId": project_id,
                "command": command,
                "waitSeconds": 1,
                "taskId": task_id,
                "callerId": "e2e-agent",
            },
        )
        return result.get("approvalId") or "", result

    def approve(approval_id: str) -> tuple[int, str]:
        return rest_status(
            rest,
            "POST",
            f"/api/approvals/{approval_id}/decide",
            {"approve": True, "decidedBy": "e2e-human"},
        )

    try:
        rest_json(
            rest,
            "PUT",
            f"/api/projects/{project_id}/config",
            {"repoPath": work_dir, "allowedCommands": ["touch"]},
        )
        # 另一个项目配一样的仓库与白名单 —— 白名单检查在闸门之前，
        # 不配的话 D6 会被白名单拦下，测不到「审批绑项目」这一层。
        rest_json(
            rest,
            "PUT",
            f"/api/projects/{other_project_id}/config",
            {"repoPath": work_dir, "allowedCommands": ["touch"]},
        )

        task_id = mcp.call(
            "guanfu_create_task",
            {"projectId": project_id, "title": "审批门禁 e2e 任务", "type": "requirement"},
        )["id"]

        # ── D1 白名单 ─────────────────────────────────────────────────
        section("链路 D：写操作的人工审批门禁")

        print("\n-- D1 白名单外的命令直接拒 --")
        before = len(rest_json(rest, "GET", "/api/approvals?status=Pending"))
        denied = mcp.call(
            "shell_run",
            {"projectId": project_id, "command": "ls -la", "waitSeconds": 1, "callerId": "e2e-agent"},
        )
        expect(not denied.get("ok"), "白名单外的命令应被拒")
        expect("白名单" in (denied.get("error") or ""), "错误信息应说明是白名单问题")
        after = len(rest_json(rest, "GET", "/api/approvals?status=Pending"))
        expect(before == after, "白名单外的命令不该产生审批单")

        # ── D2 没批准不执行 ────────────────────────────────────────────
        print("\n-- D2 没批准不执行 --")
        approval_id, pending = request_approval(f"touch {MARKER}", task_id)
        expect(not pending.get("ok"), "未批准时不应成功")
        expect(pending.get("approvalStatus") == "Pending", "审批状态应为 Pending")
        expect(bool(approval_id), "应返回 approvalId 供人工批准后续跑")
        expect(
            not os.path.exists(marker(MARKER)),
            "没批准时命令根本不该执行（标记文件不应存在）",
        )

        detail = rest_json(rest, "GET", f"/api/approvals?status=Pending&projectId={project_id}")
        expect(any(a["id"] == approval_id for a in detail), "待审列表里应有这条审批")
        expect(
            any(a["toolName"] == "shell_run" and a["id"] == approval_id for a in detail),
            "审批单应记明是哪个工具要写",
        )
        expect(
            any(a["id"] == approval_id and MARKER in (a.get("payload") or "") for a in detail),
            "审批单里应带命令原文（人批的时候要知道批的是什么）",
        )

        # ── D3 人工批准后续跑 ──────────────────────────────────────────
        print("\n-- D3 人工批准后带 approvalId 续跑 --")
        status, body = approve(approval_id)
        expect(status == 200, f"人工批准应成功（HTTP {status} {body[:120]}）")

        executed = mcp.call(
            "shell_run",
            {
                "projectId": project_id,
                "command": f"touch {MARKER}",
                "approvalId": approval_id,
                "taskId": task_id,
                "callerId": "e2e-agent",
            },
        )
        expect(executed.get("ok"), "批准后带 approvalId 续跑应执行成功")
        expect(
            os.path.exists(marker(MARKER)),
            "批准后命令应真的执行了（标记文件应存在）",
        )

        # ── D4 一条审批只能执行一次 ────────────────────────────────────
        print("\n-- D4 一条审批只能执行一次 --")
        replay = mcp.call(
            "shell_run",
            {
                "projectId": project_id,
                "command": f"touch {MARKER}",
                "approvalId": approval_id,
                "taskId": task_id,
                "callerId": "e2e-agent",
            },
        )
        expect(not replay.get("ok"), "同一条审批不该能执行第二次")
        expect(
            "Executed" in (replay.get("error") or "") or "用掉" in (replay.get("error") or ""),
            "错误信息应说清这条审批已经用过了",
        )

        # ── D5 审批绑的是「那一条命令」 ────────────────────────────────
        # 这里必须用**一条还没被消费过的**审批：校验顺序是
        # 「状态 → 工具 → 类别 → 项目 → 参数指纹」。
        # 拿一条已 Executed 的审批来测，只会撞在「状态」这一关，
        # 测不到「参数指纹」这一关。
        print("\n-- D5 换个命令用同一条审批 --")
        bound_id, _ = request_approval(f"touch {OTHER_MARKER}", task_id)
        expect(bool(bound_id), "应生成一条待审审批")
        status, body = approve(bound_id)
        expect(status == 200, f"批准应成功（HTTP {status} {body[:120]}）")

        other = mcp.call(
            "shell_run",
            {
                "projectId": project_id,
                "command": f"touch {THIRD_MARKER}",
                "approvalId": bound_id,
                "taskId": task_id,
                "callerId": "e2e-agent",
            },
        )
        expect(not other.get("ok"), "换个命令不该能用原审批")
        expect(
            "不一致" in (other.get("error") or ""),
            "错误信息应指出操作内容与当初审批的不一致",
        )
        expect(
            not os.path.exists(marker(THIRD_MARKER)),
            "没被批准的命令不该执行",
        )
        expect(
            not os.path.exists(marker(OTHER_MARKER)),
            "批过的那条命令也不该顺带执行（指纹不符就不该有任何副作用）",
        )

        # ── D6 审批绑的是「那个项目」 ──────────────────────────────────
        print("\n-- D6 换个项目用同一条审批 --")
        cross_id, _ = request_approval(f"touch {CROSS_MARKER}", task_id)
        expect(bool(cross_id), "应生成一条待审审批")
        status, body = approve(cross_id)
        expect(status == 200, f"批准应成功（HTTP {status} {body[:120]}）")

        cross = mcp.call(
            "shell_run",
            {
                "projectId": other_project_id,
                "command": f"touch {CROSS_MARKER}",
                "approvalId": cross_id,
                "callerId": "e2e-agent",
            },
        )
        expect(not cross.get("ok"), "别的项目不该能用这条审批")
        expect("别的项目" in (cross.get("error") or ""), "错误信息应说明审批属于别的项目")
        expect(
            not os.path.exists(marker(CROSS_MARKER)),
            "跨项目用审批不该执行命令",
        )

        # ── D7 撤销是后悔药 ────────────────────────────────────────────
        print("\n-- D7 撤销 = 批错了但还没执行 --")
        revoke_id, _ = request_approval(f"touch {REVOKE_MARKER}", task_id)
        expect(bool(revoke_id), "应生成一条待审审批")

        status, body = rest_status(
            rest, "POST", f"/api/approvals/{revoke_id}/revoke", {"reason": "e2e：批错了"}
        )
        expect(status == 200, f"撤销应成功（HTTP {status} {body[:120]}）")

        after_revoke = mcp.call(
            "shell_run",
            {
                "projectId": project_id,
                "command": f"touch {REVOKE_MARKER}",
                "approvalId": revoke_id,
                "taskId": task_id,
                "callerId": "e2e-agent",
            },
        )
        expect(not after_revoke.get("ok"), "撤销后的审批不该再能执行")
        expect(
            not os.path.exists(marker(REVOKE_MARKER)),
            "撤销后命令不该执行",
        )

        # ── D8 只读通道不许写 ──────────────────────────────────────────
        print("\n-- D8 只读通道不许写 --")
        # 只登记连接配置，不真连库：只读判定发生在连库之前。
        rest_json(
            rest,
            "POST",
            f"/api/projects/{project_id}/db-connections",
            {
                "name": "e2e 假连接",
                "server": "127.0.0.1,14330",
                "database": "e2e",
                "userId": "sa",
                "password": "e2e-only-never-connected",
                "isDefault": True,
            },
        )

        write_via_query = mcp.call(
            "db_query",
            {"projectId": project_id, "sql": "UPDATE dbo.Users SET Name = 'x'"},
        )
        expect(not write_via_query.get("ok"), "db_query 不该放行写语句")
        expect(
            "db_execute" in (write_via_query.get("error") or ""),
            "错误信息应指引改用 db_execute（走人工审批）",
        )

        unknown_via_query = mcp.call(
            "db_query",
            {"projectId": project_id, "sql": "EXEC sp_who"},
        )
        expect(not unknown_via_query.get("ok"), "认不出来的语句应被拦")

        # ── D9 db_execute 也要审批 ─────────────────────────────────────
        print("\n-- D9 db_execute 也要审批 --")
        db_write = mcp.call(
            "db_execute",
            {
                "projectId": project_id,
                "sql": "CREATE TABLE dbo.E2EProbe (Id INT)",
                "waitSeconds": 1,
                "callerId": "e2e-agent",
            },
        )
        expect(not db_write.get("ok"), "未批准的 db_execute 不该执行")
        expect(db_write.get("approvalStatus") == "Pending", "db_execute 也应产生待审审批")
        expect(bool(db_write.get("approvalId")), "db_execute 也应返回 approvalId")

        # ── D10 审计留痕 ───────────────────────────────────────────────
        print("\n-- D10 全程有审计留痕 --")
        audit = mcp.call(
            "guanfu_list_audit", {"projectId": project_id, "limit": 200}
        )
        shell_audit = [a for a in (audit or []) if a.get("toolName") == "shell_run"]
        expect(len(shell_audit) > 0, "shell_run 的调用应有审计记录")
        outcomes = {a.get("outcome") for a in shell_audit}
        expect("pending" in outcomes, "待审的那次调用应记为 pending")
        expect("success" in outcomes, "执行成功的那次调用应记为 success")
        expect(
            any(a.get("approvalId") for a in shell_audit),
            "审计应记下用的是哪条审批",
        )
        expect(
            any(a.get("callerId") == "e2e-agent" for a in shell_audit),
            "审计应记下是谁调的",
        )
        query_audit = [
            a for a in (audit or []) if a.get("toolName") == "db_query"
        ]
        expect(
            any(a.get("outcome") == "denied" for a in query_audit),
            "被策略拦下的只读尝试也应留痕（denied）",
        )

        # ── D11 只读调用也留痕 ───────────────────────────────────────────
        print("\n-- D11 只读调用也留痕 --")
        mcp.call("guanfu_list_tasks", {"projectId": project_id})
        audit = mcp.call(
            "guanfu_list_audit", {"projectId": project_id, "limit": 200}
        )
        read_audit = [
            a for a in (audit or []) if a.get("toolName") == "guanfu_list_tasks"
        ]
        expect(
            len(read_audit) > 0,
            "只读调用也应留审计 —— 「谁访问了什么资源」要查得出来",
        )
        expect(
            any(a.get("callerId") == "e2e-agent" for a in read_audit),
            "只读调用也要记下是谁调的",
        )

        # ── D12 打包交付也要有人在场 ────────────────────────────────────
        # 交付包是要**发出去**的东西（给项管去生产环境执行）——
        # 没人工凭证就不许打，跟 workspace export 同级。
        print("\n-- D12 打包交付没凭证就是 401 --")
        delivery_project = rest_json(
            rest, "POST", "/api/projects", {"name": f"交付 e2e {suffix}"}
        )
        delivery_milestone = rest_json(
            rest,
            "POST",
            f"/api/projects/{delivery_project['id']}/milestones",
            {"name": "12月版本"},
        )
        delivery_task = rest_json(
            rest,
            "POST",
            f"/api/projects/{delivery_project['id']}/tasks",
            {"title": "写上线脚本"},
        )
        rest_json(
            rest,
            "POST",
            f"/api/tasks/{delivery_task['id']}/milestone",
            {"milestoneId": delivery_milestone["id"]},
        )
        artifact = rest_json(
            rest,
            "POST",
            f"/api/tasks/{delivery_task['id']}/artifacts",
            {
                "title": "建表.sql",
                "type": "SqlScript",
                "content": "CREATE TABLE orders(id int primary key);",
            },
        )

        no_token_status, _ = rest_status(
            rest,
            "POST",
            f"/api/milestones/{delivery_milestone['id']}/delivery/packages",
            {"itemIds": [artifact["id"]]},
            with_token=False,
        )
        expect(
            no_token_status == 401,
            f"没人工凭证不该打得了交付包（实际 {no_token_status}）",
        )

        with_token_status, body = rest_status(
            rest,
            "POST",
            f"/api/milestones/{delivery_milestone['id']}/delivery/packages",
            {"itemIds": [artifact["id"]]},
        )
        expect(
            with_token_status in (200, 201),
            f"有人在场就该打得出交付包（实际 {with_token_status}）",
        )
        expect(
            ".zip" in body,
            "交付包应带一个 zip 文件名",
        )

        # ── D13 成员的账号与登录 ───────────────────────────────────────
        # 团队部署时「谁在操作必须是真的」：
        # 成员用账号密码登录，管理员会话能管成员，没会话就管不了。
        print("\n-- D13 成员登录与成员管理 --")
        member_user = f"e2e-admin-{suffix}"
        member_password = "e2e-password-1"

        create_status, _ = rest_status(
            rest,
            "POST",
            "/api/members",
            {
                "userName": member_user,
                "displayName": "e2e 管理员",
                "password": member_password,
                "role": "Admin",
            },
        )
        expect(
            create_status in (200, 201),
            f"owner 该能建成员（实际 {create_status}）",
        )

        login_status, _, cookie = rest_login(rest, member_user, member_password)
        expect(login_status == 200, f"成员该能登录（实际 {login_status}）")
        expect(cookie is not None, "登录后该拿到会话 Cookie")

        bad_status, _, _ = rest_login(rest, member_user, "wrong-password")
        expect(bad_status == 401, f"密码不对该是 401（实际 {bad_status}）")

        if cookie:
            members_status, members_body = rest_with_cookie(
                rest, "GET", "/api/members", cookie
            )
            expect(
                members_status == 200,
                f"管理员会话该能看成员名单（实际 {members_status}）",
            )
            expect(
                member_user in members_body,
                "成员名单里该有刚建的那个",
            )

        no_session_status, _ = rest_with_cookie(rest, "GET", "/api/members", None)
        expect(
            no_session_status == 403,
            f"没登录不该能看成员名单（实际 {no_session_status}）",
        )

        # ── D14 成员的钥匙：审计里要出现「这把钥匙是谁的」 ──────────
        print("\n-- D14 成员凭证的调用要在审计里留下成员 --")
        member_user = f"e2e-key-{suffix}"
        member_password = "e2e-password-2"

        create_member_status, member_body = rest_status(
            rest,
            "POST",
            "/api/members",
            {
                "userName": member_user,
                "displayName": "钥匙主人",
                "password": member_password,
                "role": "Member",
            },
        )
        expect(create_member_status in (200, 201), f"该能建成员（实际 {create_member_status}）")

        member_id = None
        if create_member_status in (200, 201):
            try:
                member_id = json.loads(member_body).get("id")
            except Exception:
                member_id = None

        issue_status, issue_body = rest_status(
            rest,
            "POST",
            "/api/credentials",
            {
                "kind": "Agent",
                "subject": "e2e-member-agent",
                "memberId": member_id,
                "note": "e2e 成员凭证",
            },
        )
        expect(issue_status in (200, 201), f"该能签发凭证（实际 {issue_status}）")

        token = None
        if issue_status in (200, 201):
            try:
                token = json.loads(issue_body).get("token")
            except Exception:
                token = None

        if token:
            member_client = Mcp(args.url, headers={"X-Guanfu-Token": token})
            member_client.initialize()
            member_client.call("guanfu_list_projects", {})

            audit_rows = rest_json(rest, "GET", "/api/audit?limit=200")
            owned = [r for r in audit_rows if r.get("memberName") == "钥匙主人"]
            expect(
                len(owned) >= 1,
                f"审计里该出现这把钥匙背后的人（实际：{len(owned)} 条）",
            )
        else:
            print("  ⚠ 没签发出凭证，跳过成员审计断言")

        section("结果")
        if FAILURES:
            print(f"❌ 失败 {len(FAILURES)} 项:")
            for failure in FAILURES:
                print(f"  - {failure}")
            return 1

        print("✅ 全部通过")
        return 0
    finally:
        shutil.rmtree(work_dir, ignore_errors=True)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001
        print(f"❌ e2e 执行异常: {error}")
        sys.exit(2)
