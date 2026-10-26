# HexFront 前后端分离部署手册（宝塔双站）

> 适用于本发布包 `hexfront-release-<时间戳>.zip`。
> 结构：**宝塔「静态网站」托管前端** + **宝塔「.NET 网站」托管后端**，两个域名、两个进程。

---

## 0. 发布包结构

```
hexfront-release/
├── web/                              ← 前端静态产物（宝塔「静态网站」根目录内容）
│   ├── index.html
│   └── assets/…
│
├── server/                           ← 后端（宝塔「.NET 网站」）
│   ├── HexFront.Api/
│   │   ├── publish/                  ← ★ .NET 站点的「运行路径」指向这里
│   │   │   ├── HexFront.Api.dll
│   │   │   ├── appsettings.json      ← 端口、监听地址在这里
│   │   │   └── …
│   │   └── app.db                    ← 应用库（SQLite）
│   └── data/                         ← 地图/剧本数据（约 2.0G）
│       ├── gis/{dem,water,landcover,roads_pkg,airports}
│       ├── hexgrids/
│       ├── scenarios/
│       └── docs/
│
└── deploy/                           ← 部署物料（本文档 + 配置模板）
    ├── bt-site-static.conf           ← 静态站 nginx 配置模板
    ├── bt-site-dotnet.conf           ← .NET 站 nginx 配置模板
    ├── hexfront.env                  ← 后端环境变量模板
    ├── .env.production.example       ← 前端构建变量模板
    └── DEPLOY.md                     ← 本文件
```

**已剔除**：`tianditu.json`（天地图密钥，需自行放置）、`saves/`（运行时存档）、
`terrain_test/`、备份目录、`bin`/`obj`/`node_modules`、`.pdb` 中间产物。

---

## 1. 前置条件

| 项 | 要求 | 说明 |
|---|---|---|
| .NET 运行时 | **.NET 10**（ASP.NET Core Runtime） | 包是 `--no-self-contained` 发布，服务器必须自带运行时 |
| 磁盘 | ≥ 6G | zip 约 2.1G，解压后约 4.2G |
| 域名 | 2 个，各自已解析到服务器 | 前端 `game.*` / 后端 `api.*` |
| SSL | 两边都要 HTTPS | https 页面调 http 接口会被浏览器按混合内容拦截 |

装 .NET 10（宝塔软件商店若无 10 版）：

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

---

## 2. 上传与解压

```bash
# 本地
scp hexfront-release-<ts>.zip root@<服务器IP>:/tmp/

# 服务器
mkdir -p /www/wwwroot/wargame
cd /www/wwwroot/wargame
unzip -q /tmp/hexfront-release-<ts>.zip        # 解出 hexfront-release/…
mv hexfront-release/* .  && rmdir hexfront-release
chown -R www:www /www/wwwroot/wargame
```

解开后确认：

```bash
ls /www/wwwroot/wargame/server/HexFront.Api/publish/HexFront.Api.dll   # 后端 dll
ls /www/wwwroot/wargame/server/HexFront.Api/app.db                     # 应用库
ls /www/wwwroot/wargame/server/data/scenarios | head                   # 剧本
ls /www/wwwroot/wargame/web/index.html                                 # 前端
```

---

## 3. 后端环境变量

把 `deploy/hexfront.env` 的内容按实际域名改好，然后二选一：

**A. 宝塔面板** → 网站 → hexfront-api → 设置 → 环境变量，逐条添加：

| 变量 | 值 | 说明 |
|---|---|---|
| `HF_REPO_ROOT` | `/www/wwwroot/wargame` | 数据根（其下有 `server/`） |
| `ASPNETCORE_ENVIRONMENT` | `Production` | |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5018` | 只听回环，公网走 nginx |
| `HF_ALLOWED_ORIGINS` | `https://game.example.com` | **前端站 origin**，不带路径/斜杠 |

> `HF_REPO_ROOT` 写成 `/www/wwwroot/wargame` 或 `/www/wwwroot/wargame/server` 都可以，
> 新版代码两种写法都认，不会拼出 `server/server`（旧版有这个 bug，会导致剧本目录不可读）。

**B. systemd**（环境变量完全可控，推荐生产用）：

