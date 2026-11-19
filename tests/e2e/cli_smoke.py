#!/usr/bin/env python3
"""CLI（guanfu）冒烟 —— 次要入口也得能用，而且不能靠人工点。

CLI 在观复里是「次要入口」（主路径是 Web UI + MCP），但 `guanfu mcp` 的
stdio 桥接不可替代：只支持 stdio 的 MCP 客户端全靠它接进来。
所以这一份脚本守两件事：

  E1 常用命令都跑得通（项目 / 任务 / 计划 / 上下文 / 产物 / 测试结果 /
     事件流水 / 里程碑 / 审批 / 审计 / 数据库 / 工具清单 / 指标 / 评审）
  E2 敏感命令没凭证就是 401 —— 人工凭证这条门槛在 CLI 上同样生效
  E3 `guanfu mcp` 的 stdio 桥接能真的把 JSON-RPC 转发出去（不可替代的那条路）

用法:
    python3 tests/e2e/cli_smoke.py --url http://127.0.0.1:5191 \
        --dotnet /path/to/dotnet --cli /path/to/Guanfu.Cli.dll
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

from mcp_e2e import Mcp, expect, section, FAILURES, rest_json  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5191")
    parser.add_argument("--dotnet", required=True, help="dotnet 可执行文件路径")
    parser.add_argument("--cli", required=True, help="Guanfu.Cli.dll 路径")
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

    def guanfu(*cli_args: str, token: str | None = None, expect_code: int | None = 0) -> tuple[int, str]:
        """跑一条 guanfu 命令。

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
                f"guanfu {' '.join(cli_args[:3])} 退出码应为 {expect_code}（实际 {proc.returncode}）"
                f"{'' if proc.returncode == expect_code else '：' + output.strip()[:200]}",
            )

        return proc.returncode, output

    # ── E1 常用命令 ────────────────────────────────────────────────────
    section("链路 E：CLI（guanfu）常用命令")

    print("\n-- E1.1 用法与只读命令 --")
    code, out = guanfu("--help")
    expect("观复的命令行" in out, "guanfu --help 应打出用法")

    code, out = guanfu("project", "list")
    expect("ID" in out or "还没有项目" in out, "guanfu project list 应列出项目")

    code, out = guanfu("tool", "list")
    expect("开启" in out, "guanfu tool list 应报出工具开关情况")

    print("\n-- E1.2 建项目与任务 --")
    code, out = guanfu("project", "add", f"CLI 冒烟 {suffix}")
    project_id = ""
    found = re.search(r"已创建项目\s+([0-9a-fA-F-]{36})", out)
    if found:
        project_id = found.group(1)
    expect(bool(project_id), f"guanfu project add 应回显项目 ID（输出：{out.strip()[:160]}）")

    code, out = guanfu("project", "show", project_id)
    expect(f"CLI 冒烟 {suffix}" in out, "guanfu project show 应回显项目名")

    code, out = guanfu("task", "create", project_id, "CLI 冒烟任务", "--type", "requirement")
    task_id = ""
    task_key = ""
    found = re.search(r"已创建任务\s+([0-9a-fA-F-]{36})\s+·\s+(\S+)", out)
    if found:
        task_id = found.group(1)
        task_key = found.group(2)
    expect(bool(task_id), f"guanfu task create 应回显任务 ID 与编号（输出：{out.strip()[:160]}）")
    expect(task_key.startswith("REQ-"), f"需求任务的编号应是 REQ- 前缀（实际 {task_key}）")

    code, out = guanfu("task", "list", "--project", project_id)
    expect(task_key in out, "guanfu task list 应能按项目过滤出任务")

    code, out = guanfu("task", "show", task_id)
    expect("CLI 冒烟任务" in out, "guanfu task show <ID> 应回显任务")

    code, out = guanfu("task", "show", task_key, "--project", project_id)
    expect("CLI 冒烟任务" in out, "guanfu task show <编号> 应能按编号找到同一个任务")

    print("\n-- E1.3 状态、计划与上下文 --")
    code, out = guanfu("status", "set", task_id, "DocsReady", "--note", "e2e 冒烟")
    expect("文档就绪" in out, "guanfu status set 应推进状态（输出中文状态）")

    # 中文状态也要认：人敲命令用中文，agent 用枚举名，两种都得能落地
    code, out = guanfu("task", "create", project_id, "CLI 中文状态任务", "--type", "requirement")
    cn_task_id = ""
    found = re.search(r"已创建任务\s+([0-9a-fA-F-]{36})", out)
    if found:
        cn_task_id = found.group(1)
    code, out = guanfu("status", "set", cn_task_id, "文档就绪", "--note", "e2e 中文状态")
    expect("文档就绪" in out, "guanfu status set 也该认中文状态")

    code, out = guanfu(
        "task", "plan", task_id,
        "--background", "CLI 冒烟",
        "--understanding", "命令行也要能回写计划",
        "--breakdown", "1) 建任务 2) 回写计划",
        "--acceptance", "guanfu task plan 退出码为 0",
        "--author", "e2e",
    )
    expect(code == 0, "guanfu task plan 应成功")

    code, out = guanfu("task", "context", task_id)
    expect("CLI 冒烟任务" in out, "guanfu task context 应能取到一次性上下文包")

    print("\n-- E1.4 产物、测试结果、事件流水 --")
    code, out = guanfu("task", "artifact", task_id, "CodeChange", "CLI 冒烟产物",
                   "--content", "改了三个文件", "--author", "e2e")
    expect(code == 0, "guanfu task artifact 应成功")

    print("\n-- E1.4b 批量导入文档 --")
    with tempfile.TemporaryDirectory(prefix="wb-e2e-docs-") as doc_dir:
        doc_a = os.path.join(doc_dir, "PRD-需求.md")
        doc_b = os.path.join(doc_dir, "接口文档.md")
        with open(doc_a, "w", encoding="utf-8") as handle:
            handle.write("# 需求\n登录要账号密码")
        with open(doc_b, "w", encoding="utf-8") as handle:
            handle.write("# 接口\nPOST /api/login")

        code, out = guanfu("doc", "upload", task_id, doc_a, doc_b)
        expect(code == 0, f"guanfu doc upload 多个文件应成功（输出：{out.strip()[:200]}）")
        expect("2" in out, f"应报出上传了几个文件（输出：{out.strip()[:200]}）")

    code, out = guanfu("task", "test-result", task_id, "--total", "10", "--passed", "9",
                   "--failures", "用例A", "--coverage", "0.9", "--author", "e2e")
    expect(code == 0, "guanfu task test-result 应成功")

    code, out = guanfu("task", "events", task_id, "--format", "markdown")
    expect(code == 0, "guanfu task events 应能导出事件流水")

    code, out = guanfu("task", "events", task_id, "--format", "json")
    expect(code == 0, "guanfu task events --format json 应能导出事件流水")

    print("\n-- E1.4c 从 shell 调 MCP 工具（guanfu call）与本地文件报入 --")
    code, out = guanfu("tools")
    expect(code == 0, "guanfu tools 应能列出观复的工具")
    expect("guanfu_add_artifact" in out, "工具清单里该有产物登记工具")

    code, out = guanfu("call", "guanfu_get_task_by_key", "--arg", "key=REQ-001",
                        "--arg", f"projectId={project_id}")
    expect(code == 0, f"guanfu call 应能调 MCP 工具（输出：{out.strip()[:200]}）")
    expect("CLI 冒烟任务" in out, f"guanfu call 的结果里该有那个任务（输出：{out.strip()[:200]}）")

    with tempfile.TemporaryDirectory(prefix="wb-e2e-local-") as local_dir:
        local_file = os.path.join(local_dir, "本地脚本.sql")
        with open(local_file, "w", encoding="utf-8") as handle:
            handle.write("SELECT 1;")

        code, out = guanfu("report", "artifact", task_id, "--file", local_file,
                            "--type", "SqlScript", "--title", "本地脚本.sql")
        expect(code == 0, f"guanfu report artifact 应成功（输出：{out.strip()[:200]}）")
        expect("本地脚本.sql" in out, f"登记结果里该有那个产物（输出：{out.strip()[:200]}）")

    print("\n-- E1.4d 命令行的账号验证（以成员身份说话）--")
    member_user = f"e2e-cli-{suffix}"
    member_password = "e2e-cli-password"

    created = rest_json(args.url, "POST", "/api/members", {
        "userName": member_user,
        "displayName": "命令行成员",
        "password": member_password,
        "role": "Member",
    })
    expect(created is not None, "该能建一个成员（给命令行用）")

    code, out = guanfu("login", member_user, "--password", member_password)
    expect(code == 0, f"guanfu login 应成功（输出：{out.strip()[:200]}）")

    code, out = guanfu("whoami")
    expect(code == 0, "guanfu whoami 应成功")
    expect(member_user in out, f"whoami 该报出当前身份（输出：{out.strip()[:200]}）")

    code, out = guanfu("logout")
    expect(code == 0, "guanfu logout 应成功")

    print("\n-- E1.5 认领、依赖、评审 --")
    code, out = guanfu("task", "claim", task_id, "--agent", "e2e-agent", "--lease", "30")
    expect("已认领" in out, "guanfu task claim 应成功")

    code, out = guanfu("task", "deps", task_id)
    expect(code == 0, "guanfu task deps 应能列出依赖")

    code, out = guanfu("milestone", "list", project_id)
    expect(code == 0, "guanfu milestone list 应成功")

    # 评审：先把任务推到 Review 再开轮次
    for target in ["Planned", "InDev", "Testing", "Review"]:
        code, out = guanfu("status", "set", task_id, target)
        expect(code == 0, f"guanfu status set {target} 应成功")

    code, out = guanfu("review", "start", task_id)
    expect("7 个维度" in out, "guanfu review start 应开出 7 个维度")

    code, out = guanfu("review", "submit", task_id, "security", "--pass",
                   "--findings", "无注入风险", "--evidence", "看了实现")
    expect(code == 0, "guanfu review submit 应成功")

    code, out = guanfu("review", "list", task_id)
    expect("第 1 轮" in out, "guanfu review list 应回显轮次")

    code, out = guanfu("review", "export", task_id, "--format", "markdown")
    expect(code == 0, "guanfu review export 应能导出评审报告")

    print("\n-- E1.6 审批、审计、数据库、指标 --")
    code, out = guanfu("approval", "list")
    expect(code == 0, "guanfu approval list 应成功")

    code, out = guanfu("audit", "list", "--project", project_id, "--limit", "20")
    expect(code == 0, "guanfu audit list 应能查工具调用审计")

    code, out = guanfu("db", "connections", project_id)
    expect(code == 0, "guanfu db connections 应能列连接")

    code, out = guanfu("metrics", project_id)
    expect(code == 0, "guanfu metrics 应成功")

    # ── E2 敏感命令的凭证门槛 ─────────────────────────────────────────
    print("\n-- E2 敏感命令没凭证就是 401 --")
    code, out = guanfu("project", "config", project_id, "--allow", "touch", token="",
                   expect_code=2)
    expect("401" in out, f"没凭证改项目配置应报 401（输出：{out.strip()[:160]}）")

    # 建项目同样要凭证 —— 项目一创建就把一个文件系统根目录授给了它，
    # 不设门的话 agent 自己建个项目指向 /etc 就能把文件读出去。
    code, out = guanfu("project", "add", "无凭证建的项目", token="", expect_code=2)
    expect("401" in out, f"没凭证建项目应报 401（输出：{out.strip()[:160]}）")

    # 带凭证要能改 —— 而且要顺手把仓库路径与白名单配好，
    # 后面的 shell_run 要靠它造出待审审批。
    repo_dir = tempfile.mkdtemp(prefix="wb-e2e-cli-repo-")
    try:
        code, out = guanfu("project", "config", project_id, "--repo", repo_dir, "--allow", "touch")
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
            code, out = guanfu("approval", "approve", approval_id, "--by", "e2e-human")
            expect("已批准" in out, "guanfu approval approve 应成功")

            code, out = guanfu("approval", "list")
            expect(code == 0, "guanfu approval list 在裁决后仍应成功")
    finally:
        shutil.rmtree(repo_dir, ignore_errors=True)

    print("\n-- E2.3 工作区导出（换机要带走的东西） --")
    export_dir = tempfile.mkdtemp(prefix="wb-e2e-export-")
    try:
        code, out = guanfu("workspace", "export", export_dir)
        expect(code == 0, "guanfu workspace export 应成功")
        expect(
            any(name.endswith(".db") for name in os.listdir(export_dir)),
            f"导出的工作区里应有数据库文件（实际：{os.listdir(export_dir)}）",
        )
    finally:
        shutil.rmtree(export_dir, ignore_errors=True)

    # ── E2.4 人工凭证 ──────────────────────────────────────────────────
    print("\n-- E2.4 人工凭证（远程访问网页时粘贴用） --")
    code, out = guanfu("token")
    expect(code == 0, "guanfu token 应成功")
    # 不把凭证本身写进断言消息里，只看它是不是一串非空的东西
    expect(len(out.strip()) >= 10, "guanfu token 应打印出一串人工凭证")

    # ── E3 guanfu mcp 的 stdio 桥接 ───────────────────────────────────────
    print("\n-- E3 guanfu mcp 的 stdio 桥接 --")
    expect(bridge_roundtrip(args), "guanfu mcp 应能把 JSON-RPC 转发到观复并回结果")

    section("结果")
    if FAILURES:
        print(f"❌ 失败 {len(FAILURES)} 项:")
        for failure in FAILURES:
            print(f"  - {failure}")
        return 1

    print("✅ 全部通过")
    return 0


def bridge_roundtrip(args) -> bool:
    """跑一次 guanfu mcp：喂一条 initialize，读回一条 JSON-RPC 响应。"""
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
                expect(False, f"guanfu mcp 的 stdout 混进了非 JSON 内容：{raw[:160]}")
                return False

            line = raw
            break

        if not line:
            expect(False, "guanfu mcp 没有在 30 秒内回出响应")
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
                expect(False, f"guanfu mcp 的 stdout 混进了非 JSON 内容：{raw[:160]}")
                return False

            if payload.get("id") == 2:
                tools_line = raw
                break

        if not tools_line:
            expect(False, "guanfu mcp 的 tools/list 没有回包")
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
