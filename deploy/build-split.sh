#!/usr/bin/env bash
#
# build-split.sh —— 宝塔双站分离部署物料构建（方案 B）
#
# 与 build-package.sh 的区别：不再把前端塞进后端 wwwroot，而是拆成两个站点各自的物料。
#   hexfront-web-<ts>.tar.gz    前端 dist                → 宝塔「静态网站」站点根目录
#   hexfront-api-<ts>.tar.gz    后端 publish + app.db     → 宝塔「.NET 网站」运行目录
#   hexfront-data-<ts>.tar.gz   地图数据 server/data      → /srv/hexfront/server/data
#
# 服务器上的目录布局（与 server-setup.sh / HF_REPO_ROOT 保持一致）：
#   /srv/hexfront/server/HexFront.Api/publish/HexFront.Api.dll   ← .NET 站点指向这个 dll
#   /srv/hexfront/server/HexFront.Api/app.db
#   /srv/hexfront/server/data/
#
# 用法：
#   API_BASE_URL=https://api.example.com/api bash deploy/build-split.sh
#
# 环境变量：
#   API_BASE_URL              前端调用后端的完整前缀，必须含 /api（后端控制器前缀是 [Route("api")]）。
#                             该值同时决定 WebSocket 的 origin（https -> wss），编译进前端产物。
#   SKIP_FRONTEND_BUILD=1     沿用已有 webfront/dist
#   SKIP_BACKEND_PUBLISH=1    沿用已有 publish 目录
#
set -euo pipefail

# 非交互环境下自动确认 npm 的批量删除等提示
export npm_config_yes=true

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"

API_BASE_URL="${API_BASE_URL:-https://api.example.com/api}"
OUT_DIR="$REPO/deploy/out"
TS="$(date +%Y%m%d-%H%M%S)"

# publish 输出目录带时间戳：每次都是全新目录，MSBuild 的 Copy 任务就只会"新建"而不会"覆盖"。
# 覆盖已存在文件时它走 DeleteNoThrow，在 Windows 上会栈溢出（Stack overflow / MSB4166），
# dotnet clean 往往清不掉 RID 子目录（bin/Release/net10.0/linux-x64/）里的旧产物。
PUBLISH_DIR="$OUT_DIR/publish-linux-x64-$TS"
ARTIFACTS_DIR="$OUT_DIR/artifacts-$TS"
STAGE_DIR="$OUT_DIR/stage"

WEB_OUT="$OUT_DIR/hexfront-web-$TS.tar.gz"
API_OUT="$OUT_DIR/hexfront-api-$TS.tar.gz"
DATA_OUT="$OUT_DIR/hexfront-data-$TS.tar.gz"

mkdir -p "$OUT_DIR"

echo "==> 仓库根   : $REPO"
echo "==> 后端地址 : $API_BASE_URL"
echo "    （前端 REST 用它；WebSocket 自动取其 origin，即 $(echo "$API_BASE_URL" | sed -E 's#^([a-z]+)://([^/]+).*#\1://\2#')）"
echo ""

# ---------- 1) 前端构建（注入 VITE_API_BASE_URL）----------
if [ "${SKIP_FRONTEND_BUILD:-0}" = "1" ]; then
  echo "==> [跳过] 前端构建（SKIP_FRONTEND_BUILD=1）"
else
  echo "==> 1/3 构建前端 webfront ..."
  cd "$REPO/webfront"
  if [ ! -d node_modules ] || [ "${FORCE_NPM_INSTALL:-0}" = "1" ]; then
    npm ci --yes
  else
    echo "    node_modules 已存在，跳过 npm 安装（FORCE_NPM_INSTALL=1 可强制重装）"
  fi
  # vite 会把 VITE_ 前缀的环境变量编译进产物
  VITE_API_BASE_URL="$API_BASE_URL" npm run build
  echo "    构建完成，dist 大小：$(du -sh dist 2>/dev/null | cut -f1)"
fi

if [ ! -f "$REPO/webfront/dist/index.html" ]; then
  echo "    错误：webfront/dist/index.html 不存在，前端产物无效。" >&2
  exit 1
fi

