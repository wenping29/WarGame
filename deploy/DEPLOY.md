# HEXFRONT 部署指南（Linux）

> 适用：把兵棋对抗系统 HEXFRONT-WEB 部署到 Linux 服务器。
> 架构结论：**单进程部署** —— 一个 ASP.NET Core 进程同时托管 前端静态文件(`wwwroot`)、REST(`/api`)、WebSocket(`/ws`)，对外只暴露 5018 端口，无需 nginx。后端为 **.NET 10 依赖运行时** 包（服务器需装 .NET 10 运行时）。地图数据（约 2.1G）打进同一个部署包。

---

## 1. 部署架构

```
浏览器 ──http://host:5018──▶ ASP.NET Core (HexFront.Api, 单进程)
                                ├─ server/HexFront.Api/wwwroot/  ← React 构建产物（同源 /api、/ws）
                                ├─ /api/*          ← REST 控制器
                                ├─ /ws             ← 原生 WebSocket 会话
                                ├─ server/data/    ← 地图数据（运行时读取，含 dem/gis/hexgrids/scenarios…）
                                └─ server/HexFront.Api/app.db  ← 种子应用库（用户/角色/菜单，运行时写入）
```

- 前端构建产物 `webfront/dist/` 在打包时被拷贝进 `server/HexFront.Api/wwwroot/`，`dotnet publish` 会把它带进发布输出；`Program.cs` 在 `wwwroot` 存在时启用静态托管与路径路由 fallback（`/room/XXXX/red` 刷新不 404）。
- 后端用环境变量 `HF_REPO_ROOT` 定位 `server/`（进而 `data/` 与 `app.db`），**不再依赖"向上 6 层"的硬编码层级**（已在 `AppDbService.ServerRoot` / `AppDbService.DefaultDbPath` 中支持 `HF_REPO_ROOT`、`HF_DATA_DIR`），因此包可解到任意目录。

---

## 2. 部署数据清单

### 2.1 打进部署包的内容

| 路径 | 大小(约) | 说明 |
|---|---|---|
| `server/HexFront.Api/app.db` | 100 MB | 种子应用库（用户/角色/菜单/会话；运行时写入推演流水与存档索引） |
| `server/HexFront.Api/bin/Release/net10.0/linux-x64/publish/` | 数 MB | 后端发布输出（linux-x64，依赖运行时），内含 `wwwroot/` 前端 |
| `server/data/dem/` | **约 2.1 GB** | 数字高程模型（地形），占数据绝大多数 |
| `server/data/gis/` | 124 MB | GIS 数据包（真实栅格生成产出与缓存） |
| `server/data/landcover/` | 25 MB | 土地覆盖 |
| `server/data/water/` | 31 MB | 水体数据 |
| `server/data/airports/` | 6.5 MB | 机场 |
| `server/data/hexgrids/` | 1.2 MB | 六角格网络包（每网格一个 zip） |
| `server/data/scenarios/` | 200 KB | 剧本（25 个 JSON） |
| `server/data/tianditu.example.json` | 1 KB | 天地图 token 模板（**不含真实密钥**） |
| `server/tools/` | — | Python 栅格生成工具 `server/tools/gis/hexgen.py` 等（真实地形用，可选） |

### 2.2 刻意排除的内容（不要进包）

| 路径 | 原因 |
|---|---|
| `server/data/tianditu.json` | **含真实天地图 token（密钥）**，禁止随包分发，需单独放到服务器 |
| `server/data/gis_axial_backup/`、`server/data/scenarios_backup_20261019/`、`server/data/scenario_backups/` | 开发期磁盘备份，非运行时数据 |
| `server/data/saves/` | 运行时生成的推演存档，初始为空，服务器会自动创建 |
| `server/tools/gis/__pycache__/`、`server/tools/gis/dem_cache_rhine.json` | Python 缓存 |
| `node_modules/`、`**/bin/`、`**/obj/` | 仅本地构建缓存 |

