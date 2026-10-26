#!/usr/bin/env bash
#
# server-setup.sh —— 在 Linux 服务器上部署 HEXFRONT 单进程包（方案 A）
#
# 用法：
#   sudo bash server-setup.sh /tmp/hexfront-deploy-<时间戳>.tar.gz [安装目录]
#
# 默认安装目录 /srv/hexfront。脚本会：
#   1. 解包部署包到安装目录
#   2. 找到 publish 目录与 dll
#   3. 写入环境变量文件（hexfront.env）
#   4. 生成 systemd 服务（可选，用 INSTALL_SYSTEMD=1）
#
# 解包后的目录结构：
#   /srv/hexfront/server/HexFront.Api/app.db
#   /srv/hexfront/server/HexFront.Api/bin/Release/net10.0/linux-x64/publish/HexFront.Api.dll
#   /srv/hexfront/server/data/
#
# 环境变量：
#   INSTALL_ROOT    安装目录，默认 /srv/hexfront
#   INSTALL_SYSTEMD 设为 1 时生成并启用 systemd 服务
#   SERVICE_USER    systemd 服务的运行用户，默认 www
#
set -euo pipefail

PKG="${1:-}"
INSTALL_ROOT="${2:-${INSTALL_ROOT:-/srv/hexfront}}"
SERVICE_USER="${SERVICE_USER:-www}"

if [ -z "$PKG" ]; then
  echo "用法: sudo bash server-setup.sh <部署包.tar.gz> [安装目录]" >&2
  exit 1
fi
if [ ! -f "$PKG" ]; then
  echo "错误：找不到部署包 $PKG" >&2
  exit 1
fi

echo "==> 安装目录: $INSTALL_ROOT"

mkdir -p "$INSTALL_ROOT"

# ---------- 1) 解包 ----------
echo "==> 1/4 解包 $PKG ..."
tar -xzf "$PKG" -C "$INSTALL_ROOT"

# ---------- 2) 定位 publish 目录 ----------
echo "==> 2/4 定位后端 publish 目录 ..."
# 包内有两种可能结构（兼容历史包）：
#   新：server/HexFront.Api/publish/            （方案 B 的 api 包）
#   旧：server/HexFront.Api/bin/.../publish/     （方案 A 单包）
PUBLISH=""
for cand in \
  "$INSTALL_ROOT/server/HexFront.Api/publish" \
  "$INSTALL_ROOT/server/HexFront.Api/bin/Release/net10.0/linux-x64/publish" \
  "$INSTALL_ROOT/publish" ; do
  if [ -f "$cand/HexFront.Api.dll" ]; then
    PUBLISH="$cand"
    break
  fi
done
# 兜底：任意层级下找 HexFront.Api.dll
if [ -z "$PUBLISH" ]; then
  FOUND="$(find "$INSTALL_ROOT" -name 'HexFront.Api.dll' -type f 2>/dev/null | head -1)"
  if [ -n "$FOUND" ]; then
    PUBLISH="$(dirname "$FOUND")"
  fi
fi
if [ -z "$PUBLISH" ]; then
  echo "    错误：包里找不到 HexFront.Api.dll，请检查部署包是否完整。" >&2
  exit 1
fi
echo "    publish: $PUBLISH"

# 数据目录：标准布局是 server/data，也兼容 server/HexFront.Api/data
DATA_DIR=""
for cand in "$INSTALL_ROOT/server/data" "$INSTALL_ROOT/server/HexFront.Api/data"; do
  if [ -d "$cand" ]; then DATA_DIR="$cand"; break; fi
done
mkdir -p "$INSTALL_ROOT/server/data/saves"

# ---------- 3) 环境变量 ----------
echo "==> 3/4 写入环境变量 $INSTALL_ROOT/hexfront.env ..."
cat > "$INSTALL_ROOT/hexfront.env" <<EOF
# HEXFRONT 运行环境变量（由 server-setup.sh 生成）
#
# HF_REPO_ROOT 指向「解压根」——它的下面有 server/ 子目录。
# 也可以直接指向 server 目录本身（后端两种写法都认），但请保持全篇一致。
HF_REPO_ROOT=$INSTALL_ROOT
ASPNETCORE_ENVIRONMENT=Production

# 监听地址：端口也可改 publish/../appsettings.json 的 "Urls" 键，
# 但本行存在时以本行为准（ASP.NET Core 的环境变量优先级高于配置文件）。
# 单进程直连对外时用 0.0.0.0；前面挂 nginx 反代时用 127.0.0.1。
ASPNETCORE_URLS=http://0.0.0.0:5018

# 允许的前端来源（逗号分隔）。同源部署不需要设；
# 前后端分域名部署时，必须填前端站点的 origin（不含路径、结尾不带 /）。
# HF_ALLOWED_ORIGINS=https://game.example.com

# 覆盖应用库位置（默认 <HF_REPO_ROOT>/server/HexFront.Api/app.db）
# HF_APP_DB=

# 地图数据单独挂盘时用（默认 <HF_REPO_ROOT>/server/data）
# HF_DATA_DIR=

# 存档目录（默认 <HF_REPO_ROOT>/server/data/saves）
# HF_SAVE_DIR=
EOF
echo "    已写入 $INSTALL_ROOT/hexfront.env"

# ---------- 4) systemd（可选） ----------
if [ "${INSTALL_SYSTEMD:-0}" = "1" ]; then
  echo "==> 4/4 生成 systemd 服务 hexfront.service ..."
  DOTNET_BIN="$(command -v dotnet || echo /usr/bin/dotnet)"
  cat > /etc/systemd/system/hexfront.service <<EOF
[Unit]
Description=HexFront backend (ASP.NET Core)
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
WorkingDirectory=$PUBLISH
EnvironmentFile=$INSTALL_ROOT/hexfront.env
ExecStart=$DOTNET_BIN $PUBLISH/HexFront.Api.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=hexfront

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable hexfront
  systemctl restart hexfront
  echo "    已启动：systemctl status hexfront"
else
  echo "==> 4/4 [跳过] systemd（需要时用 INSTALL_SYSTEMD=1 重新执行本脚本）"
fi

echo ""
echo "==> 部署完成。目录结构："
echo "    $INSTALL_ROOT/server/HexFront.Api/app.db"
echo "    $PUBLISH/HexFront.Api.dll"
echo "    $DATA_DIR"
echo ""
echo "==> 密钥文件（天地图 token，不随包分发，需手动创建）："
echo "    $INSTALL_ROOT/server/data/tianditu.json  ->  { \"tk\": \"你的token\" }"
echo ""
echo "==> 启动（systemd 方式）："
echo "    systemctl start hexfront && journalctl -u hexfront -f"
echo "==> 或手动前台运行（看实时日志）："
echo "    cd $PUBLISH && set -a && source $INSTALL_ROOT/hexfront.env && set +a && dotnet HexFront.Api.dll"
