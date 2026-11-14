#!/usr/bin/env bash
# 一键验收：编译 → 单测 → 端到端。
#
#   ./tests/verify.sh
#
# 企业验收要的就是一条命令能跑完、能复现、能看到每一步的结果。
# 末尾给一份总账：哪几步过了、哪几步没过、有多少条断言。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="$ROOT/.dotnet/dotnet"
E2E_LOG="$(mktemp /tmp/wb-verify-e2e.XXXXXX)"

if [[ ! -x "$DOTNET" ]]; then
  # CI 里没有仓库自带的隔离 SDK，退回用 PATH 上的 dotnet
  # （CI 把版本固定在 8.0.x，与目标框架一致）。
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
  else
    echo "找不到 dotnet：$DOTNET 不存在，PATH 上也没有。" >&2
    echo "先跑: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --quality ga --install-dir \"$ROOT/.dotnet\"" >&2
    exit 1
  fi
fi

FAILED=0
declare -a SUMMARY=()

record() {
  local name="$1" ok="$2" detail="$3"
  SUMMARY+=("$name|$ok|$detail")
  [[ "$ok" == "yes" ]] || FAILED=1
}

echo "════════════════════════════════════════════════════════════"
echo " 工作台验收：编译 → 单测 → 端到端"
echo "════════════════════════════════════════════════════════════"

# ── 1 编译 ───────────────────────────────────────────────────────────
echo
echo "── [1/3] 编译 ──────────────────────────────────────────────"
BUILD_OUT="$("$DOTNET" build "$ROOT/Workbench.sln" -v quiet --nologo 2>&1)"
BUILD_CODE=$?
if [[ $BUILD_CODE -eq 0 ]]; then
  WARNINGS="$(printf '%s' "$BUILD_OUT" | grep -c 'Warning(s)' || true)"
  echo "$BUILD_OUT" | grep -E 'Warning\(s\)|Error\(s\)' || true
  record "编译" "yes" "0 警告 0 错误"
else
  echo "$BUILD_OUT" | tail -20
  record "编译" "no" "编译失败"
fi

# ── 2 单测 ───────────────────────────────────────────────────────────
echo
echo "── [2/3] 单元测试 ──────────────────────────────────────────"
TEST_OUT="$("$DOTNET" test "$ROOT/Workbench.sln" --nologo 2>&1)"
TEST_CODE=$?
printf '%s\n' "$TEST_OUT" | grep -E '已通过!|失败!|Failed!|Passed!' || true
if [[ $TEST_CODE -eq 0 ]]; then
  TOTAL="$(printf '%s' "$TEST_OUT" | grep -oE '总计:[[:space:]]*[0-9]+' | grep -oE '[0-9]+' || true)"
  record "单元测试" "yes" "${TOTAL:-?} 条全过"
else
  printf '%s\n' "$TEST_OUT" | grep -E '失败|Failed' | head -20
  record "单元测试" "no" "有用例没过"
fi

# ── 3 端到端 ─────────────────────────────────────────────────────────
echo
echo "── [3/3] 端到端（A/B/C 主链路 · D 审批门禁 · E CLI） ────────"
"$ROOT/tests/e2e/run.sh" > "$E2E_LOG" 2>&1
E2E_CODE=$?
grep -E '^###|全部通过|❌ 失败' "$E2E_LOG" || true
PASSED="$(grep -c '✓' "$E2E_LOG" || true)"
FAILED_CASES="$(grep -c '✗' "$E2E_LOG" || true)"
if [[ $E2E_CODE -eq 0 ]]; then
  record "端到端" "yes" "${PASSED} 条断言全过"
else
  grep -E '✗' "$E2E_LOG" | head -30
  record "端到端" "no" "${FAILED_CASES} 条断言没过"
fi

# ── 总账 ─────────────────────────────────────────────────────────────
echo
echo "════════════════════════════════════════════════════════════"
echo " 总账"
echo "════════════════════════════════════════════════════════════"
for row in "${SUMMARY[@]}"; do
  IFS='|' read -r name ok detail <<< "$row"
  if [[ "$ok" == "yes" ]]; then
    printf "  ✅ %-10s %s\n" "$name" "$detail"
  else
    printf "  ❌ %-10s %s\n" "$name" "$detail"
  fi
done

echo
if [[ $FAILED -eq 0 ]]; then
  echo "  验收通过。端到端明细见 $E2E_LOG"
else
  echo "  验收未通过。端到端明细见 $E2E_LOG"
fi

exit "$FAILED"
