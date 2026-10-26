#!/usr/bin/env bash
#
# build-package.sh —— HEXFRONT 部署包构建脚本（在开发机 Windows/Git Bash 下运行）
#
# 产物：一个 tar.gz 部署包，内含
#   hexfront/
#     server/HexFront.Api/app.db                                 种子应用库
#     server/HexFront.Api/bin/Release/net10.0/linux-x64/publish/  后端(linux-x64, 依赖运行时)
#       └─ wwwroot/                                          React 构建产物（由 webfront/dist 拷贝）
#     server/data/                                          地图数据（约 2.1G，已剔除备份与密钥）
#     server/tools/                                         Python 栅格生成工具
#
# 服务器上解包到 /srv/hexfront 后，设置 HF_REPO_ROOT=/srv/hexfront 即可，
# 不再依赖"向上6层"的目录层级假设（后端用 HF_REPO_ROOT/HF_DATA_DIR 支持）。
#
# 用法：
#   bash deploy/build-package.sh
# 可选环境变量（用于增量重试，跳过耗时的步骤）：
#   SKIP_FRONTEND_BUILD=1   不重新 npm build（沿用已有 webfront/dist）
#   SKIP_BACKEND_PUBLISH=1  不重新 dotnet publish（沿用已有 publish 目录）
#
set -euo pipefail

# 非交互环境下自动确认 npm 的批量删除等提示
export npm_config_yes=true

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"

PUBLISH_DIR="$REPO/server/HexFront.Api/bin/Release/net10.0/linux-x64/publish"
OUT_DIR="$REPO/deploy/out"
mkdir -p "$OUT_DIR"
OUT="$OUT_DIR/hexfront-deploy-$(date +%Y%m%d-%H%M%S).tar.gz"

echo "==> 仓库根: $REPO"

# ---------- 1) 前端构建 ----------
if [ "${SKIP_FRONTEND_BUILD:-0}" = "1" ]; then
  echo "==> [跳过] 前端构建（SKIP_FRONTEND_BUILD=1）"
else
  echo "==> 1/3 构建前端 webfront ..."
  cd "$REPO/webfront"
  # node_modules 已存在且 lockfile 未变时跳过重装，避免 npm ci 的批量删除确认卡住；
  # 强制重装用 FORCE_NPM_INSTALL=1。
  if [ ! -d node_modules ] || [ "${FORCE_NPM_INSTALL:-0}" = "1" ]; then
    npm ci --yes
  else
    echo "    node_modules 已存在，跳过 npm 安装（FORCE_NPM_INSTALL=1 可强制重装）"
  fi
  npm run build
  echo "    构建完成，dist 大小：$(du -sh dist 2>/dev/null | cut -f1)"
fi

# ---------- 2) 把 dist 拷进后端 wwwroot（ASP.NET 单进程托管静态前端）----------
echo "==> 2/3 同步 dist -> server/HexFront.Api/wwwroot ..."
WWROOT="$REPO/server/HexFront.Api/wwwroot"
rm -rf "$WWROOT"
mkdir -p "$WWROOT"
cp -r "$REPO/webfront/dist/." "$WWROOT/"
echo "    wwwroot 大小：$(du -sh "$WWROOT" 2>/dev/null | cut -f1)"

# ---------- 3) 后端 publish (linux-x64, framework-dependent) ----------
if [ "${SKIP_BACKEND_PUBLISH:-0}" = "1" ]; then
  echo "==> [跳过] 后端发布（SKIP_BACKEND_PUBLISH=1，沿用 $PUBLISH_DIR）"
  if [ ! -f "$PUBLISH_DIR/HexFront.Api.dll" ]; then
    echo "    错误：publish 目录不存在，请先不带 SKIP_BACKEND_PUBLISH 运行一次。" >&2
    exit 1
  fi
else
  echo "==> 3/3 发布后端 (dotnet publish -c Release -r linux-x64 --no-self-contained) ..."
  cd "$REPO/server"
  # Windows 上 MSBuild 的 Copy 任务在并行拷贝时会栈溢出（Stack overflow / MSB4166 子节点过早退出），
  # 表现为 publish 拷 HexFront.Core.dll 到 Api 输出时崩掉、脚本半途退出。
  # 先 clean 掉增量产物再 publish，可稳定通过。
  dotnet clean HexFront.Api/HexFront.Api.csproj -c Release --disable-build-servers -v q
  dotnet publish HexFront.Api/HexFront.Api.csproj -c Release -r linux-x64 --no-self-contained -o "$PUBLISH_DIR" --disable-build-servers
fi

# 校验 wwwroot 确实进了 publish 输出（否则静态前端不会生效）
if [ ! -d "$PUBLISH_DIR/wwwroot" ]; then
  echo "    警告：publish 输出缺少 wwwroot，前端静态托管将不生效，请检查构建。" >&2
else
  echo "    publish/wwwroot 大小：$(du -sh "$PUBLISH_DIR/wwwroot" 2>/dev/null | cut -f1)"
fi

# ---------- 4) 打包（直接 tar 源路径，避免二次拷贝）----------
echo "==> 4/4 打包为 $OUT ..."
mkdir -p "$OUT_DIR"

# 排除项说明（路径相对 $REPO）：
#   server/data/gis_axial_backup, server/data/scenarios_backup_20261019, server/data/scenario_backups —— 开发期备份，非运行时数据
#   server/data/saves                                                         —— 运行时生成，初始为空
#   server/data/tianditu.json                                                 —— 含真实天地图 token，禁止随包分发（见 DEPLOY.md 密钥处理）
#   server/tools/gis/__pycache__, dem_cache_rhine.json                       —— Python 缓存
tar -czf "$OUT" -C "$REPO" \
  --transform 's,^,hexfront/,' \
  --exclude='server/data/gis_axial_backup' \
  --exclude='server/data/scenarios_backup_20261019' \
  --exclude='server/data/scenario_backups' \
  --exclude='server/data/saves' \
  --exclude='server/data/terrain_test' \
  --exclude='server/data/tianditu.json' \
  --exclude='server/tools/gis/__pycache__' \
  --exclude='server/tools/gis/dem_cache_rhine.json' \
  --exclude='*/__pycache__' \
  "server/HexFront.Api/app.db" \
  "server/HexFront.Api/bin/Release/net10.0/linux-x64/publish" \
  "server/data" \
  "server/tools"

echo ""
echo "==> 部署包已生成："
ls -lh "$OUT"
echo ""
echo "==> 包内顶层结构（条目数）："
tar -tzf "$OUT" | sed 's#/.*##' | sort | uniq -c
echo ""
echo "==> 下一步：把该 tar.gz 传到 Linux 服务器，执行 server-setup.sh："
echo "    scp $(basename "$OUT") user@server:/tmp/"
echo "    ssh user@server 'sudo bash server-setup.sh /tmp/$(basename "$OUT")'"