```ini
# /etc/systemd/system/hexfront.service
[Unit]
Description=HexFront API
After=network.target

[Service]
Type=simple
User=www
WorkingDirectory=/www/wwwroot/wargame/server/HexFront.Api/publish
EnvironmentFile=/www/wwwroot/wargame/hexfront.env
ExecStart=/usr/bin/dotnet /www/wwwroot/wargame/server/HexFront.Api/publish/HexFront.Api.dll
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

```bash
systemctl daemon-reload && systemctl enable --now hexfront
journalctl -u hexfront -f        # 看启动日志
```

---

## 4. 宝塔「.NET 网站」（后端）

**先在终端手工验一遍**，确认路径和端口都对，再交给宝塔托管：

```bash
cd /www/wwwroot/wargame/server/HexFront.Api/publish
dotnet HexFront.Api.dll --urls=http://127.0.0.1:5018
```

期望输出：

```
Now listening on: http://127.0.0.1:5018
```

且**不能**出现 `剧本目录不可读`。有的话说明 `HF_REPO_ROOT` 没生效。

确认无误后 Ctrl+C 停掉，建站：

| 宝塔字段 | 值 |
|---|---|
| 项目类型 | .NET 项目 |
| **运行路径** | `/www/wwwroot/wargame/server/HexFront.Api/publish` |
| **启动文件** | `HexFront.Api.dll`（只写文件名，不要带路径） |
| 项目端口 | `5018` |
| 启动用户 | `www` |

> ⚠ **运行路径必须直接指向 dll 所在目录**。宝塔按「运行路径 + 启动文件」拼命令，
> 少写一层 `publish` 会报 `dotnet-HexFront.Api.dll does not exist`。

然后：网站 → hexfront-api → 设置 → 配置文件，套用 `deploy/bt-site-dotnet.conf`。

**这套配置里最关键的两行**（漏了房间页会一直停在「连接中」）：

```nginx
proxy_set_header Upgrade    $http_upgrade;
proxy_set_header Connection $connection_upgrade;
proxy_read_timeout 3600s;      # 推演是长连接，默认 60s 会被掐断
```

> `$connection_upgrade` 由 nginx `http` 块里的 `map` 定义，宝塔默认已带。
> 若 nginx 报 `unknown variable "connection_upgrade"`，改用固定值
> `proxy_set_header Connection "upgrade";`。

---

## 5. 宝塔「静态网站」（前端）

| 宝塔字段 | 值 |
|---|---|
| 根目录 | `/www/wwwroot/wargame/web` |
| 默认文档 | `index.html` |
| PHP 版本 | **纯静态** |

网站 → hexfront-web → 设置 → 配置文件，套用 `deploy/bt-site-static.conf`。

**关键一行**（漏了房间页刷新就 404）：

```nginx
location / {
    try_files $uri $uri/ /index.html;
}
```

前端是 path 路由（`/room/ABC123/red`），直接访问或刷新不会命中任何静态文件，
必须回落到 `index.html`。

---

## 6. 端口配置来源与优先级

后端监听端口可以从三个地方配，**优先级从高到低**：

| 优先级 | 来源 | 说明 |
|---|---|---|
| 1 | `--urls` 命令行 | 最高。手工启动排查时用 |
| 2 | `ASPNETCORE_URLS` 环境变量 | **宝塔面板「端口」字段就是靠它传参的** |
| 3 | `publish/appsettings.json` 的 `Urls` 键 | 兜底默认值 `http://0.0.0.0:5018` |

> 代码里显式接管了这个优先级（`Program.cs` 读取 `ASPNETCORE_URLS` 后调
> `builder.WebHost.UseUrls`），否则框架默认是「配置文件 `Urls` 盖过环境变量」，
> 宝塔面板里填的端口会静默失效。

**改端口必须三处同步**：`ASPNETCORE_URLS` / 宝塔面板端口 / `bt-site-dotnet.conf` 的
`proxy_pass`。只改一处 = 连不上。

---

## 7. 天地图密钥（前后端两把，机制不同）

底图瓦片是**浏览器直接**向 `t0~t7.tianditu.gov.cn` 请求的，天地图按请求的
`Referer`（即页面所在域名）校验 token 白名单：域名不在白名单里就返回 401/403，
前端表现为「底图瓦片加载失败」——六角网格与地形（读本地 DEM）不受影响，只是没有底图。

密钥分两把，用途不同：

| | 配在哪 | 白名单按什么注册 | 换 key 需要重做什么 |
|---|---|---|---|
| **前端 key** | 构建时注入 `VITE_TIANDITU_TK` | **前端域名**（game.*） | 重新构建前端 + 重发 `web/` |
| **后端 key** | `server/data/tianditu.json`，随 `/api/scenarios` 下发 | 后端域名 / localhost | 改文件 + 重启后端 |

**优先级：前端 key > 后端 key**（见 `webfront/src/services/tianditu.ts`）。

### 方式 A：前端留空，用后端那把（推荐）

前端不配 key 时会自动回退用后端下发的。只要后端那把的白名单里**包含你的前端域名**，
底图就能正常显示。**换 key 只需改服务端文件并重启后端，不用重新构建前端。**

```bash
cat > /www/wwwroot/wargame/server/data/tianditu.json << 'EOF'
{ "tk": "你的天地图token" }
EOF
chown www:www /www/wwwroot/wargame/server/data/tianditu.json
systemctl restart hexfront
```

### 方式 B：前端独立 key

后端那把的白名单不含前端域名时用。在**开发机**构建时注入：

```bash
VITE_TIANDITU_TK="你的前端专属token" bash deploy/build-release-zip.sh
```

或写进 `webfront/.env.production` 再构建：

```
VITE_TIANDITU_TK=你的前端专属token
```

多把 key 用英文逗号分隔（按顺序取用，用于分摊天地图的日配额）：
`VITE_TIANDITU_TK=key1,key2,key3`

