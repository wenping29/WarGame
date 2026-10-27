#!/usr/bin/env bash
# =============================================================================
# HexFront 前后端分离发布包构建（zip 格式）
#
# 用法：
#   bash deploy/build-release-zip.sh
#   API_BASE_URL="https://api.你的域名.com/api" bash deploy/build-release-zip.sh
#
# 产出：
#   deploy/out/hexfront-release-<时间戳>.zip
#     hexfront-release/web/                    前端静态产物（已注入 API 地址）
#     hexfront-release/server/HexFront.Api/    后端 publish + app.db（含进程内六角格生成）
#     hexfront-release/deploy/                 nginx 配置 + 环境变量 + 本手册
#
# 注：约 2G 的地图/剧本数据（server/data/）不进包，由部署主机单独 provision，
#     发布包因此保持很小。
#
# 环境变量：
#   API_BASE_URL       前端调后端的完整前缀，必须以 /api 结尾
#                      默认 http://43.142.147.244:7777/api（占位符，部署前需替换）
#   VITE_TIANDITU_TK   天地图密钥（按**前端域名**注册白名单的那把 token）。
#                      留空 → 产物不写死 key，前端回退用后端 data/tianditu.json 下发的那把
#                      （运行时可改，改服务端文件即生效，但后端需重启）。
# =============================================================================
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$REPO/deploy/out"
API_BASE_URL="${API_BASE_URL:-http://43.142.147.244:7777/api}"
TIANDITU_TK="${VITE_TIANDITU_TK:-}"
TS="$(date +%Y%m%d-%H%M%S)"
PUBLISH_DIR="$OUT_DIR/publish-linux-x64-$TS"
ART_DIR="$OUT_DIR/artifacts-$TS"
ZIP="$OUT_DIR/hexfront-release-$TS.zip"

mkdir -p "$OUT_DIR"

# ---- 1. 前端构建 -----------------------------------------------------------
# VITE_* 以环境变量注入，优先级高于 .env.production 文件。
# TIANDITU_TK 为空时**不传**这个变量，这样本地 .env.production 里配的值仍会生效
# （而直接传空串会把它覆盖掉，导致前端既没有 key、也不回退后端）。
echo "==> 1/4 构建前端  (VITE_API_BASE_URL=$API_BASE_URL)"
cd "$REPO/webfront"
if [ -n "$TIANDITU_TK" ]; then
  echo "    天地图密钥: 已注入（前端独立 key，按前端域名注册白名单的那把）"
  VITE_API_BASE_URL="$API_BASE_URL" VITE_TIANDITU_TK="$TIANDITU_TK" npm run build
else
  echo "    天地图密钥: 未注入 → 前端回退用后端 data/tianditu.json 下发的 key"
  VITE_API_BASE_URL="$API_BASE_URL" npm run build
fi

# 校验产物里确实写进了目标后端地址（构建没生效的话这里会报错）
if ! grep -qF "$API_BASE_URL" "$REPO/webfront/dist/assets/"*.js 2>/dev/null; then
  echo "    !! 警告：产物中找不到 $API_BASE_URL，前端可能仍指向旧地址" >&2
fi

# ---- 2. 后端发布（含进程内六角格生成，不再有独立 GisApi 进程）-----------------
echo "==> 2/4 发布后端 (linux-x64)"
cd "$REPO/server"
# --artifacts-path 指向全新目录：Windows 上 MSBuild 的 Copy 任务在覆盖
# 已存在文件时会栈溢出（MSB4166），全新目录从根本上避开"覆盖"这个触发条件。
dotnet publish HexFront.Api/HexFront.Api.csproj \
  -c Release -r linux-x64 --no-self-contained \
  -o "$PUBLISH_DIR" \
  --artifacts-path "$ART_DIR" \
  --disable-build-servers

# ---- 3. 合并数据库 WAL -----------------------------------------------------
# SQLite 的 -wal 里可能还有未合并的事务。不合并直接打包，服务器上打开
# 会自动回放 WAL（数据不丢），但 -wal/-shm 文件名会被打包规则排除，
# 导致服务器首次启动看不到最近的数据。故先 checkpoint 合并进主库。
echo "==> 3/4 合并数据库 WAL"
cd "$REPO/server/HexFront.Api"
if command -v sqlite3 >/dev/null 2>&1; then
  sqlite3 app.db "PRAGMA wal_checkpoint(TRUNCATE);" || true
  echo "    完整性: $(sqlite3 app.db 'PRAGMA quick_check;' 2>/dev/null | head -1)"
else
  echo "    (未找到 sqlite3，跳过；服务器首次启动会自动回放 WAL)"
fi

# ---- 4. 打包 zip -----------------------------------------------------------
echo "==> 4/4 打包 zip（仅前端+后端+部署物料，不含地图数据）"
cd "$REPO"
python3 "$REPO/deploy/tools/pack_release.py" \
  --out "$ZIP" \
  --root hexfront-release \
  --web "$REPO/webfront/dist" \
  --publish "$PUBLISH_DIR" \
  --db "$REPO/server/HexFront.Api/app.db" \
  --deploy "$REPO/deploy/release"

echo ""
echo "==> 发布包: $ZIP"
ls -lh "$ZIP" | awk '{print "    大小:", $5}'
echo "    解压后根目录: hexfront-release/"
