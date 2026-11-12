#!/usr/bin/env python3
"""工作台 MCP 端到端验证。

模拟 agent 走完这句话对应的完整链路：
    「你看下 工作台中的当前项目 12月版本的 req001，做一下开发」

两条链路：
  A  正向：按编号找任务 → claim → 取上下文 → 回写计划 → 推进到 Review →
           开评审轮 → 取评审包 → 逐维度回写 7 条 → 结项
  B  拦截：只评 6 个维度就结项 / 有一个维度不通过就结项 —— 两种都必须被拒

不依赖任何外部状态：项目、里程碑、任务全部现建现用，跑完即弃。

用法:
    python3 tests/e2e/mcp_e2e.py --url http://127.0.0.1:5191
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

DIMENSIONS = [
    "code_quality",
    "comments",
    "security",
    "scope_impact",
    "missing_requirement",
    "over_implementation",
    "over_operation",
]

FAILURES: list[str] = []


class Mcp:
    """极简 MCP over Streamable HTTP 客户端（无 session id 的无状态模式）。"""

    def __init__(self, base_url: str):
        self.base = base_url.rstrip("/") + "/mcp"
        self._id = 0

    def _rpc(self, method: str, params=None, notify: bool = False):
        payload = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            payload["params"] = params
        if not notify:
            self._id += 1
            payload["id"] = self._id

        request = urllib.request.Request(
            self.base,
            data=json.dumps(payload).encode(),
            headers={
                "Content-Type": "application/json",
                "Accept": "application/json, text/event-stream",
            },
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read().decode()

        if notify:
            return None
        for line in raw.splitlines():
            if line.startswith("data:"):
                return json.loads(line[5:].strip())
        return json.loads(raw)

    def initialize(self):
        self._rpc(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "workbench-e2e", "version": "1"},
            },
        )
        self._rpc("notifications/initialized", {}, notify=True)

    def call(self, tool: str, args: dict):
        """返回工具结果。工具不抛异常，失败也有结构化的 ok/error。"""
        response = self._rpc("tools/call", {"name": tool, "arguments": args})
        if "error" in response:
            return {"ok": False, "error": f"RPC error: {response['error']}"}

        result = response.get("result", {})
        if result.get("isError"):
            texts = [c.get("text", "") for c in result.get("content", [])]
            return {"ok": False, "error": texts[0] if texts else "isError"}

        text = result.get("content", [{}])[0].get("text", "")
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return {"ok": False, "error": f"non-json response: {text[:200]}"}


def expect(condition: bool, label: str):
    print(f"  {'✓' if condition else '✗'} {label}")
    if not condition:
        FAILURES.append(label)


def section(title: str):
    print(f"\n{'=' * 60}\n{title}\n{'=' * 60}")


def rest_json(rest: str, method: str, path: str, body: dict | None = None):
    """建项目 / 建里程碑这类「改变工作台结构」的动作只走 REST —— MCP 刻意不暴露它们。"""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(
        rest.rstrip("/") + path,
        data=data,
        headers={"Content-Type": "application/json"},
        method=method,
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode())


def new_project(rest: str, suffix: str) -> tuple[str, str]:
    project = rest_json(rest, "POST", "/api/projects", {"name": f"e2e 项目 {suffix}"})
    milestone = rest_json(
        rest, "POST", f"/api/projects/{project['id']}/milestones", {"name": "12月版本"}
    )
    return project["id"], milestone["id"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5191")
    parser.add_argument("--rest", default=None, help="REST 根地址，默认与 --url 相同")
    args = parser.parse_args()

    rest = args.rest or args.url
    mcp = Mcp(args.url)
    mcp.initialize()

    suffix = str(int(time.time()))[-6:]
    project_id, milestone_id = new_project(rest, suffix)

    def move_to_review(task_id: str):
        for status in ["DocsReady", "Planned", "InDev", "Testing", "Review"]:
            result = mcp.call(
                "workbench_update_status",
                {"taskId": task_id, "status": status, "actor": "e2e-agent"},
            )
            if not result.get("ok"):
                raise RuntimeError(f"推进到 {status} 失败: {result}")

    # ── A 正向链路 ────────────────────────────────────────────────────
    section("链路 A：从编号到结项")

    def new_task(key: str) -> str:
        task = mcp.call(
            "workbench_create_task",
            {
                "projectId": project_id,
                "title": f"e2e 任务 {key}",
                "milestoneId": milestone_id,
                "key": key,
            },
        )
        if not task.get("id"):
            raise RuntimeError(f"无法创建任务: {task}")
        return task["id"]

    print("\n-- 按编号找任务（人说的是 req001） --")
    task_id = new_task("REQ-001")
    for spoken in ["req001", "REQ-001", "Req-001"]:
        found = mcp.call("workbench_get_task_by_key", {"projectId": project_id, "key": spoken})
        tasks = found.get("tasks") or []
        expect(found.get("ok") and len(tasks) == 1, f"'{spoken}' 应命中唯一任务")
        if tasks:
            expect(tasks[0]["id"] == task_id, f"'{spoken}' 应命中同一个任务")

    missing = mcp.call("workbench_get_task_by_key", {"projectId": project_id, "key": "REQ-999"})
    expect(not missing.get("ok") and missing.get("availableKeys") is not None,
           "查不到编号时应返回现有编号清单")

    print("\n-- claim / 上下文 / 计划 --")
    claim = mcp.call("workbench_claim_task", {"taskId": task_id, "agentId": "e2e-agent", "leaseMinutes": 60})
    expect(claim.get("ok"), "claim 应成功")

    context = mcp.call("workbench_get_context", {"taskId": task_id})
    expect("allowedTransitions" in context, "上下文应带出允许的状态迁移")

    plan = mcp.call(
        "workbench_set_plan",
        {
            "taskId": task_id,
            "background": "12月版本要支持账号密码登录",
            "understanding": "新增 /api/login 端点，校验账号密码并签发 token",
            "breakdown": "1) 建 DTO 2) 写鉴权服务 3) 加端点 4) 补单测",
            "acceptanceCriteria": "错误密码返回 401；正确密码返回 token",
            "author": "e2e-agent",
        },
    )
    expect(bool(plan.get("id")), "计划应写入")

    print("\n-- 推进到 Review --")
    move_to_review(task_id)

    print("\n-- 开评审轮 + 取评审包 --")
    started = mcp.call("workbench_start_review", {"taskId": task_id, "reviewer": "e2e-agent"})
    round_dto = started.get("round") or {}
    expect(started.get("ok") and len(round_dto.get("items") or []) == 7, "应开出 7 个维度的待办")

    pack = (mcp.call("workbench_get_review_pack", {"taskId": task_id}).get("pack")) or {}
    dimensions = pack.get("dimensions") or []
    expect(len(dimensions) == 7, "评审包应有 7 个维度")
    expect(all(d.get("checklist") for d in dimensions), "每个维度都该有检查清单")
    print(f"  变更文件: {pack.get('changedFiles')}")

    print("\n-- 逐维度回写（7 条全过） --")
    for dimension in dimensions:
        result = mcp.call(
            "workbench_submit_review",
            {
                "taskId": task_id,
                "dimension": dimension["key"],
                "passed": True,
                "findings": f"{dimension['key']}: 无问题",
                "evidence": "看了实现与对应单测",
                "reviewer": "e2e-agent",
            },
        )
        expect(result.get("ok"), f"{dimension['key']} 回写应成功")

    current = (mcp.call("workbench_get_review", {"taskId": task_id}).get("round")) or {}
    expect(current.get("verdict") == "Passed", "当前轮判定应为 Passed")

    done = mcp.call("workbench_update_status", {"taskId": task_id, "status": "Done", "actor": "e2e-agent"})
    expect(done.get("ok"), "评审全过后应能结项")

    history = mcp.call("workbench_list_reviews", {"taskId": task_id}).get("rounds") or []
    expect(len(history) == 1 and len(history[0].get("items") or []) == 7, "历史应留一轮 7 条")

    # ── B 拦截链路 ────────────────────────────────────────────────────
    section("链路 B：评审没过不许结项")

    print("\n-- 只评 6 个维度就结项 --")
    partial_id = new_task(f"B-{suffix}")
    move_to_review(partial_id)
    mcp.call("workbench_start_review", {"taskId": partial_id, "reviewer": "e2e-agent"})

    for dimension in DIMENSIONS[:6]:
        mcp.call(
            "workbench_submit_review",
            {"taskId": partial_id, "dimension": dimension, "passed": True, "reviewer": "e2e-agent"},
        )

    blocked = mcp.call("workbench_update_status", {"taskId": partial_id, "status": "Done", "actor": "e2e-agent"})
    expect(not blocked.get("ok"), "只评 6 个维度不该能结项")
    expect("待评审" in (blocked.get("error") or ""), "错误信息应指出还有哪个维度没评")

    after = mcp.call("workbench_get_task", {"taskId": partial_id})
    expect(after.get("status") == "Review", "状态应仍停在 Review")

    print("\n-- 有一个维度不通过就结项 --")
    failed_id = new_task(f"C-{suffix}")
    move_to_review(failed_id)
    mcp.call("workbench_start_review", {"taskId": failed_id, "reviewer": "e2e-agent"})

    for dimension in DIMENSIONS:
        mcp.call(
            "workbench_submit_review",
            {
                "taskId": failed_id,
                "dimension": dimension,
                "passed": dimension != "security",
                "findings": "有注入风险" if dimension == "security" else "ok",
                "reviewer": "e2e-agent",
            },
        )

    blocked2 = mcp.call("workbench_update_status", {"taskId": failed_id, "status": "Done", "actor": "e2e-agent"})
    expect(not blocked2.get("ok"), "有不通过维度不该能结项")
    expect("不通过" in (blocked2.get("error") or ""), "错误信息应指出有不通过的维度")

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
