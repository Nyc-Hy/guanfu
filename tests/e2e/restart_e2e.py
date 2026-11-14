#!/usr/bin/env python3
"""链路 G：重启对账 —— 崩在执行中间的写操作，结果要标成「未知」。

服务崩在写操作执行中间时，那次写操作**可能已经生效**了 ——
比如 commit 已经写进仓库，只是执行方没来得及回写状态。
重启后系统判不出来，就别假装判得出来：把它标成「结果未知」交人工确认，
而不是悄悄重跑一遍（重跑可能把同一个写操作做两遍）。

这条链路真的起服务、崩服务、再起一次：
  1. 起服务 A，造一条要跑很久的命令（sleep），让它执行起来
  2. 执行途中**杀进程**（模拟崩溃，不是优雅退出）—— 审批停在 Executing
  3. 用同一个 WORKBENCH_HOME 起服务 B
  4. 那条审批应当是「过期 + 结果未知」，人一眼能看出来

用法:
    python3 tests/e2e/restart_e2e.py --dotnet /path/to/dotnet
"""
from __future__ import annotations

import argparse
import json
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

# 崩溃前给执行留的时间。够 shell_run 把审批占住（Approved → Executing）就行。
EXECUTE_SECONDS = 4
# 那条「要跑很久」的命令。秒数要够长，保证我们杀进程时它还在跑。
LONG_COMMAND = "sleep 45"


def start_server(dotnet: str, root: str, home: str, port: int) -> subprocess.Popen:
    """起一个工作台进程，等它真的能服务了再返回。"""
    env = dict(os.environ)
    env["WORKBENCH_HOME"] = home

    # 直接跑编译好的 dll，而不是 dotnet run ——
    # dotnet run 会再 fork 一个子进程，杀父进程杀不掉真在干活的那个。
    dll = os.path.join(root, "src/Workbench.Web/bin/Debug/net8.0/Workbench.Web.dll")
    proc = subprocess.Popen(
        [dotnet, dll, "--urls", f"http://127.0.0.1:{port}"],
        env=env,
        cwd=os.path.join(root, "src/Workbench.Web"),
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
    那条 sleep 就跑完了、审批也正常收尾，测的就不是「崩溃」了。
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
    repo = tempfile.mkdtemp(prefix="wb-e2e-restart-repo.")
    port = args.port or free_port()
    url = f"http://127.0.0.1:{port}"

    server: subprocess.Popen | None = None

    try:
        # ---- 1 起服务 A ----
        section("链路 G：重启对账（执行中断 → 结果未知）")

        build = subprocess.run(
            [args.dotnet, "build", os.path.join(root, "src/Workbench.Web"), "-v", "quiet", "--nologo"],
            capture_output=True, text=True, timeout=300)
        if build.returncode != 0:
            print(f"❌ 编译失败:\n{build.stdout}\n{build.stderr}")
            return 2

        server = start_server(args.dotnet, root, home, port)

        # 人工凭证：改项目配置、裁决审批都要它
        with open(os.path.join(home, ".human-token"), encoding="utf-8") as handle:
            os.environ["WB_HUMAN_TOKEN"] = handle.read().strip()

        project = rest_json(url, "POST", "/api/projects",
                            {"name": "重启对账", "description": "链路 G", "repoPath": repo})
        project_id = project["id"]

        # 白名单放行 sleep —— shell_run 不走 shell 解析，命令被拆成可执行文件 + 参数
        rest_json(url, "PUT", f"/api/projects/{project_id}/config",
                  {"allowedCommands": ["sleep"], "repoPath": repo})

        mcp = Mcp(url)
        mcp.initialize()

        # ---- 2 申请一条会跑很久的命令的审批 ----
        pending = mcp.call("shell_run", {
            "projectId": project_id,
            "command": LONG_COMMAND,
            "waitSeconds": 1,
            "callerId": "e2e-agent",
        })
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
                mcp.call("shell_run", {
                    "projectId": project_id,
                    "command": LONG_COMMAND,
                    "approvalId": approval_id,
                    "waitSeconds": 1,
                    "callerId": "e2e-agent",
                })
            except Exception:
                # 服务被杀了，连接断开是预期内的
                pass

        worker = threading.Thread(target=execute, daemon=True)
        worker.start()

        # 等它把审批占住（Approved → Executing）再杀进程
        time.sleep(EXECUTE_SECONDS)

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
        shutil.rmtree(repo, ignore_errors=True)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001
        print(f"❌ 重启对账链路执行异常: {error}")
        sys.exit(2)
