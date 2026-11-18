#!/usr/bin/env python3
"""链路 F：agent 的对话跟项目/任务绑在一起。

这条链路验的是整套设计的地基 —— 没有它，后面「追踪任务进度、记录产出文件、
留下数据库调用记录」全是无根之木：

  F1 项目绑本地仓库：目录落在项目绑定的仓库范围内，反查得到项目
  F2 不传 projectId 也能干活：会话自动归属，agent 不用背 GUID
  F3 跨项目要拒：会话在 A 项目，显式传 B 项目 → 必须被拒
  F4 按编号认领：claim(taskKey) 就能领，不用先查 GUID
  F5 认领之后审计自动带任务：agent 不用自己填 taskId
  F6 提交之后产出文件自动落库：观复自己数出来的，不是 agent 自报的

用法:
    python3 tests/e2e/session_e2e.py --url http://127.0.0.1:5199
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.request

from mcp_e2e import Mcp, expect, section, rest_json, FAILURES, make_temp_repo


def decide(rest: str, approval_id: str, approve: bool = True) -> tuple[int, str]:
    """人工裁决审批。敏感端点要凭证。"""
    token = os.environ.get("WB_HUMAN_TOKEN", "")
    headers = {"Content-Type": "application/json"}
    if token:
        headers["X-Guanfu-Human-Token"] = token

    request = urllib.request.Request(
        rest.rstrip("/") + f"/api/approvals/{approval_id}/decide",
        data=json.dumps({"approve": approve, "decidedBy": "e2e-human"}).encode(),
        headers=headers,
        method="POST",
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
    suffix = str(int(time.time()))[-6:]

    section("链路 F：agent 会话 ↔ 项目 / 任务绑定")

    repo = make_temp_repo("main")
    if repo is None:
        print("  ⚠ git 不可用，跳过链路 F")
        return 1

    # ── F1 项目绑本地仓库 ──────────────────────────────────────────
    print("\n-- F1 项目绑本地仓库 --")
    project = rest_json(rest, "POST", "/api/projects", {
        "name": f"会话绑定 e2e {suffix}",
        "repoPath": repo,
    })
    project_id = project["id"]
    expect(bool(project_id), "项目应创建成功")
    expect(
        project.get("repoRoot") is not None,
        "项目应记下绑定键（规范化后的仓库路径）",
    )

    other = rest_json(rest, "POST", "/api/projects", {"name": f"别的项目 {suffix}"})
    other_id = other["id"]

    # 带工作目录的客户端 —— 这就是桥接做的事：告诉观复「我在这个目录干活」
    bound = Mcp(args.url, headers={"X-Guanfu-Repo": repo, "X-Guanfu-Agent": "e2e-agent"})
    bound.initialize()

    # ── F2 不传 projectId 也能干活 ─────────────────────────────────
    print("\n-- F2 不传 projectId 也能干活（会话自动归属）--")
    listing = bound.call("fs_list", {})
    expect(listing.get("ok") is True, f"不传 projectId 的 fs_list 应成功（实际：{listing.get('error')}）")
    expect(
        listing.get("root") is not None and repo in (listing.get("root") or ""),
        f"fs_list 的根应是项目绑定的仓库（实际：{listing.get('root')}）",
    )

    session = bound.call("guanfu_get_session", {})
    expect(session.get("ok") is True, "应有活跃会话")
    expect(
        (session.get("session") or {}).get("projectId") == project_id,
        f"会话应绑定到刚才那个项目（实际：{(session.get('session') or {}).get('projectId')}）",
    )
    expect(
        (session.get("session") or {}).get("agentId") == "e2e-agent",
        "会话应记住 agent 标识",
    )

    # ── F3 跨项目要拒 ─────────────────────────────────────────────
    print("\n-- F3 会话在 A 项目，显式传 B 项目要被拒 --")
    crossed = bound.call("fs_list", {"projectId": other_id})
    expect(crossed.get("ok") is not True, "跨项目调用必须被拒")
    expect(
        "不一致" in (crossed.get("error") or ""),
        f"错误信息应说明是项目不一致（实际：{crossed.get('error')}）",
    )

    # ── F4 按编号认领 ─────────────────────────────────────────────
    print("\n-- F4 按编号认领（不用先查 GUID）--")
    created = bound.call("guanfu_create_task", {
        "title": f"会话绑定 e2e 任务 {suffix}",
        "type": "requirement",
    })
    task_id = created["id"]
    task_key = created.get("key")
    expect(bool(task_key), f"任务应有编号（实际：{task_key}）")

    claimed = bound.call("guanfu_claim_task", {"taskKey": task_key, "agentId": "e2e-agent"})
    expect(claimed.get("ok") is True, f"按编号认领应成功（实际：{claimed.get('error')}）")
    expect(
        (claimed.get("task") or {}).get("id") == task_id,
        "认领到的应该是那个任务",
    )

    # ── F4.1 工位独占：主工作区同一时刻只允许一个 agent 占 ────────
    print("\n-- F4.1 主工作区同一时刻只允许一个 agent 占 --")
    # 另一个 agent（也绑同一个仓库）想在主工作区干活 → 必须被拒。
    # 两个人共用一个目录会互相踩文件，这是 agent 时代最常见的事故。
    second = bound.call("guanfu_create_task", {
        "title": f"第二个活 {suffix}",
        "type": "requirement",
    })
    second_key = second.get("key")

    other_agent = Mcp(args.url, headers={"X-Guanfu-Repo": repo, "X-Guanfu-Agent": "e2e-agent-2"})
    other_agent.initialize()

    blocked = other_agent.call(
        "guanfu_claim_task", {"taskKey": second_key, "agentId": "e2e-agent-2"}
    )
    expect(blocked.get("ok") is not True, "另一个 agent 不该能占用同一个主工作区")
    expect(
        "占着" in (blocked.get("error") or ""),
        f"错误信息应说明工作区被谁占着（实际：{blocked.get('error')}）",
    )
    expect(
        "worktree" in (blocked.get("error") or ""),
        f"错误信息应给出出路（开自己的 worktree）（实际：{blocked.get('error')}）",
    )

    # 开了自己的 worktree → 允许并行
    worktree = os.path.join(os.path.dirname(repo), f"wt-{suffix}")
    subprocess.run(
        ["git", "worktree", "add", "-q", worktree, "-b", f"feature/e2e-{suffix}"],
        cwd=repo, check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    if os.path.isdir(worktree):
        parallel = other_agent.call(
            "guanfu_claim_task",
            {"taskKey": second_key, "agentId": "e2e-agent-2", "worktree": worktree},
        )
        expect(
            parallel.get("ok") is True,
            f"带自己的 worktree 时应能并行领活（实际：{parallel.get('error')}）",
        )
        expect(
            (parallel.get("task") or {}).get("workPath") is not None,
            "认领结果里应带上登记的工位",
        )

        # 工具要**认工位**：文件只存在于 worktree 里，读它必须读得到 ——
        # 读的是主工作区的话，agent 看不到自己的产出，产出采集也会记错文件。
        marker = "wt-only-marker.txt"
        with open(os.path.join(worktree, marker), "w", encoding="utf-8") as handle:
            handle.write("只在 worktree 里的文件")
        read_back = other_agent.call("fs_read", {"path": marker})
        expect(
            read_back.get("ok") is True and "只在 worktree" in (read_back.get("content") or ""),
            f"文件工具应按工位读 worktree 里的文件（实际：{read_back.get('error') or read_back.get('content')}）",
        )
        listing = other_agent.call("fs_list", {})
        expect(
            listing.get("root") is not None and worktree in (listing.get("root") or ""),
            f"fs_list 的根应是工位（实际：{listing.get('root')}）",
        )
    else:
        print("  ⚠ git worktree 不可用，跳过并行领活场景")

    context = bound.call("guanfu_get_context", {"taskKey": task_key})
    expect(
        (context.get("task") or {}).get("id") == task_id,
        f"按编号取上下文应拿到那个任务（实际：{context.get('error') or context.get('task')}）",
    )
    station = context.get("station") or {}
    expect(
        station.get("mainCheckoutHolder") is not None,
        f"上下文里应说明主工作区被谁占着（实际：{station}）",
    )

    # ── F4.2 每任务工具调用上限 ───────────────────────────────────
    print("\n-- F4.2 每任务工具调用上限（用量闸门）--")
    usage = rest_json(rest, "GET", f"/api/tasks/{task_id}/usage")
    used = usage.get("toolCalls", 0)
    rest_json(rest, "PUT", f"/api/projects/{project_id}/config", {"maxToolCallsPerTask": used + 1})

    ok_call = other_agent.call("fs_list", {})
    expect(ok_call.get("ok") is True, "还没超上限时工具调用应正常")

    blocked_call = other_agent.call("fs_list", {})
    expect(blocked_call.get("ok") is not True, "超过上限后工具调用应被拦下")
    expect(
        "上限" in (blocked_call.get("error") or ""),
        f"错误信息应说明上限（实际：{blocked_call.get('error')}）",
    )

    # 撤销限制，别影响后面的链路
    rest_json(rest, "PUT", f"/api/projects/{project_id}/config", {"maxToolCallsPerTask": 0})

    # ── F4.3 交接：放掉活 + 下一个接手的人看得见为什么 ────────────────
    print("\n-- F4.3 交接（放掉活 + 接手的人看得见上下文）--")
    released = other_agent.call(
        "guanfu_release_task", {"taskId": second["id"], "reason": "环境不对，接不了"}
    )
    expect(released.get("ok"), f"放掉活应成功（实际：{released.get('error')}）")

    # 另一个 agent 接手 → 结果里要带「上一个是谁、为什么」。
    # 用**独立的客户端**：bound 这个会话已经挂在第一个任务上，
    # 拿它去领别的任务会把会话的任务改掉，后面的断言就全乱了。
    third = Mcp(args.url, headers={"X-Guanfu-Repo": repo, "X-Guanfu-Agent": "e2e-agent-3"})
    third.initialize()
    # 主工作区被别的 agent 占着（工位独占）→ 接手方得声明自己的 worktree
    takeover = third.call(
        "guanfu_claim_task",
        {"taskId": second["id"], "agentId": "e2e-agent-3", "worktree": worktree},
    )
    expect(takeover.get("ok"), f"接手应成功（实际：{takeover.get('error')}）")
    expect(
        takeover.get("previousHolder") == "e2e-agent-2",
        f"接手的结果里应带上一个持有者（实际：{takeover.get('previousHolder')}）",
    )
    expect(
        "环境不对" in (takeover.get("handoffReason") or ""),
        f"接手的结果里应带上交接的原因（实际：{takeover.get('handoffReason')}）",
    )

    # ── F5 审计自动带任务 ─────────────────────────────────────────
    print("\n-- F5 认领之后审计自动带任务归属 --")
    # 认领之后再来一次调用 —— agent 不用自己填 taskId，
    # 会话记住了它干的是哪个任务，审计就该自动带上。
    after_claim = bound.call("fs_list", {})
    expect(after_claim.get("ok") is True, "认领之后的 fs_list 应成功")

    rows = rest_json(rest, "GET", f"/api/tasks/{task_id}/sessions")
    expect(len(rows) >= 1, f"任务应挂上会话（实际：{len(rows)}）")
    if rows:
        expect(
            rows[0].get("taskId") == task_id,
            "会话应挂着这个任务",
        )
        expect(
            rows[0].get("repoPath") is not None and repo in rows[0].get("repoPath"),
            "会话应记住工作目录",
        )

    audit = rest_json(rest, "GET", f"/api/audit?toolName=fs_list")
    fs_rows = [r for r in audit if r.get("taskId") == task_id]
    expect(
        len(fs_rows) >= 1,
        f"认领之后的 fs_list 审计应自动带上 taskId（实际带上的条数：{len(fs_rows)}）",
    )
    expect(
        all(r.get("projectId") == project_id for r in fs_rows),
        "审计里的项目归属应是被绑定的那个项目",
    )

    # ── F6 提交之后产出文件自动落库 ───────────────────────────────
    print("\n-- F6 提交之后产出文件自动落库 --")
    rest_json(rest, "PUT", f"/api/projects/{project_id}/config", {
        "repoPath": repo,
        "allowedCommands": ["git", "touch"],
    })

    with open(os.path.join(repo, "hello.txt"), "w", encoding="utf-8") as handle:
        handle.write("hello\n")

    commit = bound.call("git_commit", {
        "message": f"e2e 提交 {suffix}",
        "waitSeconds": 1,
        "taskId": task_id,
    })
    approval_id = commit.get("approvalId")
    expect(bool(approval_id), f"提交应生成审批单（实际：{commit.get('error')}）")

    if approval_id:
        status, _ = decide(rest, approval_id)
        expect(status == 200, f"人工批准应成功（实际 HTTP {status}）")

        committed = bound.call("git_commit", {
            "message": f"e2e 提交 {suffix}",
            "approvalId": approval_id,
            "waitSeconds": 1,
            "taskId": task_id,
        })
        expect(committed.get("ok") is True, f"批准后提交应成功（实际：{committed.get('error')}）")

    changes = rest_json(rest, "GET", f"/api/tasks/{task_id}/file-changes")
    expect(len(changes) >= 1, f"产出文件应自动落库（实际：{len(changes)} 条）")
    if changes:
        paths = [c.get("path") for c in changes]
        expect("hello.txt" in paths, f"产出文件里应包含 hello.txt（实际：{paths}）")
        expect(
            any(c.get("commitSha") for c in changes),
            "产出文件应绑到那次提交的 commit",
        )

    print("\n" + "=" * 60)
    if FAILURES:
        print(f"❌ {len(FAILURES)} 条断言没过")
        for item in FAILURES:
            print(f"  - {item}")
        return 1

    print("✅ 全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
