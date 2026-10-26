# HexFront 部署手册

面向两种部署形态：

- **方案 A（单进程）**：后端同时托管前端静态文件，一个 .NET 站就够。
- **方案 B（双站分离）**：宝塔静态站跑前端 + 宝塔 .NET 站跑后端，前后端不同域名。

---

## 0. 先看这三条（宝塔上最常踩的坑）

### 0.1 `dotnet-HexFront.Api.dll does not exist`

宝塔的 .NET 项目管理器按「**运行路径** + **启动文件**」拼启动命令。两处必须对齐：

| 面板字段 | 填什么 | 说明 |
|---|---|---|
| 运行路径 | `/www/wwwroot/wargame/server/HexFront.Api/publish` | **必须指向 dll 所在的那个目录** |
| 启动文件 | `HexFront.Api.dll` | 只写文件名，不要带路径、不要带 `dotnet` 前缀 |
| 项目端口 | `5018` | 与 nginx 反代目标端口一致 |

> **最常见的错法**：把运行路径填成 `/www/wwwroot/wargame/server/HexFront.Api`（少一层 `publish`），
> 宝塔就会去那儿找 dll → 报 `does not exist`。
>
> 面板上「运行路径」和「启动文件」是两个字段，不要在启动文件里写全路径。

若用宝塔的「启动命令」自定义方式，写法是：

```
dotnet /www/wwwroot/wargame/server/HexFront.Api/publish/HexFront.Api.dll --urls=http://127.0.0.1:5018
```

**先在终端手工验证一遍**，能起来说明路径没问题：

```bash
cd /www/wwwroot/wargame/server/HexFront.Api/publish
dotnet HexFront.Api.dll --urls=http://127.0.0.1:5018
# 看到 "Now listening on: http://127.0.0.1:5018" 就是好的
```

### 0.2 `Failed to bind to address http://127.0.0.1:5000: address already in use`

端口被占了。三种原因，按顺序排查：

```bash
# 1) 谁占了 5000
ss -lntp | grep 5000
# 2) 是不是自己起了两个（宝塔托管了一个，你又在终端手跑了一个 —— 最常见）
ps aux | grep -i hexfront | grep -v grep
# 3) 是不是别的服务（如另一个 .NET 站、宝塔的默认站点）
```

**修法**：

- 手跑测试后没关掉 → `kill <pid>`，只留宝塔托管的那一个；
- 5000 被别的服务占 → 把本项目改用 5018（宝塔「项目端口」字段 + nginx `proxy_pass` + `ASPNETCORE_URLS` 三处一起改）；
- **注意**：报错里的 5000 说明 `ASPNETCORE_URLS` 或 `--urls` 被设成了 5000。宝塔默认端口就是 5000，
  建议统一改成 5018，避免和面板默认值混淆。

### 0.3 `剧本目录不可读: .../server/server/data/scenarios`（路径多了一层 server）

后端用 `HF_REPO_ROOT` 定位 `server/` 目录。**两种写法都合法**，但早期版本只认一种，写错就会整体偏移一层：

| HF_REPO_ROOT 指向 | 后端解析出 | 是否正确 |
|---|---|---|
| `/www/wwwroot/wargame`（解压根，其下有 `server/`） | `/www/wwwroot/wargame/server` | ✅ |
| `/www/wwwroot/wargame/server`（server 目录本身） | `/www/wwwroot/wargame/server` | ✅ |

现在后端会**自动识别**这两者（见 `AppDbService.ResolveServerRoot`），不会拼出 `server/server`。
但如果你看到 `server/server`，说明跑的是**旧版本的后端**——需要重新发布后端。

**验证**：启动日志里应该有这样一行（没有就说明路径不对）：

```
统一应用库就绪: /www/wwwroot/wargame/server/HexFront.Api/app.db
```

以及剧本目录不报「不可读」。

---

## 1. 前置依赖

| 组件 | 版本 | 备注 |
|---|---|---|
| .NET 运行时 | 10.0 | 宝塔 → 网站 → .NET 项目 → .NET 环境管理 |
| Nginx | 宝塔自带 | |
| Python | 3.9+ | 仅生成真实地形数据时需要（开发机侧） |

宝塔装 .NET 10（面板没有 10 版时）：

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

---

## 2. 构建物料（开发机）

### 方案 A：单包

```bash
bash deploy/build-package.sh
```

产物：`deploy/out/hexfront-deploy-<时间戳>.tar.gz`（约 2.0G，内含前端 + 后端 + 数据）。

### 方案 B：三包（双站分离）

```bash
API_BASE_URL=https://api.example.com/api bash deploy/build-split.sh
```

产物：

