#!/usr/bin/env bash
# 端到端验证：起一个临时观复，跑 MCP 全链路，退出时收摊。
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

cleanup() {
  if [[ -n "${SERVER_PID:-}" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$HOME_DIR"
}
trap cleanup EXIT

echo "起服务: http://127.0.0.1:$PORT  (WORKBENCH_HOME=$HOME_DIR)"
WORKBENCH_HOME="$HOME_DIR" "$DOTNET" run --project "$ROOT/src/Guanfu.Web" \
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
# 所以把观复生成的 token 带上 —— agent 那条路（MCP）本来就没有这个权限。
WB_HUMAN_TOKEN="$(cat "$HOME_DIR/.human-token" 2>/dev/null || true)"
export WB_HUMAN_TOKEN
# CLI 认这个环境变量（--token > WORKBENCH_HUMAN_TOKEN > WORKBENCH_HOME/.human-token）。
export WORKBENCH_HUMAN_TOKEN="$WB_HUMAN_TOKEN"

# CLI 要单独跑一遍：它是次要入口，但 `guanfu mcp` 的 stdio 桥接不可替代，
# 而且敏感命令在 CLI 上也得守同一条人工凭证门槛。
"$DOTNET" build "$ROOT/src/Guanfu.Cli/Guanfu.Cli.csproj" -v quiet --nologo >/dev/null
CLI_DLL="$(find "$ROOT/src/Guanfu.Cli/bin" -name 'Guanfu.Cli.dll' -path '*net8.0*' -print -quit)"
if [[ -z "$CLI_DLL" ]]; then
  echo "找不到 Guanfu.Cli.dll —— CLI 冒烟跳过" >&2
else
  export CLI_DLL
fi

status=0

echo
echo "### 链路 A/B/C：MCP 全链路（编号 → 结项、评审拦截、分支守卫）"
python3 "$ROOT/tests/e2e/mcp_e2e.py" --url "http://127.0.0.1:$PORT" || status=1

echo
echo "### 链路 D：写操作的人工审批门禁"
python3 "$ROOT/tests/e2e/approval_e2e.py" --url "http://127.0.0.1:$PORT" || status=1

echo
echo "### 链路 F：agent 会话 ↔ 项目 / 任务绑定"
python3 "$ROOT/tests/e2e/session_e2e.py" --url "http://127.0.0.1:$PORT" || status=1

# 链路 G 自己起服务（要真崩一次再起一次），所以端口由它自己挑。
echo
echo "### 链路 G：重启对账（执行中断 → 结果未知）"
python3 "$ROOT/tests/e2e/restart_e2e.py" --dotnet "$DOTNET" --root "$ROOT" || status=1

if [[ -n "${CLI_DLL:-}" ]]; then
  echo
  echo "### 链路 E：CLI（guanfu）冒烟"
  python3 "$ROOT/tests/e2e/cli_smoke.py" \
    --url "http://127.0.0.1:$PORT" \
    --dotnet "$DOTNET" \
    --cli "$CLI_DLL" || status=1
fi

exit "$status"
