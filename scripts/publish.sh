#!/usr/bin/env bash
#
# 打出可交付的观复：一个 tar.gz，组长解开就能装、能跑。
#
#   ./scripts/publish.sh                      # 按当前机器的平台打
#   ./scripts/publish.sh linux-x64            # 指定平台（交叉发布）
#   ./scripts/publish.sh linux-arm64
#
# 产物结构：
#   dist/guanfu-<平台>-<日期>/
#     bin/guanfu-server/   服务端（自包含，不需要装 .NET）
#     bin/guanfu-cli/      命令行与 stdio 桥接（自包含）
#     bin/guanfu           包装脚本（转发给 guanfu-cli/guanfu）
#     deploy/              systemd 单元 / 反向代理 / 环境变量示例
#     README.md            交付说明：装、跑、备份、升级
#     VERSION
#
set -euo pipefail

cd "$(dirname "$0")/.."

RID="${1:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) RID="osx-arm64" ;;
    Darwin-x86_64) RID="osx-x64" ;;
    Linux-x86_64) RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    *) echo "不认识的平台：$(uname -s)-$(uname -m)。请显式指定，例如 linux-x64。"; exit 1 ;;
  esac
fi

STAMP="$(date +%Y%m%d)"
NAME="guanfu-${RID}-${STAMP}"
OUT="dist/$NAME"

echo "==> 清理 $OUT"
rm -rf "$OUT"
mkdir -p "$OUT/bin" "$OUT/deploy"

echo "==> 发布服务端（$RID，自包含）"
dotnet publish src/Guanfu.Web/Guanfu.Web.csproj \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$OUT/bin/guanfu-server"

echo "==> 发布命令行（$RID，自包含）"
dotnet publish src/Guanfu.Cli/Guanfu.Cli.csproj \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$OUT/bin/guanfu-cli"

cat > "$OUT/bin/guanfu" <<'EOF'
#!/usr/bin/env sh
# 观复的命令行入口（转发给同目录下的 guanfu-cli）。
exec "$(dirname "$0")/guanfu-cli/guanfu" "$@"
EOF
chmod +x "$OUT/bin/guanfu"

# 命令行的可执行文件改成好认的名字（原来叫 Guanfu.Cli）
if [ -f "$OUT/bin/guanfu-cli/Guanfu.Cli" ]; then
  mv "$OUT/bin/guanfu-cli/Guanfu.Cli" "$OUT/bin/guanfu-cli/guanfu"
fi
if [ -f "$OUT/bin/guanfu-cli/Guanfu.Cli.exe" ]; then
  mv "$OUT/bin/guanfu-cli/Guanfu.Cli.exe" "$OUT/bin/guanfu-cli/guanfu.exe"
fi

# 服务端的可执行文件改成好认的名字（原来叫 Guanfu.Web）
if [ -f "$OUT/bin/guanfu-server/Guanfu.Web" ]; then
  mv "$OUT/bin/guanfu-server/Guanfu.Web" "$OUT/bin/guanfu-server/guanfu-server"
fi
if [ -f "$OUT/bin/guanfu-server/Guanfu.Web.exe" ]; then
  mv "$OUT/bin/guanfu-server/Guanfu.Web.exe" "$OUT/bin/guanfu-server/guanfu-server.exe"
fi

echo "==> 写部署模板"
if [ -d deploy ]; then
  cp -R deploy/. "$OUT/deploy/"
fi

# 交付说明放在包根目录（拆开就能读）
if [ -f deploy/README.md ]; then
  cp deploy/README.md "$OUT/README.md"
fi

cat > "$OUT/VERSION" <<EOF
观复 $STAMP
平台：$RID
构建时间：$(date -u '+%Y-%m-%d %H:%M:%S UTC')
EOF

tar -czf "$OUT.tar.gz" -C dist "$NAME"

echo
echo "==> 打包完成：$OUT.tar.gz"
du -sh "$OUT.tar.gz" | sed 's/^/    /'
echo
echo "解开就能跑："
echo "    tar -xzf $(basename "$OUT.tar.gz")"
echo "    cd $NAME"
echo "    ./bin/guanfu-server          # 服务端（UI + REST + /mcp）"
echo
echo "详细说明在包里的 README.md。"