# ---------- 2) 后端 publish（linux-x64，依赖运行时）----------
if [ "${SKIP_BACKEND_PUBLISH:-0}" = "1" ]; then
  echo "==> [跳过] 后端发布（SKIP_BACKEND_PUBLISH=1，沿用已有 publish 目录）"
  # 复用最近一次产出的 publish 目录（目录名带时间戳）
  PUBLISH_DIR="$(ls -dt "$OUT_DIR"/publish-linux-x64-* 2>/dev/null | head -1)"
  if [ -z "$PUBLISH_DIR" ] || [ ! -f "$PUBLISH_DIR/HexFront.Api.dll" ]; then
    echo "    错误：找不到可复用的 publish 目录，请先不带 SKIP_BACKEND_PUBLISH 运行一次。" >&2
    exit 1
  fi
  echo "    沿用：$PUBLISH_DIR"
else
  echo "==> 2/3 发布后端 (dotnet publish -c Release -r linux-x64 --no-self-contained) ..."
  cd "$REPO/server"
  # MSBuild 的 Copy 任务在"覆盖已存在文件"时走 DeleteNoThrow，在 Windows 上会栈溢出
  # （Stack overflow / MSB4166 子节点过早退出），publish 拷 HexFront.Core.dll 时崩。
  # dotnet clean 清不掉 RID 子目录（bin|obj/Release/net10.0/linux-x64/）里的旧产物，
  # 所以把整个 artifacts（obj + bin）重定向到全新目录，Copy 就只会新建、不会删除。
  dotnet publish HexFront.Api/HexFront.Api.csproj -c Release -r linux-x64 --no-self-contained \
    -o "$PUBLISH_DIR" --artifacts-path "$ARTIFACTS_DIR" --disable-build-servers
fi

# 方案 B：前端由静态站托管，后端只做 API。publish 里若残留 wwwroot 会启用静态托管+路由回落，
# 访问后端域名会误显示前端页面，这里清掉让职责单一。
if [ -d "$PUBLISH_DIR/wwwroot" ]; then
  echo "    移除 publish/wwwroot（方案B：前端由宝塔静态站托管）"
  rm -rf "$PUBLISH_DIR/wwwroot"
fi
if [ ! -f "$PUBLISH_DIR/HexFront.Api.dll" ]; then
  echo "    错误：publish 目录缺少 HexFront.Api.dll。" >&2
  exit 1
fi

# ---------- 3) 打包三份物料 ----------
echo "==> 3/3 打包 ..."
mkdir -p "$OUT_DIR"

# 3.1 前端：解出来就是 index.html 所在的层，直接放宝塔静态站根目录
tar -czf "$WEB_OUT" -C "$REPO/webfront/dist" .
echo "    前端包：$WEB_OUT  ($(du -h "$WEB_OUT" | cut -f1))"

# 3.2 后端：解到 /srv/hexfront 得到 server/HexFront.Api/{publish,app.db}
rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR/server/HexFront.Api"
cp -r "$PUBLISH_DIR" "$STAGE_DIR/server/HexFront.Api/publish"
cp "$REPO/server/HexFront.Api/app.db" "$STAGE_DIR/server/HexFront.Api/app.db"
tar -czf "$API_OUT" -C "$STAGE_DIR" server
rm -rf "$STAGE_DIR"
echo "    后端包：$API_OUT  ($(du -h "$API_OUT" | cut -f1))"

# 3.3 数据：体积大（约 2.1G），单独一包便于分开上传/更新
#   排除项与 build-package.sh 一致：开发期备份、运行时 saves、真实密钥 tianditu.json
tar -czf "$DATA_OUT" -C "$REPO" \
  --exclude='server/data/gis_axial_backup' \
  --exclude='server/data/scenarios_backup_20261019' \
  --exclude='server/data/scenario_backups' \
  --exclude='server/data/saves' \
  --exclude='server/data/terrain_test' \
  --exclude='server/data/tianditu.json' \
  "server/data"
echo "    数据包：$DATA_OUT  ($(du -h "$DATA_OUT" | cut -f1))"

echo ""
echo "==> 全部完成。上传到服务器后："
echo "    1) 后端+数据 → 解到 /srv/hexfront（宝塔 .NET 站点指向 server/HexFront.Api/publish/HexFront.Api.dll）"
echo "    2) 前端     → 解到宝塔静态网站根目录（index.html 所在层）"
echo "    3) 密钥     → 手工放 /srv/hexfront/server/data/tianditu.json（打包已剔除）"
echo ""
echo "    scp $WEB_OUT $API_OUT $DATA_OUT user@server:/tmp/"