> 两把 key 都没有时应用照常启动，只是底图不可用（地图区域显示经纬网格线）。

---

## 8. 验证清单

```bash
# ① 后端活着
curl -s -o /dev/null -w "后端 HTTP %{http_code}\n" http://127.0.0.1:5018/api/instances
#    期望 200

# ② CORS 预检放行（跨域必需）
curl -i -X OPTIONS http://api.example.com/api/instances \
  -H 'Origin: https://game.example.com' \
  -H 'Access-Control-Request-Method: POST' | grep -i allow-origin
#    期望：Access-Control-Allow-Origin: https://game.example.com

# ③ 前端路由回落
curl -s -o /dev/null -w "前端路由 %{http_code}\n" https://game.example.com/room/ABC123/red
#    期望 200（内容是 index.html）

# ④ WebSocket 升级
curl -i -N -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  "http://api.example.com/ws?room=TEST&role=blue" | head -5
#    期望：HTTP/1.1 101 Switching Protocols
```

浏览器控制台不应有 CORS 报错；Network 里 WS 请求应指向**后端域名**（`wss://api.*`）。

---

## 9. 排错表

| 现象 | 根因 | 处理 |
|---|---|---|
| `dotnet-HexFront.Api.dll does not exist` | 宝塔「运行路径」少了 `publish` 层 | 运行路径改成 `.../HexFront.Api/publish`，启动文件只写 `HexFront.Api.dll` |
| `Failed to bind to address ...: address already in use` | 端口被占（常见：宝塔托管了一个 + 终端手工又跑了一个） | `ss -lntp \| grep 5018`；`ps aux \| grep HexFront`；杀掉多余进程 |
| `剧本目录不可读: .../server/server/data/scenarios` | `HF_REPO_ROOT` 多写了一层 `server` | 改成 `/www/wwwroot/wargame`（新版代码两种写法都认，但按文档写最稳） |
| 剧本列表为空 / 地图加载不出来 | `server/data` 没解压到位 | `ls /www/wwwroot/wargame/server/data/scenarios` 应有 json |
| 房间页一直「连接中」 | nginx `/ws` 缺 Upgrade 透传，或超时太短 | 见第 4 节的两行 + `proxy_read_timeout 3600s` |
| 玩一会儿掉线重连 | `proxy_read_timeout` 默认 60s 掐断长连接 | 放宽到 3600s |
| 浏览器报混合内容 / Mixed Content | 前端 https、后端 http | 两边都上 HTTPS；后端 `ASPNETCORE_URLS` 保持 http（走 nginx 加密）即可 |
| 副本编辑、删除报 403 | CORS 缺 `PUT`/`DELETE`（旧包才有此问题） | 用本发布包（已含修复），并确认 `HF_ALLOWED_ORIGINS` 与前端地址完全一致 |
| 前端请求打到了前端域名的 `/api` | 构建时没注入后端地址 | 用本发布包的 `web/`（已按目标后端域名构建）；换域名见第 11 节 |
| 底图瓦片加载失败 / 「未取得天地图密钥」 | 天地图按请求 Referer（页面域名）校验白名单，域名没注册 | 见第 7 节：前端 key 要按**前端域名**注册，或把前端域名加进后端那把的白名单 |
| 底图时有时无、缩放几次后空白 | 天地图日配额用尽 | 多配几把 key（`VITE_TIANDITU_TK` 逗号分隔），或换一把 |
| 后端起不来：`UnauthorizedAccessException` | 目录权限不足 | `chown -R www:www /www/wwwroot/wargame` |

---

## 10. 日常运维

```bash
systemctl restart hexfront          # 重启后端（systemd 方式）
journalctl -u hexfront -n 100       # 看日志
tail -f /www/wwwlogs/hexfront-api.error.log   # nginx 层错误
```

**升级新版本**：

```bash
cd /www/wwwroot/wargame
cp server/HexFront.Api/app.db /tmp/app.db.bak     # 先备份库
unzip -o /tmp/hexfront-release-<新时间戳>.zip      # 覆盖（app.db 会被覆盖，用备份还原）
# 若新包带了 app.db，先 rm 再解压，然后决定是否还原备份
```

**回滚**：保留上一版 zip，重新解压即可。

---

## 11. 换域名（后端域名变了）

前端产物里写死了后端地址，改域名需要重新构建前端：

```bash
# 开发机
cd webfront
cp .env.production.example .env.production
# 把 VITE_API_BASE_URL 改成新的后端地址（必须以 /api 结尾）
npm run build
```

然后把新的 `dist/` 替换服务器上的 `web/` 目录，并同步修改后端的
`HF_ALLOWED_ORIGINS`（前端域名若也变了，同样要改）。

> ⚠ **前端域名也变了的话，天地图 key 的白名单要跟着改**：
> 天地图按页面域名（Referer）校验 token，换了前端域名就必须重新注册白名单，
> 否则底图加载失败（见第 7 节）。后端那把 key 的白名单也要加上新域名。
