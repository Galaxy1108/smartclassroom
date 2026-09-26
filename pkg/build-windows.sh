#!/usr/bin/env bash
# 构建 Windows 绿色包 zip。需要 Node ≥ 22.19 由用户环境提供（写入 README）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$ROOT/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
VERSION="$(grep -oP '^pkgver=\K.*' "$ROOT/pkg/PKGBUILD")"
OUT="$ROOT/pkg/win-stage"
ZIP="$ROOT/pkg/smartclassroom-${VERSION}-win-x64.zip"

echo "==> 1/4 安装 pi-ai 边车依赖 (npm install)"
(cd "$ROOT/tools/ai-sidecar" && npm install --omit=dev --no-audit --no-fund >/dev/null)

echo "==> 2/4 发布应用 (self-contained win-x64)"
rm -rf "$OUT"
dotnet publish "$ROOT/src/App/SmartClassroom.App.csproj" \
  -c Release -r win-x64 --self-contained -o "$OUT" --nologo -v q

echo "==> 3/4 放入 AI 边车（含 node_modules）"
rm -rf "$OUT/ai-sidecar"
mkdir -p "$OUT/ai-sidecar"
cp "$ROOT/tools/ai-sidecar/package.json" "$ROOT/tools/ai-sidecar/sidecar.mjs" "$OUT/ai-sidecar/"
cp -a "$ROOT/tools/ai-sidecar/node_modules" "$OUT/ai-sidecar/"

echo "==> 3.5/4 归一权限（工作区文件可能是 0600）"
chmod -R a+rX "$OUT"

echo "==> 4/4 打 zip"
rm -f "$ZIP"
python3 - "$OUT" "$ZIP" <<'PY'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for root, _, files in os.walk(src):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, src))
print('zip written:', dst)
PY
ls -la "$ZIP"
