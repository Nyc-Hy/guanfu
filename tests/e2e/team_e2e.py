#!/usr/bin/env python3
"""团队工作链路：远程 MCP、开发机 CLI、项管交付、备份恢复。"""
from __future__ import annotations

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from threading import Thread
import urllib.error
from zipfile import ZipFile

from approval_e2e import rest_bytes, rest_login, rest_with_cookie
from mcp_e2e import FAILURES, Mcp, expect, rest_json
from restart_e2e import free_port, start_server


def stop(process: subprocess.Popen) -> None:
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--cli", required=True)
    args = parser.parse_args()

    with TemporaryDirectory(prefix="guanfu-team-e2e-") as directory:
        root = Path(directory)
        live = root / "server"
        backup = root / "backup"
        restored = root / "restored"
        checkout = root / "dev-checkout"
        checkout.mkdir()
        report = checkout / "测试报告.md"
        report.write_text("# 测试报告\n通过", encoding="utf-8")
        port = free_port()
        url = f"http://127.0.0.1:{port}"
        server = start_server(args.dotnet, args.root, str(live), port)

        try:
            os.environ["WB_HUMAN_TOKEN"] = (live / ".human-token").read_text().strip()
            first = rest_json(url, "POST", "/api/projects", {"name": "项目甲"})["id"]
            second = rest_json(url, "POST", "/api/projects", {"name": "项目乙"})["id"]
            release = rest_json(url, "POST", f"/api/projects/{first}/milestones", {"name": "迭代一"})["id"]
            other_release = rest_json(url, "POST", f"/api/projects/{second}/milestones", {"name": "乙的版本"})["id"]
            developer = rest_json(url, "POST", "/api/members", {
                "userName": "dev", "password": "dev-e2e-pass", "role": "Member",
            })["id"]
            rest_json(url, "POST", "/api/members", {
                "userName": "pm", "password": "pm-e2e-pass", "role": "Admin",
            })
            task = rest_json(url, "POST", f"/api/projects/{first}/tasks", {
                "title": "本机开发", "milestoneId": release,
            })
            other = rest_json(url, "POST", f"/api/projects/{second}/tasks", {"title": "乙的任务"})
            rest_json(url, "POST", f"/api/tasks/{task['id']}/assign", {"assigneeMemberId": developer})
            owner = Mcp(url)
            owner.initialize()
            doc = owner.call("doc_upload", {
                "taskId": other["id"], "fileName": "乙的文档.md", "content": "# 乙的文档",
            })["documents"][0]
            other_connection = rest_json(url, "POST", f"/api/projects/{second}/db-connections", {
                "name": "乙的测试连接", "server": "test.invalid", "database": "fixture",
            })["id"]

            def issue(subject: str, project_ids: list[str]) -> str:
                return rest_json(url, "POST", "/api/credentials", {
                    "kind": "Agent", "subject": subject,
                    "memberId": developer, "projectScope": project_ids,
                })["token"]

            token = issue("agent-first", [first])
            other_token = issue("agent-second", [second])
            multi_token = issue("agent-multi", [first, second])
            agent = Mcp(url, {"X-Guanfu-Token": token})
            agent.initialize()
            other_agent = Mcp(url, {"X-Guanfu-Token": other_token})
            other_agent.initialize()
            multi_agent = Mcp(url, {"X-Guanfu-Token": multi_token})
            multi_agent.initialize()

            projects = agent.call("guanfu_list_projects", {})
            expect(isinstance(projects, list) and [project["id"] for project in projects] == [first],
                   "项目列表仅返回凭证授权的项目")
            expect(other_agent.call("guanfu_get_project", {"projectId": second}).get("id") == second,
                   "乙的凭证能访问乙项目")
            expect(other_agent.call("guanfu_get_task", {"taskId": task["id"]}).get("ok") is False,
                   "乙的凭证不能读甲的任务")
            for tool, payload in [
                ("guanfu_get_project", {"projectId": second}),
                ("guanfu_get_context", {"taskId": other["id"]}),
                ("doc_get_text", {"documentId": doc["id"]}),
                ("guanfu_add_artifact", {"taskId": other["id"], "title": "禁止写入", "content": "校验"}),
                ("guanfu_get_review_pack", {"taskId": other["id"]}),
                ("guanfu_list_audit", {"taskId": other["id"]}),
                ("db_list_rules", {"projectId": first, "connectionId": other_connection}),
                ("guanfu_update_milestone", {"milestoneId": other_release, "name": "禁止修改"}),
                ("guanfu_assign_milestone", {"taskId": task["id"], "milestoneId": other_release}),
                ("guanfu_bind_session", {"projectId": second, "repoPath": str(checkout), "agentId": "agent-first"}),
            ]:
                expect(agent.call(tool, payload).get("ok") is False, f"{tool} 拒绝甲凭证访问乙项目")
            expect(multi_agent.call("guanfu_list_tasks", {}).get("ok") is False,
                   "多项目凭证查询任务时须选项目")
            before = agent.call("guanfu_list_tasks", {})
            expect(isinstance(before, list) and [item["id"] for item in before] == [task["id"]],
                   "单项目凭证的任务列表自动收窄到授权项目")
            context = multi_agent.call("guanfu_get_context", {"projectId": first, "taskKey": task["key"]})
            expect(context.get("task", {}).get("id") == task["id"], "无服务器仓库时可按项目与任务编号取上下文")
            claim = agent.call("guanfu_claim_task", {"taskKey": task["key"]})
            expect(claim.get("ok") is True, "单项目凭证可直接按编号认领本机开发任务")

            client_env = {key: value for key, value in os.environ.items() if key not in {
                "GUANFU_TOKEN", "GUANFU_HUMAN_TOKEN", "WORKBENCH_TOKEN", "WORKBENCH_HUMAN_TOKEN",
                "GUANFU_URL", "WORKBENCH_URL",
            }}
            client_env.update(GUANFU_HOME=str(root / "dev-data"), GUANFU_CLI_HOME=str(root / "cli"))

            def cli(*parameters: str, stdin: str | None = None) -> subprocess.CompletedProcess:
                return subprocess.run(
                    [args.dotnet, args.cli, *parameters],
                    env=client_env, cwd=checkout, input=stdin, capture_output=True, text=True, timeout=20)

            unconfigured = cli("whoami")
            expect(unconfigured.returncode != 0 and "尚未配置观复服务" in unconfigured.stderr,
                   "未配置服务时 CLI 拒绝联机")
            expect(cli("configure", "--url", url).returncode == 0, "CLI 保存默认服务地址")
            expect(cli("login", "dev", stdin="dev-e2e-pass\n").returncode == 0, "开发者 CLI 使用成员账号登录")
            expect("登录名 dev" in cli("whoami").stdout, "CLI 从已配置服务器恢复登录身份")

            seen: list[tuple[str, bool, bool]] = []

            class OtherService(BaseHTTPRequestHandler):
                def do_GET(self):
                    seen.append((self.path, "Cookie" in self.headers, "X-Guanfu-Human-Token" in self.headers))
                    if self.path.startswith("/redirect/"):
                        self.send_response(302)
                        self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/redirect-target")
                        self.end_headers()
                        return
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.end_headers()
                    self.wfile.write(b'{"member": null}')

                def log_message(self, *args):
                    pass

            with ThreadingHTTPServer(("127.0.0.1", 0), OtherService) as alternate:
                worker = Thread(target=alternate.serve_forever, daemon=True)
                worker.start()
                try:
                    other_url = f"http://127.0.0.1:{alternate.server_port}"
                    data = Path(client_env["GUANFU_HOME"])
                    data.mkdir()
                    (data / ".human-token").write_text("local-fixture-token")
                    cli("whoami", "--url", other_url)
                    expect(seen == [("/api/auth/me", False, False)],
                           "切换服务时不发送旧 Cookie 或本机人工凭证")
                    redirected = cli("whoami", "--url", other_url + "/redirect", "--token", "fixture-token")
                    expect(redirected.returncode != 0 and seen[-1] == ("/redirect/api/auth/me", False, True)
                           and not any(path == "/redirect-target" for path, _, _ in seen),
                           "带凭证的 CLI 请求不跟随服务重定向")
                finally:
                    alternate.shutdown()
                    worker.join(timeout=5)

            expect("登录名 dev" in cli("whoami").stdout, "切回原服务仍保留原成员会话")
            bridge_request = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2025-06-18", "capabilities": {},
                "clientInfo": {"name": "team-e2e", "version": "1"},
            }}) + "\n"
            bridge = cli("--token", token, "mcp", stdin=bridge_request)
            expect(bridge.returncode == 0 and "serverInfo" in bridge.stdout,
                   "开发机 stdio 桥接用自己的凭证连 HTTP 服务")
            uploaded = cli("--token", token, "report", "artifact", task["id"],
                           "--file", str(report), "--type", "TestReport")
            expect(uploaded.returncode == 0 and "测试报告.md" in uploaded.stdout, "CLI 上报开发机本地报告")
            for status in ["DocsReady", "Planned", "InDev", "Testing", "Review", "Done"]:
                result = agent.call("guanfu_update_status", {"taskId": task["id"], "status": status})
                expect(result.get("task", result).get("status") == status, f"agent 回写状态 {status}")

            login, _, cookie = rest_login(url, "pm", "pm-e2e-pass")
            expect(login == 200 and cookie is not None, "项管使用独立管理员账号登录")
            accepted, signed = rest_with_cookie(url, "POST", f"/api/milestones/{release}/accept", cookie, {})
            expect(accepted == 200 and json.loads(signed)["acceptedBy"] == "pm",
                   "项管账号验收迭代版本并留下本人签字")
            status, body = rest_with_cookie(url, "GET", f"/api/milestones/{release}/delivery/candidates", cookie)
            assert status == 200
            artifact = next(item for item in json.loads(body)["candidates"] if item["title"] == report.name)
            status, body = rest_with_cookie(url, "POST", f"/api/milestones/{release}/delivery/packages",
                                            cookie, {"itemIds": [artifact["itemId"]]})
            assert status == 201
            package = json.loads(body)
            status, body = rest_with_cookie(url, "POST", f"/api/delivery-packages/{package['id']}/share", cookie, {})
            assert status == 200
            share_path = "/delivery/" + json.loads(body)["token"]
            status, zipped = rest_bytes(url, share_path)
            with ZipFile(io.BytesIO(zipped)) as archive:
                expect(status == 200 and any(n.endswith(report.name) for n in archive.namelist()),
                       "分享链接可下载包含开发报告的 ZIP")

            status, _, dev_cookie = rest_login(url, "dev", "dev-e2e-pass")
            assert status == 200
            rest_json(url, "PUT", f"/api/members/{developer}/status", {"disabled": True})
            status, _ = rest_with_cookie(url, "GET", "/api/projects", dev_cookie)
            expect(status == 401, "停用成员立即拦下旧会话")
            try:
                agent.call("guanfu_list_projects", {})
                expect(False, "停用成员立即吊销名下 MCP 凭证")
            except urllib.error.HTTPError as error:
                expect(error.code == 401, "停用成员立即吊销名下 MCP 凭证")

            rest_json(url, "POST", "/api/workspace/export", {"targetDirectory": str(backup)})
            expect(any((backup / "packages").rglob("*.zip")), "工作区备份包含实际的交付 ZIP")
            imported = cli("workspace", "import", str(backup), "--home", str(restored))
            expect(imported.returncode == 0, "开发工具可在服务启动前导入工作区备份")
        finally:
            stop(server)

        port = free_port()
        restored_url = f"http://127.0.0.1:{port}"
        server = start_server(args.dotnet, args.root, str(restored), port)
        try:
            status, restored_zip = rest_bytes(restored_url, share_path)
            expect(status == 200 and restored_zip == zipped, "从备份启动后原分享链接仍可下载同一 ZIP")
            os.environ["WB_HUMAN_TOKEN"] = (restored / ".human-token").read_text().strip()
            project = rest_json(restored_url, "POST", "/api/projects", {"name": "恢复后可写"})
            expect(bool(project["id"]), "从 SQLite 快照恢复后服务可写")
        finally:
            stop(server)

    if FAILURES:
        return 1
    print("✅ 全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
