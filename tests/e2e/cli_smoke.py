#!/usr/bin/env python3
"""CLI（wb）冒烟 —— 次要入口也得能用，而且不能靠人工点。

CLI 在工作台里是「次要入口」（主路径是 Web UI + MCP），但 `wb mcp` 的
stdio 桥接不可替代：只支持 stdio 的 MCP 客户端全靠它接进来。
所以这一份脚本守两件事：

  E1 常用命令都跑得通（项目 / 任务 / 计划 / 上下文 / 产物 / 测试结果 /
     事件流水 / 里程碑 / 审批 / 审计 / 数据库 / 工具清单 / 指标 / 评审）
  E2 敏感命令没凭证就是 401 —— 人工凭证这条门槛在 CLI 上同样生效
  E3 `wb mcp` 的 stdio 桥接能真的把 JSON-RPC 转发出去（不可替代的那条路）

用法:
    python3 tests/e2e/cli_smoke.py --url http://127.0.0.1:5191 \
        --dotnet /path/to/dotnet --cli /path/to/Workbench.Cli.dll
"""
from __future__ import annotations

import argparse
import json
import os
import re
import select
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mcp_e2e import Mcp, expect, section, FAILURES  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5191")
    parser.add_argument("--dotnet", required=True, help="dotnet 可执行文件路径")
    parser.add_argument("--cli", required=True, help="Workbench.Cli.dll 路径")
    parser.add_argument("--token", default=os.environ.get("WB_HUMAN_TOKEN", ""))
    args = parser.parse_args()

    suffix = str(int(time.time()))[-6:]

    def env_with_token(token: str) -> dict:
        env = dict(os.environ)
        if token:
            env["WORKBENCH_HUMAN_TOKEN"] = token
        else:
            # 没凭证的跑法：既不给环境变量，也不让 CLI 从 WORKBENCH_HOME 里捡到 token 文件。
            env.pop("WORKBENCH_HUMAN_TOKEN", None)
            env["WORKBENCH_HOME"] = tempfile.mkdtemp(prefix="wb-e2e-no-token-")
        return env

    def wb(*cli_args: str, token: str | None = None, expect_code: int | None = 0) -> tuple[int, str]:
        """跑一条 wb 命令。

        token=None（默认）→ 用命令行给的凭证；token="" → 明确不给凭证。
        """
        use_token = args.token if token is None else token
        command = [args.dotnet, args.cli, "--url", args.url, *cli_args]
        proc = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=180,
            env=env_with_token(use_token),
        )
        output = (proc.stdout or "") + (proc.stderr or "")

        if expect_code is not None:
            expect(
                proc.returncode == expect_code,
                f"wb {' '.join(cli_args[:3])} 退出码应为 {expect_code}（实际 {proc.returncode}）"
                f"{'' if proc.returncode == expect_code else '：' + output.strip()[:200]}",
            )

        return proc.returncode, output

    # ── E1 常用命令 ────────────────────────────────────────────────────
    section("链路 E：CLI（wb）常用命令")

    print("\n-- E1.1 用法与只读命令 --")
    code, out = wb("--help")
    expect("开发工作台命令行" in out, "wb --help 应打出用法")

    code, out = wb("project", "list")
    expect("ID" in out or "还没有项目" in out, "wb project list 应列出项目")

    code, out = wb("tool", "list")
    expect("开启" in out, "wb tool list 应报出工具开关情况")

    print("\n-- E1.2 建项目与任务 --")
    code, out = wb("project", "add", f"CLI 冒烟 {suffix}")
    project_id = ""
    found = re.search(r"已创建项目\s+([0-9a-fA-F-]{36})", out)
    if found:
        project_id = found.group(1)
    expect(bool(project_id), f"wb project add 应回显项目 ID（输出：{out.strip()[:160]}）")

    code, out = wb("project", "show", project_id)
    expect(f"CLI 冒烟 {suffix}" in out, "wb project show 应回显项目名")

    code, out = wb("task", "create", project_id, "CLI 冒烟任务", "--type", "requirement")
    task_id = ""
    task_key = ""
    found = re.search(r"已创建任务\s+([0-9a-fA-F-]{36})\s+·\s+(\S+)", out)
    if found:
        task_id = found.group(1)
        task_key = found.group(2)
    expect(bool(task_id), f"wb task create 应回显任务 ID 与编号（输出：{out.strip()[:160]}）")
    expect(task_key.startswith("REQ-"), f"需求任务的编号应是 REQ- 前缀（实际 {task_key}）")

    code, out = wb("task", "list", "--project", project_id)
    expect(task_key in out, "wb task list 应能按项目过滤出任务")

    code, out = wb("task", "show", task_id)
    expect("CLI 冒烟任务" in out, "wb task show <ID> 应回显任务")

    code, out = wb("task", "show", task_key, "--project", project_id)
    expect("CLI 冒烟任务" in out, "wb task show <编号> 应能按编号找到同一个任务")

    print("\n-- E1.3 状态、计划与上下文 --")
    code, out = wb("status", "set", task_id, "DocsReady", "--note", "e2e 冒烟")
    expect("文档就绪" in out, "wb status set 应推进状态（输出中文状态）")

    # 中文状态也要认：人敲命令用中文，agent 用枚举名，两种都得能落地
    code, out = wb("task", "create", project_id, "CLI 中文状态任务", "--type", "requirement")
    cn_task_id = ""
    found = re.search(r"已创建任务\s+([0-9a-fA-F-]{36})", out)
    if found:
        cn_task_id = found.group(1)
    code, out = wb("status", "set", cn_task_id, "文档就绪", "--note", "e2e 中文状态")
    expect("文档就绪" in out, "wb status set 也该认中文状态")

    code, out = wb(
        "task", "plan", task_id,
        "--background", "CLI 冒烟",
        "--understanding", "命令行也要能回写计划",
        "--breakdown", "1) 建任务 2) 回写计划",
        "--acceptance", "wb task plan 退出码为 0",
        "--author", "e2e",
    )
    expect(code == 0, "wb task plan 应成功")

    code, out = wb("task", "context", task_id)
    expect("CLI 冒烟任务" in out, "wb task context 应能取到一次性上下文包")

    print("\n-- E1.4 产物、测试结果、事件流水 --")
    code, out = wb("task", "artifact", task_id, "CodeChange", "CLI 冒烟产物",
                   "--content", "改了三个文件", "--author", "e2e")
    expect(code == 0, "wb task artifact 应成功")

    code, out = wb("task", "test-result", task_id, "--total", "10", "--passed", "9",
                   "--failures", "用例A", "--coverage", "0.9", "--author", "e2e")
    expect(code == 0, "wb task test-result 应成功")

    code, out = wb("task", "events", task_id, "--format", "markdown")
    expect(code == 0, "wb task events 应能导出事件流水")

    code, out = wb("task", "events", task_id, "--format", "json")
    expect(code == 0, "wb task events --format json 应能导出事件流水")

    print("\n-- E1.5 认领、依赖、评审 --")
    code, out = wb("task", "claim", task_id, "--agent", "e2e-agent", "--lease", "30")
    expect("已认领" in out, "wb task claim 应成功")

    code, out = wb("task", "deps", task_id)
    expect(code == 0, "wb task deps 应能列出依赖")

    code, out = wb("milestone", "list", project_id)
    expect(code == 0, "wb milestone list 应成功")

    # 评审：先把任务推到 Review 再开轮次
    for target in ["Planned", "InDev", "Testing", "Review"]:
        code, out = wb("status", "set", task_id, target)
        expect(code == 0, f"wb status set {target} 应成功")

    code, out = wb("review", "start", task_id)
    expect("7 个维度" in out, "wb review start 应开出 7 个维度")

    code, out = wb("review", "submit", task_id, "security", "--pass",
                   "--findings", "无注入风险", "--evidence", "看了实现")
    expect(code == 0, "wb review submit 应成功")

    code, out = wb("review", "list", task_id)
    expect("第 1 轮" in out, "wb review list 应回显轮次")

    code, out = wb("review", "export", task_id, "--format", "markdown")
    expect(code == 0, "wb review export 应能导出评审报告")

    print("\n-- E1.6 审批、审计、数据库、指标 --")
    code, out = wb("approval", "list")
    expect(code == 0, "wb approval list 应成功")

    code, out = wb("audit", "list", "--project", project_id, "--limit", "20")
    expect(code == 0, "wb audit list 应能查工具调用审计")

    code, out = wb("db", "connections", project_id)
    expect(code == 0, "wb db connections 应能列连接")

    code, out = wb("metrics", project_id)
    expect(code == 0, "wb metrics 应成功")

    # ── E2 敏感命令的凭证门槛 ─────────────────────────────────────────
    print("\n-- E2 敏感命令没凭证就是 401 --")
    code, out = wb("project", "config", project_id, "--allow", "touch", token="",
                   expect_code=2)
    expect("401" in out, f"没凭证改项目配置应报 401（输出：{out.strip()[:160]}）")

    # 带凭证要能改 —— 而且要顺手把仓库路径与白名单配好，
    # 后面的 shell_run 要靠它造出待审审批。
    repo_dir = tempfile.mkdtemp(prefix="wb-e2e-cli-repo-")
    try:
        code, out = wb("project", "config", project_id, "--repo", repo_dir, "--allow", "touch")
        expect("已更新项目配置" in out, "带凭证改项目配置应成功")

        print("\n-- E2.2 人工裁决审批（CLI 这条路也要能批） --")
        # 用 MCP 造一条待审审批，再用 CLI 批 —— 验证 CLI 的敏感路径真的通。
        mcp = Mcp(args.url)
        mcp.initialize()
        pending = mcp.call(
            "shell_run",
            {"projectId": project_id, "command": "touch cli-smoke-marker.txt", "waitSeconds": 1,
             "callerId": "e2e-agent"},
        )
        approval_id = pending.get("approvalId") or ""
        expect(bool(approval_id), f"应造出一条待审审批（返回：{pending}）")

        if approval_id:
            code, out = wb("approval", "approve", approval_id, "--by", "e2e-human")
            expect("已批准" in out, "wb approval approve 应成功")

            code, out = wb("approval", "list")
            expect(code == 0, "wb approval list 在裁决后仍应成功")
    finally:
        shutil.rmtree(repo_dir, ignore_errors=True)

    print("\n-- E2.3 工作区导出（换机要带走的东西） --")
    export_dir = tempfile.mkdtemp(prefix="wb-e2e-export-")
    try:
        code, out = wb("workspace", "export", export_dir)
        expect(code == 0, "wb workspace export 应成功")
        expect(
            any(name.endswith(".db") for name in os.listdir(export_dir)),
            f"导出的工作区里应有数据库文件（实际：{os.listdir(export_dir)}）",
        )
    finally:
        shutil.rmtree(export_dir, ignore_errors=True)

    # ── E2.4 人工凭证 ──────────────────────────────────────────────────
    print("\n-- E2.4 人工凭证（远程访问网页时粘贴用） --")
    code, out = wb("token")
    expect(code == 0, "wb token 应成功")
    # 不把凭证本身写进断言消息里，只看它是不是一串非空的东西
    expect(len(out.strip()) >= 10, "wb token 应打印出一串人工凭证")

    # ── E3 wb mcp 的 stdio 桥接 ───────────────────────────────────────
    print("\n-- E3 wb mcp 的 stdio 桥接 --")
    expect(bridge_roundtrip(args), "wb mcp 应能把 JSON-RPC 转发到工作台并回结果")

    section("结果")
    if FAILURES:
        print(f"❌ 失败 {len(FAILURES)} 项:")
        for failure in FAILURES:
            print(f"  - {failure}")
        return 1

    print("✅ 全部通过")
    return 0


