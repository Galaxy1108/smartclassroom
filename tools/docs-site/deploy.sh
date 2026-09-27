#!/usr/bin/env bash
# 一键部署文档站到 Cloudflare 隧道后面的服务器。
#
#   bash tools/docs-site/deploy.sh
#
# 服务器现状（2026-09-27 部署时确认）：
#   Windows 主机 iZkwv94x2vd6v7Z，SSH 别名 server
#   站点目录  C:\data\web\smartclassroom-docs
#   静态服务  C:\data\web\docs-server.mjs（计划任务 SmartClassroomDocs，SYSTEM，开机自启，端口 6187）
#   隧道入口  docs.galaxy1108.top → http://127.0.0.1:6187（C:\data\web\config.yml）
#
# 改内容只要重跑这个脚本 —— 服务器直接读磁盘上的文件，不用重启任何服务。
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HOST="${DOCS_HOST:-server}"
REMOTE_DIR="${DOCS_REMOTE_DIR:-C:\\data\\web\\smartclassroom-docs}"
BASE="${DOCS_BASE:-/smartclassroom/}"

cd "$REPO"
echo "① 构建站点（base=$BASE）"
node tools/docs-site/build.mjs --base "$BASE"

echo "② 同步服务器脚本（静态服务器 + 启动批处理）"
# 用 base64 传字节：直接 Set-Content 会在 Windows PowerShell 下把换行/中文写坏
put_file() {  # put_file <本地文件> <远端路径>
  local b64; b64=$(base64 -w0 "$1")
  ssh -o BatchMode=yes "$HOST" "[IO.File]::WriteAllBytes('$2', [Convert]::FromBase64String('$b64'))"
}
put_file tools/docs-site/server.mjs 'C:\data\web\docs-server.mjs'
printf '@echo off\r\n"C:\\Program Files\\nodejs\\node.exe" "C:\\data\\web\\docs-server.mjs" --root "C:\\data\\web\\smartclassroom-docs" --port 6187 --base "/smartclassroom"\r\n' > /tmp/sc-docs-start.bat
put_file /tmp/sc-docs-start.bat 'C:\data\web\start-docs.bat'
rm -f /tmp/sc-docs-start.bat

echo "③ 上传站点到 $HOST:$REMOTE_DIR"
tar -czf /tmp/sc-docs.tgz -C docs-site-dist .
ssh -o BatchMode=yes "$HOST" "New-Item -ItemType Directory -Force -Path '$REMOTE_DIR' | Out-Null"
ssh -o BatchMode=yes "$HOST" "tar -xzf - -C '$REMOTE_DIR'" < /tmp/sc-docs.tgz
rm -f /tmp/sc-docs.tgz

echo "④ 服务器自检"
# 内容更新不需要重启服务（服务器直接读磁盘）；脚本更新后重启计划任务
ssh -o BatchMode=yes "$HOST" 'Start-ScheduledTask -TaskName SmartClassroomDocs; Start-Sleep -Seconds 3; curl.exe -s -o NUL -w "   本机 /smartclassroom/ → %{http_code}`n" http://127.0.0.1:6187/smartclassroom/; curl.exe -s -o NUL -w "   本机 /smartclassroom/docs → %{http_code}`n" http://127.0.0.1:6187/smartclassroom/docs'

echo "⑤ 公网自检"
curl -sS --max-time 20 -o /dev/null -w "   https://docs.galaxy1108.top/smartclassroom/ → %{http_code}\n" \
  https://docs.galaxy1108.top/smartclassroom/
echo "完成。"
