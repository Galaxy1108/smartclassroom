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

echo "==> 3.4/4 打包 ClassIsland 插件（应用自带 + 单独 .cipx）"
PLUGIN_STAGE="$ROOT/pkg/plugin-stage"
rm -rf "$PLUGIN_STAGE"
dotnet publish "$ROOT/src/ClassIslandPlugin/SmartClassroom.ClassIslandPlugin.csproj" \
  -c Release -o "$PLUGIN_STAGE" --nologo -v q
# 应用自带一份，设置页的「安装插件」直接用它
rm -rf "$ROOT/pkg/stage/classisland-plugin"
cp -a "$PLUGIN_STAGE" "$ROOT/pkg/stage/classisland-plugin"
rm -f "$ROOT/pkg/stage/classisland-plugin"/*.pdb
# 单独一份 .cipx（ClassIsland 的插件包格式就是 zip）
(cd "$PLUGIN_STAGE" && zip -q -r "$ROOT/pkg/smartclassroom-classisland-plugin.cipx" . -x "*.pdb")

echo "==> 3.5/4 归一权限（工作区文件可能是 0600）"
chmod -R a+rX "$ROOT/pkg/stage"

echo "==> 3.6/4 放置桌面图标（Avalonia 资源不进 publish 输出）"
cp "$ROOT/src/App/Assets/app-icon-256.png" "$ROOT/pkg/smartclassroom.png"

echo "==> 4/4 makepkg"
(cd "$ROOT/pkg" && makepkg -f)

ls -la "$ROOT/pkg/"*.pkg.tar.zst
