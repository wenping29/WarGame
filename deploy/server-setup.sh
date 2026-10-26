#!/usr/bin/env bash
#
# server-setup.sh —— HEXFRONT 在 Linux 服务器上的部署脚本（在目标服务器运行）
#
# 前置：一个由 build-package.sh 生成的 hexfront-deploy-*.tar.gz
# 功能：
#   1. 解包到 /srv/hexfront（可用 $2 改路径）
#   2. 写入环境变量文件（HF_REPO_ROOT 等）
#   3. 安装 .NET 10 运行时（若未装；可选 Python3+GDAL 用于真实地形）
#   4. 生成 systemd 单元并启动
#   5. 提示放置天地图密钥
#
# 用法：
#   sudo bash server-setup.sh <部署包.tar.gz> [安装根目录，默认 /srv/hexfront]
#
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo "请用 root 运行：sudo bash $0 <包> [安装目录]" >&2
  exit 1
fi

PKG="${1:-}"
if [ -z "$PKG" ] || [ ! -f "$PKG" ]; then
  echo "用法: sudo bash $0 <hexfront-deploy-*.tar.gz> [安装根目录]" >&2
  exit 1
fi
PKG="$(readlink -f "$PKG")"

INSTALL_ROOT="${2:-/srv/hexfront}"

echo "==> 部署包: $PKG"
echo "==> 安装根: $INSTALL_ROOT"

# ---------- 1) 解包 ----------
echo "==> 1/5 解包 ..."
mkdir -p "$INSTALL_ROOT"
tar -xzf "$PKG" -C "$INSTALL_ROOT"
# tar 内顶层为 hexfront/，若解包后多了一层则摊平
if [ -d "$INSTALL_ROOT/hexfront" ] && [ "$(ls -A "$INSTALL_ROOT" | wc -l)" -eq 1 ]; then
  mv "$INSTALL_ROOT/hexfront/"* "$INSTALL_ROOT/"
  rmdir "$INSTALL_ROOT/hexfront"
fi

PUBLISH="$INSTALL_ROOT/server/HexFront.Api/bin/Release/net10.0/linux-x64/publish"
if [ ! -f "$PUBLISH/HexFront.Api.dll" ]; then
  echo "    错误：未找到 $PUBLISH/HexFront.Api.dll，包结构异常。" >&2
  exit 1
fi

# ---------- 2) 环境变量文件 ----------
echo "==> 2/5 写入环境变量 $INSTALL_ROOT/hexfront.env ..."
mkdir -p "$INSTALL_ROOT/server/data/saves"
cat > "$INSTALL_ROOT/hexfront.env" <<EOF
# HEXFRONT 运行环境变量（由 server-setup.sh 生成）
HF_REPO_ROOT=$INSTALL_ROOT
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:5018

# —— 可选覆盖 ——
# 把地图数据单独挂到别的盘时：
# HF_DATA_DIR=/data/hexfront
# 公网直接访问 5018 端口（非同源）时，填写浏览器来源以放开 CORS：
# HF_ALLOWED_ORIGINS=https://your.domain
EOF

# ---------- 3) 依赖：.NET 10 运行时 ----------
echo "==> 3/5 检查 .NET 运行时 ..."
if command -v dotnet >/dev/null 2>&1; then
  echo "    已检测到 dotnet: $(dotnet --version)"
  DOTNET_BIN="$(command -v dotnet)"
else
  echo "    未检测到 dotnet，使用 dotnet-install.sh 安装 .NET 10 运行时 ..."
  export DOTNET_INSTALL_DIR="/usr/share/dotnet"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir "$DOTNET_INSTALL_DIR"
  ln -sf "$DOTNET_INSTALL_DIR/dotnet" /usr/bin/dotnet
  DOTNET_BIN="/usr/bin/dotnet"
fi

# 可选：真实地形生成需要 Python3 + GDAL（缺省时降级为合成地形，不影响启动）
if command -v python3 >/dev/null 2>&1; then
  echo "    检测到 python3: $(python3 --version 2>&1)"
  if python3 -c "import osgeo" >/dev/null 2>&1; then
    echo "    已安装 GDAL (osgeo)。"
  else
    echo "    提示：未检测到 GDAL，真实栅格地形将降级为合成地形。需要时在服务器执行："
    echo "          apt-get install -y python3-gdal   # Debian/Ubuntu"
  fi
else
  echo "    提示：未安装 python3，真实栅格地形将降级为合成地形（不影响启动）。"
fi

# ---------- 4) 系统用户 + 权限 ----------
echo "==> 4/5 创建运行用户 hexfront 并设置权限 ..."
id -u hexfront >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin hexfront
chown -R hexfront:hexfront "$INSTALL_ROOT"

# ---------- 5) systemd 单元 ----------
echo "==> 5/5 注册 systemd 服务 hexfront.service ..."
DOTNET_EXEC="$DOTNET_BIN"
# 若 dotnet 指向脚本包装，取真实路径
[ -L "$DOTNET_EXEC" ] && DOTNET_EXEC="$(readlink -f "$DOTNET_EXEC")"
cat > /etc/systemd/system/hexfront.service <<EOF
[Unit]
Description=HEXFRONT Wargame Backend (ASP.NET Core, single process: API + /ws + wwwroot)
After=network.target

[Service]
WorkingDirectory=$PUBLISH
EnvironmentFile=$INSTALL_ROOT/hexfront.env
ExecStart=$DOTNET_EXEC $PUBLISH/HexFront.Api.dll
Restart=always
RestartSec=5
User=hexfront
Group=hexfront

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now hexfront.service

echo ""
echo "==> 部署完成。等待 3 秒检查状态 ..."
sleep 3
systemctl status hexfront.service --no-pager || true

echo ""
echo "==> 访问地址： http://<本机IP>:5018   （单进程已同时托管前端 /api /ws）"
echo ""
echo "==> ⚠️ 密钥处理：把真实天地图 token 放到以下文件（包内已刻意剔除）："
echo "      $INSTALL_ROOT/server/data/tianditu.json"
echo '      内容格式： { "tk": "你的真实token" }'
echo "      放好后重启服务： systemctl restart hexfront"
echo ""
echo "==> 排错："
echo "      日志： journalctl -u hexfront -f"
echo "      端口被占用： lsof -i:5018  或改 hexfront.env 里的 ASPNETCORE_URLS"
