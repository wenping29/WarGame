# HEXFRONT 部署指南（Linux）

> 适用：把兵棋对抗系统 HEXFRONT-WEB 部署到 Linux 服务器。
> 架构结论：**单进程部署** —— 一个 ASP.NET Core 进程同时托管 前端静态文件(`wwwroot`)、REST(`/api`)、WebSocket(`/ws`)，对外只暴露 5018 端口，无需 nginx。后端为 **.NET 10 依赖运行时** 包（服务器需装 .NET 10 运行时）。地图数据（约 2.8G）打进同一个部署包。

---

## 1. 部署架构

```
浏览器 ──http://host:5018──▶ ASP.NET Core (HexFront.Api, 单进程)
                                ├─ wwwroot/        ← React 构建产物（同源 /api、/ws）
                                ├─ /api/*          ← REST 控制器
                                ├─ /ws             ← 原生 WebSocket 会话
                                ├─ data/           ← 地图数据（运行时读取，含 dem/gis/hexgrids/scenarios…）
                                └─ server/app.db  ← 种子应用库（用户/角色/菜单，运行时写入）
```

- 前端构建产物 `webfront/dist/` 在打包时被拷贝进 `server/src/HexFront.Api/wwwroot/`，`dotnet publish` 会把它带进发布输出；`Program.cs` 在 `wwwroot` 存在时启用静态托管与路径路由 fallback（`/room/XXXX/red` 刷新不 404）。
- 后端用环境变量 `HF_REPO_ROOT` 定位 `data/` 与 `app.db`，**不再依赖"向上 6 层"的硬编码层级**（已在 `RoomService.GetRepoRoot` / `AppDbService.DefaultDbPath` 中支持 `HF_REPO_ROOT`、`HF_DATA_DIR`），因此包可解到任意目录。

---

## 2. 部署数据清单

### 2.1 打进部署包的内容

| 路径 | 大小(约) | 说明 |
|---|---|---|
| `server/app.db` | 100 MB | 种子应用库（用户/角色/菜单/会话；运行时写入推演流水与存档索引） |
| `server/.../publish/` | 数 MB | 后端发布输出（linux-x64，依赖运行时），内含 `wwwroot/` 前端 |
| `data/dem/` | **2.6 GB** | 数字高程模型（地形），占数据绝大多数 |
| `data/gis/` | 124 MB | GIS 数据包（真实栅格生成产出与缓存） |
| `data/landcover/` | 25 MB | 土地覆盖 |
| `data/water/` | 31 MB | 水体数据 |
| `data/airports/` | 6.5 MB | 机场 |
| `data/hexgrids/` | 1.2 MB | 六角格网络包（每网格一个 zip） |
| `data/scenarios/` | 200 KB | 剧本（25 个 JSON） |
| `data/tianditu.example.json` | 1 KB | 天地图 token 模板（**不含真实密钥**） |
| `tools/` | — | Python 栅格生成工具 `tools/gis/hexgen.py` 等（真实地形用，可选） |

### 2.2 刻意排除的内容（不要进包）

| 路径 | 原因 |
|---|---|
| `data/tianditu.json` | **含真实天地图 token（密钥）**，禁止随包分发，需单独放到服务器 |
| `data/gis_axial_backup/`、`data/scenarios_backup_20261019/`、`data/scenario_backups/` | 开发期磁盘备份，非运行时数据 |
| `data/saves/` | 运行时生成的推演存档，初始为空，服务器会自动创建 |
| `tools/gis/__pycache__/`、`tools/gis/dem_cache_rhine.json` | Python 缓存 |
| `server/data/`、`node_modules/`、`**/bin/`、`**/obj/` | 仅测试产物 / 本地构建缓存（`server/data/` 只是测试 JSON，运行时用的是仓库根 `data/`） |

---

## 3. 密钥处理（重要）

`data/tianditu.json` 里是真实的天地图访问 token，**打包脚本已刻意剔除**。部署后请手动放到服务器：

```bash
cat > /srv/hexfront/data/tianditu.json <<'JSON'
{ "tk": "你的真实token" }
JSON
chown hexfront:hexfront /srv/hexfront/data/tianditu.json
systemctl restart hexfront
```