| 包 | 大小 | 解到哪 |
|---|---|---|
| `hexfront-web-<ts>.tar.gz` | ~170K | 静态站根目录 `/www/wwwroot/hexfront-web` |
| `hexfront-api-<ts>.tar.gz` | ~9M | `/www/wwwroot/wargame/server/HexFront.Api/` |
| `hexfront-data-<ts>.tar.gz` | ~2.0G | `/www/wwwroot/wargame/server/` |

`API_BASE_URL` **必须以 `/api` 结尾**（后端控制器前缀是 `[Route("api")]`）。
它同时决定 WebSocket 的 origin（https → wss），编译进前端产物。

> Windows 上 `dotnet publish` 会因 MSBuild `Copy` 任务覆盖已存在文件而栈溢出
> （Stack overflow / MSB4166）。脚本已用 `--artifacts-path <全新目录>` 规避，
> **不要去掉这个参数**。

---

## 3. 上传与放置

```bash
scp deploy/out/hexfront-*-<ts>.tar.gz user@server:/tmp/
```

服务器上（方案 B）：

```bash
sudo mkdir -p /www/wwwroot/wargame/server/HexFront.Api
sudo tar -xzf /tmp/hexfront-api-<ts>.tar.gz   -C /www/wwwroot/wargame/server/HexFront.Api
sudo tar -xzf /tmp/hexfront-data-<ts>.tar.gz  -C /www/wwwroot/wargame/server
sudo mkdir -p /www/wwwroot/hexfront-web
sudo tar -xzf /tmp/hexfront-web-<ts>.tar.gz   -C /www/wwwroot/hexfront-web
sudo chown -R www:www /www/wwwroot/wargame /www/wwwroot/hexfront-web
```

放置后的目录结构（**务必核对**）：

```
/www/wwwroot/wargame/
├── server/
│   ├── data/                          ← 数据包解出来的（scenarios/ gis/ hexgrids/）
│   └── HexFront.Api/
│       ├── app.db
│       └── publish/
│           └── HexFront.Api.dll       ← 宝塔 .NET 站点指向这个
```

**常见放错**：data 包没解到 `server/` 下，而是解到了 `server/server/` 或仓库根下。
后端只认 `<HF_REPO_ROOT>/server/data`，放错了剧本列表就是空的。

---

## 4. 宝塔：静态站（前端）

- 域名：`game.example.com`
- 根目录：`/www/wwwroot/hexfront-web`
- 配置里套用 `deploy/bt-site-static.conf`

关键那条：

```nginx
try_files $uri $uri/ /index.html;
```

房间页走 path 路由（`/room/XXXX/red`），不回落到 index.html 刷新就 404。

---

## 5. 宝塔：.NET 站（后端）

- 域名：`api.example.com`
- 运行路径：`/www/wwwroot/wargame/server/HexFront.Api/publish`
- 启动文件：`HexFront.Api.dll`
- 项目端口：`5018`
- 启动用户：`www`

配置里套用 `deploy/bt-site-dotnet.conf`。**重点核对两件事**：

1. `/ws` 有没有透传 `Upgrade` / `Connection` 头 —— 漏了房间永远「连接中」；
2. `proxy_read_timeout` 要放宽（推演是长连接，默认 60s 会被 nginx 掐断）。

---

## 6. 后端环境变量

### 6.1 端口配置来源与优先级

| 层级 | 来源 | 生效 |
|---|---|---|
| 1 | `--urls` 命令行参数 | 最高 |
| 2 | `ASPNETCORE_URLS` 环境变量 | ✅ |
| 3 | `appsettings.json` 的 `"Urls"` 键 | ✅ |
| 4 | Kestrel 内置默认 5000 | 兜底 |

后端在 `Program.cs` 里显式接管了这个优先级（`builder.WebHost.UseUrls(...)`）。
原因是框架自带的顺序是**反的**——配置文件的 `Urls` 会盖过 `ASPNETCORE_URLS`，
那样宝塔面板的端口字段会静默失效，后端还可能退回监听 `0.0.0.0` 暴露公网。

> ⚠️ 不要用 `Kestrel:Endpoints`。官方文档明确它的优先级**高于** `--urls` 和
> `ASPNETCORE_URLS`，会把两者一并盖掉。

### 6.2 环境变量清单

照 `deploy/bt-dotnet.env.example` 配置：

```ini
HF_REPO_ROOT=/www/wwwroot/wargame
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5018
HF_ALLOWED_ORIGINS=https://game.example.com
```