---

## 3. 密钥处理（重要）

`server/data/tianditu.json` 里是真实的天地图访问 token，**打包脚本已刻意剔除**。部署后请手动放到服务器：

```bash
cat > /srv/hexfront/server/data/tianditu.json <<'JSON'
{ "tk": "你的真实token" }
JSON
chown hexfront:hexfront /srv/hexfront/server/data/tianditu.json
systemctl restart hexfront
```

不放也能启动（GIS 降级为合成地形），但真实地形高程/覆盖会缺失。

---

## 4. 在开发机构建部署包

要求本机有 `node`(构建前端) 与 `dotnet 10`(发布后端)，在 **Git Bash** 下运行：

```bash
bash deploy/build-package.sh
# 增量重试可跳过耗时步骤：
#   SKIP_FRONTEND_BUILD=1 bash deploy/build-package.sh
#   SKIP_BACKEND_PUBLISH=1 bash deploy/build-package.sh
```

产出：`deploy/out/hexfront-deploy-YYYYMMDD-HHMMSS.tar.gz`（约 2.1G）。

> 注：因数据量大，`tar` 直接基于源路径打包（不做二次拷贝）。`npm ci` 与 `dotnet publish` 需要网络。

---

## 5. 在服务器部署

```bash
# 传包
scp deploy/out/hexfront-deploy-*.tar.gz user@server:/tmp/

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
| `HF_REPO_ROOT` | 解包根目录（如 `/srv/hexfront`）。后端据此定位 `server/`（内含 `data/` 与 `HexFront.Api/app.db`） |
| `HF_DATA_DIR` | 覆盖 `server/data/` 位置（数据单独挂盘时用） |
| `HF_APP_DB` | 覆盖应用库路径（默认 `<HF_REPO_ROOT>/server/HexFront.Api/app.db`） |
| `ASPNETCORE_ENVIRONMENT` | `Production`（关闭 Swagger） |
| `ASPNETCORE_URLS` | `http://0.0.0.0:5018` |
| `HF_ALLOWED_ORIGINS` | 仅当浏览器直连 `:5018` 且不同源时需要（单进程同源可留空） |
| `HEXGEN_PYTHON` | 指定 Python 解释器（真实栅格地形；缺省自动探测） |

---

## 7. 排错

- **端口 5018 被占用**：`lsof -i:5018`；或改 `hexfront.env` 的 `ASPNETCORE_URLS` 后 `systemctl restart hexfront`。
- **前端白屏 / 刷新 404**：确认 `publish/wwwroot/index.html` 存在（`Program.cs` 的 `MapFallbackToFile` 依赖它）。
- **地图无地形**：检查 `server/data/dem/` 是否在包内；是否需要 `python3-gdal`（`server/tools/gis/hexgen.py`）。
- **`data` 或 `app.db` 找不到（旧版目录层级坑）**：确认 `hexfront.env` 里 `HF_REPO_ROOT` 指向解包根，其下含 `server/`，`server/` 内含 `data/` 与 `HexFront.Api/`（例如 `/srv/hexfront/server/data`）。
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
    location /      { root /srv/hexfront/server/HexFront.Api/bin/Release/net10.0/linux-x64/publish/wwwroot;
                      try_files $uri $uri/ /index.html; }
}
```

此时浏览器同源访问 `https://your.domain`，无需设 `HF_ALLOWED_ORIGINS`。

---

## 9. 方案 B：宝塔双站分离部署（静态网站 + .NET 网站）

适用：前端由宝塔「**静态网站**」托管，后端由宝塔「**.NET 网站**」托管，**两者分属不同域名**。

### 9.1 与单进程部署的差异（先想清楚要不要走这条路）