不放也能启动（GIS 降级为合成地形），但真实地形高程/覆盖会缺失。

---

## 4. 在开发机构建部署包

要求本机有 `node`(构建前端) 与 `dotnet 10`(发布后端)，在 **Git Bash** 下运行：

```bash
bash scripts/deploy/build-package.sh
# 增量重试可跳过耗时步骤：
#   SKIP_FRONTEND_BUILD=1 bash scripts/deploy/build-package.sh
#   SKIP_BACKEND_PUBLISH=1 bash scripts/deploy/build-package.sh
```

产出：`scripts/deploy/out/hexfront-deploy-YYYYMMDD-HHMMSS.tar.gz`（约 2.8G）。

> 注：因数据量大，`tar` 直接基于源路径打包（不做二次拷贝）。`npm ci` 与 `dotnet publish` 需要网络。

---

## 5. 在服务器部署

```bash
# 传包
scp scripts/deploy/out/hexfront-deploy-*.tar.gz user@server:/tmp/

# 在服务器（root）执行
ssh user@server
sudo bash server-setup.sh /tmp/hexfront-deploy-*.tar.gz        # 默认装到 /srv/hexfront
# 或指定安装根： sudo bash server-setup.sh <包> /opt/hexfront
```

`server-setup.sh` 会：解包 → 写 `hexfront.env` → 安装 .NET 10 运行时（若缺失）→ 建 `hexfront` 系统用户 → 注册并启动 `hexfront.service`。

完成后访问 `http://<服务器IP>:5018`。

---

## 6. 环境变量一览

| 变量 | 默认值/说明 |
|---|---|
| `HF_REPO_ROOT` | 解包根目录（如 `/srv/hexfront`）。后端据此定位 `data/` 与 `app.db` |
| `HF_DATA_DIR` | 覆盖 `data/` 位置（数据单独挂盘时用） |
| `HF_APP_DB` | 覆盖应用库路径（默认 `<HF_REPO_ROOT>/server/app.db`） |
| `ASPNETCORE_ENVIRONMENT` | `Production`（关闭 Swagger） |
| `ASPNETCORE_URLS` | `http://0.0.0.0:5018` |
| `HF_ALLOWED_ORIGINS` | 仅当浏览器直连 `:5018` 且不同源时需要（单进程同源可留空） |
| `HEXGEN_PYTHON` | 指定 Python 解释器（真实栅格地形；缺省自动探测） |

---

## 7. 排错

- **端口 5018 被占用**：`lsof -i:5018`；或改 `hexfront.env` 的 `ASPNETCORE_URLS` 后 `systemctl restart hexfront`。
- **前端白屏 / 刷新 404**：确认 `publish/wwwroot/index.html` 存在（`Program.cs` 的 `MapFallbackToFile` 依赖它）。
- **地图无地形**：检查 `data/dem/` 是否在包内；是否需要 `python3-gdal`（`tools/gis/hexgen.py`）。
- **`data` 或 `app.db` 找不到（旧版目录层级坑）**：确认 `hexfront.env` 里 `HF_REPO_ROOT` 指向解包根（含 `data/` 与 `server/` 同级的目录）。
- **查看日志**：`journalctl -u hexfront -f`。

---

## 8. 可选：前面挂 nginx（公网 HTTPS）

单进程已可直接对外。若需 TLS / 静态缓存，可在 5018 前加 nginx，后端改监听 `127.0.0.1:5018`：

```nginx
server {
    listen 443 ssl; server_name your.domain;
    ssl_certificate ...; ssl_certificate_key ...;
    location /api/  { proxy_pass http://127.0.0.1:5018; proxy_set_header Host $host; }
    location /ws    { proxy_pass http://127.0.0.1:5018; proxy_http_version 1.1;
                      proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade"; }
    location /      { root /srv/hexfront/server/src/HexFront.Api/bin/Release/net10.0/linux-x64/publish/wwwroot;
                      try_files $uri $uri/ /index.html; }
}
```

此时浏览器同源访问 `https://your.domain`，无需设 `HF_ALLOWED_ORIGINS`。