def bridge_roundtrip(args) -> bool:
    """跑一次 wb mcp：喂一条 initialize，读回一条 JSON-RPC 响应。"""
    command = [args.dotnet, args.cli, "--url", args.url, "mcp"]
    proc = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )

    try:
        request = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "cli-smoke", "version": "1"},
            },
        }
        proc.stdin.write(json.dumps(request) + "\n")
        proc.stdin.flush()

        # 最多等 30 秒读一行 —— 桥接不该卡住，但别把测试挂死。
        deadline = time.time() + 30
        line = ""
        while time.time() < deadline:
            ready, _, _ = select.select([proc.stdout], [], [], 1.0)
            if not ready:
                continue

            raw = proc.stdout.readline()
            if not raw:
                break

            raw = raw.strip()
            if not raw:
                continue

            try:
                payload = json.loads(raw)
            except json.JSONDecodeError:
                # 桥接的 stdout 只能有 JSON-RPC：出现别的就是流污染。
                expect(False, f"wb mcp 的 stdout 混进了非 JSON 内容：{raw[:160]}")
                return False

            line = raw
            break

        if not line:
            expect(False, "wb mcp 没有在 30 秒内回出响应")
            return False

        payload = json.loads(line)
        expect(payload.get("id") == 1, f"回应的 id 应与请求对上（实际 {payload.get('id')}）")
        expect("result" in payload, f"initialize 应回 result（实际 {payload}）")

        # 再来一条 tools/list —— 桥接的价值就在「把客户端的请求原样接进来」。
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}) + "\n")
        proc.stdin.flush()

        deadline = time.time() + 30
        tools_line = ""
        while time.time() < deadline:
            ready, _, _ = select.select([proc.stdout], [], [], 1.0)
            if not ready:
                continue

            raw = proc.stdout.readline()
            if not raw:
                break

            raw = raw.strip()
            if not raw:
                continue

            try:
                payload = json.loads(raw)
            except json.JSONDecodeError:
                expect(False, f"wb mcp 的 stdout 混进了非 JSON 内容：{raw[:160]}")
                return False

            if payload.get("id") == 2:
                tools_line = raw
                break

        if not tools_line:
            expect(False, "wb mcp 的 tools/list 没有回包")
            return False

        tools = json.loads(tools_line).get("result", {}).get("tools", [])
        expect(len(tools) > 0, f"tools/list 应列出工具（实际 {len(tools)} 个）")
        expect(
            all("_" in tool.get("name", "") for tool in tools),
            "工具名一律用下划线（规矩：MCP 客户端不接受点号）",
        )

        return True
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass

        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:  # noqa: BLE001
        print(f"❌ CLI 冒烟执行异常: {error}")
        sys.exit(2)
