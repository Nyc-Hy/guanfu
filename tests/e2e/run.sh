#!/usr/bin/env bash
# 端到端验证：起一个临时工作台，跑 MCP 全链路，退出时收摊。
#
#   ./tests/e2e/run.sh
#
# 用项目下的隔离 .NET 8 SDK（.dotnet/），不用系统版 —— 目标框架就是 net8.0。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="$ROOT/.dotnet/dotnet"
PORT="${WB_E2E_PORT:-5199}"
HOME_DIR="$(mktemp -d /tmp/wb-e2e-home.XXXXXX)"
LOG="$HOME_DIR/server.log"

if [[ ! -x "$DOTNET" ]]; then
  echo "找不到隔离 SDK: $DOTNET" >&2
  echo "先跑: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --quality ga --install-dir \"$ROOT/.dotnet\"" >&2
  exit 1
fi

cleanup() {
  if [[ -n "${SERVER_PID:-}" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$HOME_DIR"
}
trap cleanup EXIT

echo "起服务: http://127.0.0.1:$PORT  (WORKBENCH_HOME=$HOME_DIR)"
WORKBENCH_HOME="$HOME_DIR" "$DOTNET" run --project "$ROOT/src/Workbench.Web" \
  --no-launch-profile --urls "http://127.0.0.1:$PORT" > "$LOG" 2>&1 &
SERVER_PID=$!

for _ in $(seq 1 40); do
  if curl -fsS -o /dev/null "http://127.0.0.1:$PORT/" 2>/dev/null; then
    break
  fi
  sleep 0.5
done

if ! curl -fsS -o /dev/null "http://127.0.0.1:$PORT/" 2>/dev/null; then
  echo "服务没起来，日志：" >&2
  tail -30 "$LOG" >&2
  exit 1
fi

# 人工凭证：敏感端点（改项目配置等）要它。e2e 扮演的是「人工操作员」，
# 所以把工作台生成的 token 带上 —— agent 那条路（MCP）本来就没有这个权限。
WB_HUMAN_TOKEN="$(cat "$HOME_DIR/.human-token" 2>/dev/null || true)"
export WB_HUMAN_TOKEN

python3 "$ROOT/tests/e2e/mcp_e2e.py" --url "http://127.0.0.1:$PORT"
