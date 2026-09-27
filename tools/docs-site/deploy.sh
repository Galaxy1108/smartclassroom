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

echo "② 上传到 $HOST:$REMOTE_DIR"
tar -czf /tmp/sc-docs.tgz -C docs-site-dist .
ssh -o BatchMode=yes "$HOST" "New-Item -ItemType Directory -Force -Path '$REMOTE_DIR' | Out-Null"
ssh -o BatchMode=yes "$HOST" "tar -xzf - -C '$REMOTE_DIR'" < /tmp/sc-docs.tgz
rm -f /tmp/sc-docs.tgz

echo "③ 服务器自检"
ssh -o BatchMode=yes "$HOST" 'curl.exe -s -o NUL -w "   本机 /smartclassroom/ → %{http_code}`n" http://127.0.0.1:6187/smartclassroom/'

echo "④ 公网自检"
curl -sS --max-time 20 -o /dev/null -w "   https://docs.galaxy1108.top/smartclassroom/ → %{http_code}\n" \
  https://docs.galaxy1108.top/smartclassroom/
echo "完成。"