| | 方案 A 单进程（第 1~8 节） | 方案 B 双站分离 |
|---|---|---|
| 前端位置 | 后端发布包内的 `wwwroot` | 宝塔静态站根目录 |
| 浏览器请求 | 同源 `/api`、`/ws` | 跨域，打到后端域名 |
| 后端 CORS | 不需要 | **必须**配 `HF_ALLOWED_ORIGINS` |
| WebSocket | 同源 `wss` | 跨域 `wss`，nginx 需透传 `Upgrade` |
| 对外地址 | 一个 | 两个（前端域名 + 后端域名） |
| 要做的事 | 解包 + 起进程 | 上面全部 + 两边各配一次 SSL + 额外反代规则 |

**代价**：多一次跨域往返、两个域名各自上 SSL、nginx 多一段 WS 反代规则。
若目标只是「能访问的站点」，方案 A 更省事——发布包里前端已经内置了。

### 9.2 一次性准备

1. 两个域名解析到服务器：前端 `game.example.com`，后端 `api.example.com`
2. **两边都要 HTTPS**（宝塔 → SSL → Let's Encrypt）。原因：https 页面调 http 接口会被浏览器按「混合内容」直接拦截；https 页面也只能连 `wss` 而不能连 `ws`。
3. 装 .NET 10 运行时。宝塔 → 软件商店 →「.NET 核心」；若面板没有 10 版本，用微软官方脚本：
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
   ln -sf ~/.dotnet/dotnet /usr/bin/dotnet
   ```
4. 建系统用户与目录（后端要写 app.db 与存档目录）：
   ```bash
   sudo useradd -r -s /sbin/nologin hexfront
   sudo mkdir -p /srv/hexfront && sudo chown hexfront:hexfront /srv/hexfront
   ```

### 9.3 构建双站物料（开发机执行）

```bash
API_BASE_URL=https://api.example.com/api bash deploy/build-split.sh
```

产出三份互不重叠的包，便于分别上传与更新：

| 包 | 内容 | 去处 |
|---|---|---|
| `hexfront-web-<ts>.tar.gz` | 前端 `dist` 内容 | 宝塔静态站根目录 |
| `hexfront-api-<ts>.tar.gz` | 后端 publish + `app.db` | `/srv/hexfront/server/HexFront.Api/` |
| `hexfront-data-<ts>.tar.gz` | 地图数据（~2.1G） | `/srv/hexfront/server/data/` |

要点：
- `API_BASE_URL` **必须以 `/api` 结尾**（后端控制器前缀是 `[Route("api")]`）。该值同时决定 WebSocket 的 origin（`https` → `wss`），编译进前端产物。
- 后端发布时**不含** `wwwroot`（脚本会清掉），职责单一：后端只做 API。
- `tianditu.json`（真实天地图 token）已被剔除，见 9.7。

### 9.4 上传与放置

```bash
scp deploy/out/hexfront-*-<ts>.tar.gz user@server:/tmp/
```

服务器上：

```bash
sudo tar -xzf /tmp/hexfront-api-<ts>.tar.gz   -C /srv/hexfront     # 得到 server/HexFront.Api/{publish,app.db}
sudo tar -xzf /tmp/hexfront-data-<ts>.tar.gz  -C /srv/hexfront     # 得到 server/data
sudo chown -R hexfront:hexfront /srv/hexfront
```

前端包解到静态站根目录（`index.html` 所在那一层，**不要多套一层 `dist/`**）：

```bash
sudo mkdir -p /www/wwwroot/hexfront-web
sudo tar -xzf /tmp/hexfront-web-<ts>.tar.gz -C /www/wwwroot/hexfront-web
sudo chown -R www:www /www/wwwroot/hexfront-web
```

### 9.5 宝塔站点配置

**① 静态网站（前端）**

- 网站根目录：`/www/wwwroot/hexfront-web`
- 配置文件里把 `location` 段替换为 `deploy/bt-site-static.conf` 的内容。
  关键一条是 SPA 回落——房间页走 path 路由（`/room/XXXX/red`），刷新不回落到 `index.html` 就会 404。

**② .NET 网站（后端）**

- 项目路径：`/srv/hexfront/server/HexFront.Api/publish`
- 启动文件：`HexFront.Api.dll`
- 监听端口：`5018`（与 9.6 的 `ASPNETCORE_URLS` 一致）
- 配置文件里把 proxy 段替换为 `deploy/bt-site-dotnet.conf` 的内容。**重点核对 `/ws` 是否透传了 `Upgrade`/`Connection` 头**，漏了会导致房间连不上；`proxy_read_timeout` 也要放宽（推演是长连接，默认 60s 会被掐断）。
- 后端已由应用层处理 CORS，nginx **不要**再叠加 `Access-Control-Allow-Origin` 响应头（叠加会变成 `origin1, origin2`，浏览器判定非法而拒绝）。

### 9.6 后端环境变量

用 `deploy/bt-dotnet.env.example` 作模板，写到 `/srv/hexfront/hexfront.env`：

```ini
HF_REPO_ROOT=/srv/hexfront
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5018
HF_ALLOWED_ORIGINS=https://game.example.com
```

`HF_ALLOWED_ORIGINS` 是方案 B 的关键项，填**前端站点的 origin**（协议+域名+端口，末尾不带斜杠），不是后端地址。

托管方式二选一：

- **systemd**（推荐，环境变量完全可控）：在 `hexfront.service` 里加 `EnvironmentFile=/srv/hexfront/hexfront.env`，宝塔侧用「反向代理」站点指向 `127.0.0.1:5018`。
- **宝塔 .NET 站点直接托管**：在站点设置的「环境变量」里逐项填同名变量。

### 9.7 天地图密钥

```bash
sudo tee /srv/hexfront/server/data/tianditu.json >/dev/null <<'JSON'
{ "tk": "你的真实token" }
JSON
sudo chown hexfront:hexfront /srv/hexfront/server/data/tianditu.json
```
不放也能启动，但真实地形高程/土地覆盖会缺失（降级为合成地形）。

### 9.8 验证清单

```bash
# 1) 后端进程在跑
curl -i http://127.0.0.1:5018/api/room/XXXX        # 期望 200/404 JSON，而不是连不上
# 2) 跨域预检（模拟前端站来源），期望返回 Access-Control-Allow-Origin 且不在 403
curl -i -X OPTIONS http://api.example.com/api/room/XXXX \
  -H 'Origin: https://game.example.com' \
  -H 'Access-Control-Request-Method: POST'
