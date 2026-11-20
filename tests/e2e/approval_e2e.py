#!/usr/bin/env python3
"""链路 D：数据库写入审批、审计、团队身份与交付门禁。"""
from __future__ import annotations

import argparse
import json
import os
import select
import socket
import sys
import time
import urllib.error
import urllib.request

from mcp_e2e import Mcp, expect, section, rest_json, FAILURES

def create_fixture_connection(rest: str, project_id: str, server: str = "127.0.0.1,1") -> dict:
    """只在临时服务登记测试用的回环连接。"""
    return rest_json(rest, "POST", f"/api/projects/{project_id}/db-connections", {
        "name": "数据库审批测试",
        "server": server,
        "database": "guanfu_test",
        "userId": "test-user",
        "password": "test-password",
    })


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


def rest_bytes(rest: str, path: str, cookie: str | None = None) -> tuple[int, bytes]:
    """取二进制内容（下载 zip 用 —— 按文本解会炸）。"""
    request = urllib.request.Request(rest.rstrip("/") + path, method="GET")
    if cookie:
        request.add_header("Cookie", cookie.split(";")[0])

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


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

    with socket.socket() as database:
        database.bind(("127.0.0.1", 0))
        database.listen()
        server = f"127.0.0.1,{database.getsockname()[1]}"
        connection = create_fixture_connection(rest, project_id, server)
        create_fixture_connection(rest, other_project_id, server)
        sql = "UPDATE dbo.E2EProbe SET Id = 1"

        def request_write(statement: str, **arguments) -> dict:
            return mcp.call("db_execute", {
                "projectId": project_id, "sql": statement, "waitSeconds": 1,
                "callerId": "e2e-agent", **arguments,
            })

        print("\n-- 数据库写入须取得相应审批 --")
        rest_json(rest, "POST", f"/api/db-connections/{connection['id']}/rules", {
            "tableName": "E2EProtected", "writeForbidden": True,
        })
        denied = request_write("UPDATE dbo.E2EProtected SET Id = 1")
        expect(not denied.get("ok") and "禁止写" in (denied.get("error") or ""), "禁写表直接拒绝")
        expect(not rest_json(rest, "GET", f"/api/approvals?projectId={project_id}"), "禁写不创建审批")

        pending = request_write(sql)
        approval_id = pending.get("approvalId")
        expect(bool(approval_id) and pending.get("approvalStatus") == "Pending", "未批准时 SQL 只创建待审审批")
        status, _ = rest_status(rest, "POST", f"/api/approvals/{approval_id}/decide",
                                {"approve": True, "decidedBy": "e2e-human"})
        expect(status == 200, "人工可以批准数据库写入")
        modified = request_write("UPDATE dbo.E2EProbe SET Id = 2", approvalId=approval_id)
        expect(not modified.get("ok") and "不一致" in (modified.get("error") or ""), "审批不能换 SQL 使用")
        crossed = request_write(sql, projectId=other_project_id, approvalId=approval_id)
        expect(not crossed.get("ok"), "审批不能换项目使用")
        retained = mcp.call("guanfu_get_approval", {"approvalId": approval_id})
        expect(retained.get("status") == "Approved", "拒绝的请求没有消费审批")

        status, _ = rest_status(rest, "POST", f"/api/approvals/{approval_id}/revoke", {"reason": "撤销测试"})
        expect(status == 200, "人工可以撤销未执行的数据库写入审批")
        revoked = request_write(sql, approvalId=approval_id)
        expect(not revoked.get("ok") and revoked.get("approvalStatus") == "Rejected", "已撤销的审批不能执行")

        # ── D8 只读通道不许写 ──────────────────────────────────────────
        print("\n-- D8 只读通道不许写 --")
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
        write_audit = [a for a in (audit or []) if a.get("toolName") == "db_execute"]
        expect(len(write_audit) > 0, "db_execute 的调用应有审计记录")
        outcomes = {a.get("outcome") for a in write_audit}
        expect("pending" in outcomes, "待审的那次调用应记为 pending")
        expect("denied" in outcomes, "被拦下的写入应记为 denied")
        expect(
            any(a.get("approvalId") for a in write_audit),
            "审计应记下用的是哪条审批",
        )
        expect(
            any(a.get("callerId") == "e2e-agent" for a in write_audit),
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

        # 交付的最后一公里：项管没有观复账号，他要的是一个能点的链接。
        package_id = None
        try:
            package_id = json.loads(body).get("id")
        except Exception:
            package_id = None

        if package_id:
            share_status, share_body = rest_status(
                rest,
                "POST",
                f"/api/delivery-packages/{package_id}/share",
                {},
            )
            expect(
                share_status == 200,
                f"有人在场就该签得出分享链接（实际 {share_status}）",
            )

            token = None
            try:
                token = json.loads(share_body).get("token")
            except Exception:
                token = None

            if token:
                # 拿链接的人**没有凭证**也能下载
                download_status, download_body = rest_bytes(rest, f"/delivery/{token}")
                expect(
                    download_status == 200,
                    f"拿分享链接的人该能直接下载（实际 {download_status}）",
                )
                expect(
                    download_body.startswith(b"PK"),
                    "下载到的该是 zip 文件",
                )

                # 撤销之后链接就失效了
                revoke_status, _ = rest_status(
                    rest,
                    "DELETE",
                    f"/api/delivery-packages/{package_id}/share",
                )
                expect(revoke_status in (200, 204), f"该能撤销分享链接（实际 {revoke_status}）")

                after_status, _ = rest_status(rest, "GET", f"/delivery/{token}")
                expect(
                    after_status == 404,
                    f"撤销之后链接该失效（实际 {after_status}）",
                )
            else:
                print("  ⚠ 没签发出分享链接，跳过分享下载断言")

        # ── D13 成员的账号与登录 ───────────────────────────────────────
        # 团队部署时「谁在操作必须是真的」：
        # 成员用账号密码登录，管理员会话能管成员，没会话就管不了。
        print("\n-- D13 成员登录与成员管理 --")
        member_user = f"e2e-admin-{suffix}"
        member_password = "e2e-password-1"

        create_status, admin_body = rest_status(
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
            no_session_status == 401,
            f"没登录不该能看成员名单（实际 {no_session_status}）",
        )

        for method, path, payload in [
            ("GET", "/api/projects", None),
            ("POST", f"/api/projects/{project_id}/tasks", {"title": "未登录不能建任务"}),
            ("POST", "/_blazor/negotiate?negotiateVersion=1", {}),
            ("GET", "/api/human-token", None),
        ]:
            status, _ = rest_with_cookie(rest, method, path, None, payload)
            expect(status == 401, f"团队模式下未登录的 {method} {path} 应返回 401（实际 {status}）")

        status, login_page = rest_with_cookie(rest, "GET", "/", None)
        expect(status == 200 and 'id="wb-login"' in login_page and "blazor.web.js" not in login_page,
               "未登录访问主页应进入独立登录表单")

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

        # ── D15 个人数据隔离：拿别人的任务 id 直接调接口也不行 ────
        print("\n-- D15 别人的任务 id 调不通 --")
        member_user = f"e2e-iso-{suffix}"
        member_password = "e2e-password-3"

        iso_status, iso_body = rest_status(
            rest,
            "POST",
            "/api/members",
            {
                "userName": member_user,
                "displayName": "隔离成员",
                "password": member_password,
                "role": "Member",
            },
        )
        expect(iso_status in (200, 201), f"该能建成员（实际 {iso_status}）")

        iso_login_status, _, iso_cookie = rest_login(rest, member_user, member_password)
        expect(iso_login_status == 200, f"成员该能登录（实际 {iso_login_status}）")

        # 再建一个「别人」，用来当被分派的那一方
        other_status, other_body = rest_status(
            rest,
            "POST",
            "/api/members",
            {
                "userName": f"e2e-other-{suffix}",
                "displayName": "另一位",
                "password": member_password,
                "role": "Member",
            },
        )
        other_member_id = None
        if other_status in (200, 201):
            try:
                other_member_id = json.loads(other_body).get("id")
            except Exception:
                other_member_id = None

        if iso_cookie:
            # 这个任务是 owner 建的、没分派 → 对成员是可见的（未分派谁都能看）。
            # 所以先拿一个**分派给别人**的任务来试。
            other_task = rest_json(
                rest,
                "POST",
                f"/api/projects/{project_id}/tasks",
                {"title": "e2e 隔离任务", "type": "requirement"},
            )
            rest_json(
                rest,
                "POST",
                f"/api/tasks/{other_task['id']}/assign",
                {"assignee": "别人", "assigneeMemberId": other_member_id},
            )

            status, body = rest_with_cookie(
                rest, "GET", f"/api/tasks/{other_task['id']}", iso_cookie
            )
            expect(
                status == 404,
                f"成员不该能按 id 读到别人的任务（实际 HTTP {status}）",
            )

            status, body = rest_with_cookie(
                rest, "GET", f"/api/tasks/{other_task['id']}/documents", iso_cookie
            )
            expect(
                status == 404,
                f"成员不该能按 id 读到别人的任务文档（实际 HTTP {status}）",
            )

            document = mcp.call("doc_upload", {
                "taskId": other_task["id"],
                "fileName": "private.md",
                "content": "# 另一位成员的文档",
            })["documents"][0]
            status, _ = rest_with_cookie(
                rest, "GET", f"/api/documents/{document['id']}/outline", iso_cookie
            )
            expect(status == 404, "成员直接按文档编号读别人的文档应返回 404")

            print("\n-- D16 交付包遵守成员的任务可见性 --")
            iso_member_id = json.loads(iso_body)["id"]
            delivery_id = rest_json(
                rest, "POST", f"/api/projects/{project_id}/milestones", {"name": "成员交付测试"}
            )["id"]
            own_task = rest_json(
                rest, "POST", f"/api/projects/{project_id}/tasks",
                {"title": "自己的交付任务", "milestoneId": delivery_id},
            )
            rest_json(rest, "POST", f"/api/tasks/{own_task['id']}/assign",
                      {"assigneeMemberId": iso_member_id})
            rest_json(rest, "POST", f"/api/tasks/{other_task['id']}/milestone",
                      {"milestoneId": delivery_id})
            own_item = rest_json(
                rest, "POST", f"/api/tasks/{own_task['id']}/artifacts",
                {"title": "自己的报告", "type": "TestReport", "content": "自己的交付内容"},
            )["id"]
            package_list_path = f"/api/milestones/{delivery_id}/delivery/packages"
            own_package = rest_json(rest, "POST", package_list_path, {"itemIds": [own_item]})["id"]
            mixed_package = rest_json(
                rest, "POST", package_list_path, {"itemIds": [own_item, document["id"]]}
            )["id"]
            mixed_path = f"/api/delivery-packages/{mixed_package}"

            status, visible = rest_with_cookie(rest, "GET", package_list_path, iso_cookie)
            expect(status == 200 and [p["id"] for p in json.loads(visible)] == [own_package],
                   "包列表只显示全部来源任务均可见的包")
            status, zipped = rest_bytes(rest, f"/api/delivery-packages/{own_package}/download", iso_cookie)
            expect(status == 200 and zipped.startswith(b"PK"), "成员可下载自己任务的交付包")
            status, _ = rest_with_cookie(rest, "GET", mixed_path, iso_cookie)
            expect(status == 404, "成员不能读取含他人任务的交付包清单")
            status, _ = rest_bytes(rest, mixed_path + "/download", iso_cookie)
            expect(status == 404, "成员不能下载含他人任务的交付包")
            status, _ = rest_with_cookie(rest, "POST", mixed_path + "/share", iso_cookie, {})
            expect(status == 401, "普通成员不能签发分享链接")

            share_status, shared = rest_with_cookie(rest, "POST", mixed_path + "/share", cookie, {})
            expect(share_status == 200, "管理员可为交付包签发链接")
            if share_status == 200:
                status, zipped = rest_bytes(rest, f"/delivery/{json.loads(shared)['token']}")
                expect(status == 200 and zipped.startswith(b"PK"), "团队模式下公开分享仍可凭链接下载")

            rest_json(rest, "PUT", f"/api/members/{iso_member_id}/status", {"disabled": True})
            status, _ = rest_with_cookie(rest, "GET", "/api/projects", iso_cookie)
            expect(status == 401, "成员停用后旧会话不能访问业务接口")

        if cookie:
            admin_member_id = json.loads(admin_body)["id"]
            rest_json(rest, "PUT", f"/api/members/{admin_member_id}/role", {"role": "Member"})
            status, _ = rest_with_cookie(rest, "GET", "/api/members", cookie)
            expect(status == 403, "降级后的旧会话不能继续以管理员身份管理成员")

        ready, _, _ = select.select([database], [], [], 0)
        expect(not ready, "未通过审批或只读校验的 SQL 没有连接数据库")

    section("结果")
    if FAILURES:
        print(f"❌ 失败 {len(FAILURES)} 项:")
        for failure in FAILURES:
            print(f"  - {failure}")
        return 1

    print("✅ 全部通过")
    return 0

if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001
        print(f"❌ e2e 执行异常: {error}")
        sys.exit(2)
