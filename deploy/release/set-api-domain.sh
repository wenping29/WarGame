#!/usr/bin/env bash
# =============================================================================
# 一键替换前端产物里的后端地址（不用重新构建前端）
#
# 用法（在服务器上，包解开后的 deploy/ 目录里执行）：
#   bash set-api-domain.sh https://api.你的域名.com/api
#
# 可选第二个参数指定前端产物目录，默认 ../web
#   bash set-api-domain.sh https://api.你的域名.com/api /www/wwwroot/wargame/web
#
# 前提：包是用占位符 http://43.142.147.244:7777/api 构建的。
#      如果包是按真实域名构建的，这个脚本不需要跑。
#
# ⚠ 换完域名后还要做一件事：
#    如果**前端域名**也变了，必须同步改后端的 HF_ALLOWED_ORIGINS，
#    否则浏览器 CORS 预检失败（表现为前端一片红、请求 403）。
#    后端域名变更是**不影响** HF_ALLOWED_ORIGINS 的。
# =============================================================================
set -euo pipefail

OLD="http://43.142.147.244:7777/api"
NEW="${1:-}"
WEB="${2:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/web}"

if [ -z "$NEW" ]; then
  echo "用法: bash set-api-domain.sh <新的后端地址，必须以 /api 结尾> [web目录]" >&2
  echo "示例: bash set-api-domain.sh https://api.hexfront.cn/api" >&2
  exit 1
fi

case "$NEW" in
  */api) ;;
  *) echo "错误: 地址必须以 /api 结尾（后端控制器前缀是 [Route(\"api\")]），收到: $NEW" >&2; exit 1 ;;
esac

if [ ! -d "$WEB/assets" ]; then
  echo "错误: 找不到前端产物目录 $WEB/assets" >&2
  exit 1
fi

echo "前端目录 : $WEB"
echo "旧地址   : $OLD"
echo "新地址   : $NEW"

# 先确认旧地址确实在产物里（避免改了个寂寞）
if ! grep -rqF "$OLD" "$WEB/assets" 2>/dev/null; then
  echo "错误: 产物中找不到 $OLD —— 这个包可能不是用占位符构建的，无需替换。" >&2
  exit 1
fi

# 备份
BACKUP="${WEB}/assets.bak.$(date +%Y%m%d-%H%M%S)"
cp -r "$WEB/assets" "$BACKUP"
echo "已备份   : $BACKUP"

# 替换（产物是压缩后的 JS，域名是完整字符串，直接全文替换）
grep -rlF "$OLD" "$WEB/assets" 2>/dev/null | while read -r f; do
  sed -i "s|$OLD|$NEW|g" "$f"
  echo "  已改: $f"
done

# 校验
echo ""
if grep -rqF "$NEW" "$WEB/assets" 2>/dev/null && ! grep -rqF "$OLD" "$WEB/assets" 2>/dev/null; then
  echo "✔ 替换成功：新地址已写入，旧地址已清除。"
  echo ""
  echo "后续步骤："
  echo "  1. 若前端域名也变了 → 改后端环境变量 HF_ALLOWED_ORIGINS，然后重启后端"
  echo "  2. 浏览器强刷（Ctrl+F5）清掉旧缓存的 index.html"
else
  echo "✘ 替换校验失败，请检查上面的输出。" >&2
  exit 1
fi