# 3) 静态站 SPA 回落
curl -i https://game.example.com/room/XXXX/red      # 期望 200 + index.html，不是 404
# 4) WebSocket 握手（浏览器控制台应显示 101 Switching Protocols）
```

浏览器侧：打开 `https://game.example.com`，登录、建房间、进入房间页——控制台不应有 CORS 报错，`ws://`/`wss://` 应指向**后端**域名。

### 9.9 常见问题

| 现象 | 原因与处理 |
|---|---|
| 控制台报 CORS 错误 | `HF_ALLOWED_ORIGINS` 没配或填成了后端地址；也要确认是前端 origin（含协议） |
| 登录/列表正常，但「编辑副本单位」403 | 后端 CORS 方法表缺 `PUT`/`DELETE`（已在 `Program.cs` 补齐，**需重新发布后端**） |
| 房间页一直「连接中」，WS 不通 | nginx `/ws` 缺 `Upgrade`/`Connection` 透传，或 `proxy_read_timeout` 太短 |
| 页面报混合内容拦截 | 前端 https、后端 http；两边都要上 SSL |
| 前端请求打到前端域名的 `/api` | 构建时没注入 `VITE_API_BASE_URL`；改 `webfront/.env.production` 后重新构建 |
| 刷新房间页 404 | 静态站缺 `try_files $uri $uri/ /index.html` |
| 地图无地形 | `server/data/` 未解到位，或缺 `tianditu.json` |

