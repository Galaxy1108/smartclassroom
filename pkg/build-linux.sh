#!/usr/bin/env bash
# 构建 Linux .pkg.tar.zst：先装边车依赖，再 self-contained 发布，最后 makepkg。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$ROOT/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "==> 1/4 安装 pi-ai 边车依赖 (npm install)"
(cd "$ROOT/tools/ai-sidecar" && npm install --omit=dev --no-audit --no-fund >/dev/null)

echo "==> 2/4 发布应用 (self-contained linux-x64)"
rm -rf "$ROOT/pkg/stage"
dotnet publish "$ROOT/src/App/SmartClassroom.App.csproj" \
  -c Release -r linux-x64 --self-contained -o "$ROOT/pkg/stage" --nologo -v q

echo "==> 3/4 放入 AI 边车（含 node_modules）"
rm -rf "$ROOT/pkg/stage/ai-sidecar"
mkdir -p "$ROOT/pkg/stage/ai-sidecar"
cp "$ROOT/tools/ai-sidecar/package.json" "$ROOT/tools/ai-sidecar/sidecar.mjs" "$ROOT/pkg/stage/ai-sidecar/"
cp -a "$ROOT/tools/ai-sidecar/node_modules" "$ROOT/pkg/stage/ai-sidecar/"

echo "==> 4/4 makepkg"
(cd "$ROOT/pkg" && makepkg -f)

ls -la "$ROOT/pkg/"*.pkg.tar.zst
