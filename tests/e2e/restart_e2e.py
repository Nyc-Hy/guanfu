#!/usr/bin/env python3
"""链路 G：数据库写入在执行中断后，服务重启并标记结果未知。

本机 TCP 测试连接只接收连接、不完成握手。确认真实 SQL 执行器进入连接阶段
后终止服务，再用同一数据目录启动，验证审批对账。
"""
from __future__ import annotations

import argparse
import os
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mcp_e2e import Mcp, expect, section, rest_json, FAILURES  # noqa: E402

from approval_e2e import create_fixture_connection  # noqa: E402

SQL = "UPDATE dbo.RestartProbe SET Id = 1"


def start_server(dotnet: str, root: str, home: str, port: int) -> subprocess.Popen:
    """起一个观复进程，等它真的能服务了再返回。"""
    env = dict(os.environ)
    env["GUANFU_HOME"] = home

    # 直接跑编译好的 dll，而不是 dotnet run ——
    # dotnet run 会再 fork 一个子进程，杀父进程杀不掉真在干活的那个。
    dll = os.path.join(root, "src/Guanfu.Web/bin/Debug/net8.0/Guanfu.Web.dll")
    proc = subprocess.Popen(
        [dotnet, dll, "--urls", f"http://127.0.0.1:{port}"],
        env=env,
        cwd=os.path.join(root, "src/Guanfu.Web"),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    deadline = time.time() + 60
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"服务进程退出了（退出码 {proc.returncode}）")

        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as response:
                if response.status == 200:
                    return proc
        except Exception:
            pass

        time.sleep(0.5)

    proc.kill()
    raise RuntimeError("服务 60 秒内没起来")


def kill_hard(proc: subprocess.Popen) -> None:
    """模拟崩溃：SIGKILL，不给收尾的机会。

    优雅退出（SIGTERM）会让 ASP.NET Core 等在途请求跑完 ——
    数据库连接可能超时并触发正常收尾，这条链路需要模拟进程崩溃。
    """
    try:
        os.kill(proc.pid, signal.SIGKILL)
    except ProcessLookupError:
        return

    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        pass


def free_port() -> int:
    """找一个空闲端口 —— 这条链路自己起服务，别跟总跑批的端口撞上。"""
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True, help="dotnet 可执行文件路径")
    parser.add_argument("--port", type=int, default=0, help="0 表示自动挑一个空闲端口")
    parser.add_argument("--root", default=os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
    args = parser.parse_args()

    root = args.root
    home = tempfile.mkdtemp(prefix="wb-e2e-restart-home.")
    port = args.port or free_port()
    url = f"http://127.0.0.1:{port}"

    server: subprocess.Popen | None = None
    database = socket.socket()
    database.bind(("127.0.0.1", 0))
    database.listen()
    database.settimeout(10)

    try:
        # ---- 1 起服务 A ----
        section("链路 G：重启对账（执行中断 → 结果未知）")

        build = subprocess.run(
            [args.dotnet, "build", os.path.join(root, "src/Guanfu.Web"), "-v", "quiet", "--nologo"],
            capture_output=True, text=True, timeout=300)
        if build.returncode != 0:
            print(f"❌ 编译失败:\n{build.stdout}\n{build.stderr}")
            return 2

        server = start_server(args.dotnet, root, home, port)

        # 人工凭证：改项目配置、裁决审批都要它
        with open(os.path.join(home, ".human-token"), encoding="utf-8") as handle:
            os.environ["WB_HUMAN_TOKEN"] = handle.read().strip()

        project = rest_json(url, "POST", "/api/projects", {"name": "重启对账"})
        project_id = project["id"]
        create_fixture_connection(url, project_id, f"127.0.0.1,{database.getsockname()[1]}")

        mcp = Mcp(url)
        mcp.initialize()
        pending = mcp.call("db_execute", {"projectId": project_id, "sql": SQL, "waitSeconds": 1})
        approval_id = pending.get("approvalId") or ""
        expect(bool(approval_id), f"应造出一条待审审批（返回：{pending}）")

        if not approval_id:
            return 1

        decided = rest_json(url, "POST", f"/api/approvals/{approval_id}/decide",
                            {"approve": True, "decidedBy": "e2e-human", "reason": "可以跑"})
        expect(decided.get("status") == "Approved", f"审批应已批准（实际 {decided.get('status')}）")

        # ---- 3 执行途中把服务杀了 ----
        def execute() -> None:
            try:
                mcp.call("db_execute", {
                    "projectId": project_id,
                    "sql": SQL,
                    "approvalId": approval_id,
                    "waitSeconds": 1,
                    "callerId": "e2e-agent",
                })
            except Exception:
                # 服务被杀了，连接断开是预期内的
                pass

        worker = threading.Thread(target=execute, daemon=True)
        worker.start()

        # 有 TCP 连接进入才继续，说明真实执行器已开始连接数据库。
        peer, _ = database.accept()
        with peer:
            before = rest_json(url, "GET", "/api/approvals")
            executing = [a for a in before if a.get("id") == approval_id]
            expect(
                bool(executing) and executing[0].get("status") == "Executing",
                f"杀进程前审批应停在执行中（实际：{executing[0].get('status') if executing else '没找到'}）",
            )
            expect(
                bool(executing) and not executing[0].get("outcomeUnknown"),
                "还没重启，结果不该是未知的",
            )

            kill_hard(server)
        server = None

        # ---- 4 用同一个 WORKBENCH_HOME 再起一次 ----
        server = start_server(args.dotnet, root, home, port)

        after = rest_json(url, "GET", "/api/approvals")
        row = next((a for a in after if a.get("id") == approval_id), None)

        expect(row is not None, "重启后应还能查到那条审批")
        if row is not None:
            expect(
                row.get("status") == "Expired",
                f"被中断的审批应过期（实际 {row.get('status')}）",
            )
            expect(
                row.get("outcomeUnknown") is True,
                "被中断的审批应标成结果未知 —— 那次写操作可能已经生效了",
            )
            expect(
                "结果未知" in (row.get("decisionReason") or ""),
                f"理由要说清结果未知（实际：{row.get('decisionReason')}）",
            )

        section("结果")
        if FAILURES:
            print(f"❌ 失败 {len(FAILURES)} 项:")
            for failure in FAILURES:
                print(f"  - {failure}")
            return 1

        print("✅ 全部通过")
        return 0
    finally:
        if server is not None:
            kill_hard(server)
        shutil.rmtree(home, ignore_errors=True)
        database.close()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001
        print(f"❌ 重启对账链路执行异常: {error}")
        sys.exit(2)