| 变量 | 作用 |
|---|---|
| `HF_REPO_ROOT` | 解压根，后端据此定位 `server/`（含 `data/` 与 `HexFront.Api/app.db`） |
| `ASPNETCORE_URLS` | 监听地址，覆盖 `appsettings.json` 的 `Urls` |
| `HF_ALLOWED_ORIGINS` | CORS 白名单，填**前端站点**的 origin（结尾不带 `/`） |
| `HF_APP_DB` | 覆盖应用库路径 |
| `HF_DATA_DIR` | 覆盖 data 目录（地图数据单独挂盘时用） |
| `HF_SAVE_DIR` | 覆盖存档目录 |
| `HF_SCENARIO_DIR` | 覆盖剧本目录 |

**托管方式二选一**：

- **systemd（推荐，环境变量完全可控）**：见 `deploy/server-setup.sh` 生成的 `hexfront.service`，
  宝塔建一个反向代理站点指 `127.0.0.1:5018`；
- **宝塔 .NET 站点直接托管**：在站点设置的「环境变量」里填同名变量。

### 6.3 密钥

`/www/wwwroot/wargame/server/data/tianditu.json`：

```json
{ "tk": "你的天地图token" }
```

不放也能启动，只是地形降级为合成数据。

---

## 7. 验证清单

```bash
# 1) 后端起来了
curl -i http://127.0.0.1:5018/api/instances
#    期望 200

# 2) CORS 预检放行
curl -i -X OPTIONS http://api.example.com/api/instances \
  -H 'Origin: https://game.example.com' -H 'Access-Control-Request-Method: POST'
#    期望：Access-Control-Allow-Origin: https://game.example.com

# 3) PUT/DELETE 也要放行（副本单位编辑、删除会用到）
curl -i -X OPTIONS http://api.example.com/api/admin/save_units/x \
  -H 'Origin: https://game.example.com' -H 'Access-Control-Request-Method: PUT'

# 4) 前端路由回落
curl -i https://game.example.com/room/XXXX/red
#    期望 200 + index.html

# 5) 剧本目录读到了
grep "剧本目录不可读" /www/wwwlogs/*.log   # 应该有这一条才对
```

浏览器控制台不应有 CORS 报错；WebSocket 应指向**后端**域名。

---

## 8. 排错

| 现象 | 原因 | 处理 |
|---|---|---|
| `dotnet-HexFront.Api.dll does not exist` | 宝塔运行路径少一层 `publish` | 见 §0.1 |
| `address already in use` | 重复启动 / 端口被占 | 见 §0.2 |
| `剧本目录不可读: .../server/server/...` | 后端是旧版本，或 `HF_REPO_ROOT` 指错 | 见 §0.3，重新发布后端 |
| 剧本列表为空 | `server/data` 没放在 `server/` 下 | 核对目录结构，见 §3 |
| 登录/编辑副本 403 | CORS 缺 `PUT`/`DELETE` | 重新发布后端（已修） |
| 房间一直「连接中」 | nginx `/ws` 缺 Upgrade 透传，或超时太短 | 核对 `bt-site-dotnet.conf` |
| 混合内容拦截 | 前端 https、后端 http | 两边都上 SSL |
| 请求打到了前端域名的 `/api` | 构建时没注入 `VITE_API_BASE_URL` | 改 `.env.production` 后重新构建 |
| 端口 5018 被占用 | `lsof -i:5018` | 换端口，三处一起改（宝塔端口 / `ASPNETCORE_URLS` / nginx `proxy_pass`） |

---

## 9. 附录

### 9.1 文件清单

| 文件 | 用途 |
|---|---|
| `deploy/build-package.sh` | 方案 A 单包构建 |
| `deploy/build-split.sh` | 方案 B 三包构建 |
| `deploy/server-setup.sh` | 服务器端解包 + 环境变量 + systemd |
| `deploy/hexfront.env.example` | 方案 A 环境变量模板 |
| `deploy/bt-dotnet.env.example` | 方案 B 环境变量模板 |
| `deploy/bt-site-static.conf` | 静态站 nginx 片段 |
| `deploy/bt-site-dotnet.conf` | .NET 站 nginx 片段 |
| `webfront/.env.production.example` | 前端构建期变量模板 |

### 9.2 跨域前提（代码里已改好，升级后端才会生效）

| 文件 | 改动 | 为什么必须 |
|---|---|---|
| `webfront/src/room/RoomSocket.tsx` | WS 地址从 `VITE_API_BASE_URL` 的 origin 派生（https→wss） | 原本写死 `location.host/ws`，跨域会连错域名 |
| `server/HexFront.Api/Program.cs` | CORS 方法表补 `PUT`/`DELETE`/`PATCH` | 前端 `api.ts` 有 PUT/DELETE，漏了预检就 403 |
| `server/HexFront.Api/src/Services/AppDbService.cs` | `HF_REPO_ROOT` 两种写法都认 | 避免 `server/server` 路径错位 |
