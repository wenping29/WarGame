# HEXFRONT 项目长期约定

## 目录结构（2026-10 调整后）
```
server/HexFront.Api      后端 ASP.NET Core (.NET 10) + app.db（应用库，就在这里）
server/HexFront.Core     共享领域逻辑
server/HexFront.Tests    xUnit 测试
server/data              地图数据：dem/gis/hexgrids/scenarios/landcover/water/airports
server/tools/gis         Python 栅格工具（extract_terrain.py 纯 tifffile，不用 GDAL）
webfront                 React + Vite 前端
deploy                   部署物料与脚本（out/ 放产物，已 gitignore）
├── build-release-zip.sh 前后端分离发布包构建入口
├── release/              随包分发的部署物料（nginx 配置/env/手册/域名替换脚本）
└── tools/pack_release.py Python zipfile 打包器（ZIP64）
```
后端定位根目录用 `AppDbService.ServerRoot()`（认 `server` 目录），支持 `HF_REPO_ROOT`/`HF_DATA_DIR`/`HF_APP_DB`/`HF_SAVE_DIR`/`HF_SCENARIO_DIR` 环境变量覆盖。**不要再用"向上 N 层"的硬编码层级。**
`HF_REPO_ROOT` 两种写法都认：指向解压根（其下有 `server/`）或直接指向 `server` 目录本身（`ResolveServerRoot` 统一处理，不会拼出 `server/server`）。

## 部署架构：两种方案
- **方案 A（默认，单进程同源）**：后端进程同时托管 `wwwroot` 前端 + `/api` + `/ws`，只需一个站点，无需 nginx、无需 CORS。发布包已含前端。
- **方案 B（宝塔双站分离）**：前端在宝塔「静态网站」，后端在宝塔「.NET 网站」，**分属不同域名 → 跨域**。前提：
  1. 前端构建注入 `VITE_API_BASE_URL=https://api.<域名>/api`（**必须以 /api 结尾**；前端路径本身不带 /api，靠 baseURL 提供）
  2. 后端设 `HF_ALLOWED_ORIGINS=https://<前端站 origin>`（逗号分隔，末尾无斜杠）
  3. WebSocket 地址由 `VITE_API_BASE_URL` 的 origin 自动派生（https→wss），见 `RoomSocket.tsx`
  4. nginx 的 `/ws` 必须透传 `Upgrade`/`Connection`，`proxy_read_timeout` 放宽（长连接）
  5. **两边都必须 HTTPS**：https 页面调 http 接口会被浏览器按混合内容拦截
  6. 静态站必须 `try_files $uri $uri/ /index.html`（前端是 path 路由，如 `/room/XXX/red`）

## 发布包（当前主流程：前后端分离 + zip）
```bash
API_BASE_URL="https://api.<后端域名>/api" bash deploy/build-release-zip.sh
# -> deploy/out/hexfront-release-<ts>.zip  (2.0G, 148 文件, 约 60s)
```
包内结构 `hexfront-release/{web,server/{HexFront.Api/{publish,app.db},data},deploy}`：
- `web/` 前端静态产物（构建时注入 `VITE_API_BASE_URL`，**必须以 /api 结尾**）
- `server/HexFront.Api/publish/` 后端 dll（宝塔 .NET 站点「运行路径」指向这里，启动文件只写 `HexFront.Api.dll`）
- `server/data/` 地图剧本数据；`deploy/` nginx 配置 + env + DEPLOY.md + set-api-domain.sh
- 已剔除：`tianditu.json`（密钥）、`saves/`、`terrain_test/`、备份、`bin`/`obj`/`node_modules`、`.pdb`

**占位符换域名**：包里 `deploy/set-api-domain.sh` 直接改产物里的地址字符串，**不用重新构建前端**。
产物里 `https://api.example.com/api` 是完整字符串（出现 2 次），WS 地址是**运行时**从它派生
（`new URL(apiBase)` → https 转 wss），所以替换一处 REST 与 WS 同时生效。

**打包注意**：
- PowerShell 5.1 的 `Compress-Archive` 处理 2G 数据极慢且有单文件 2GB 上限 → 用 Python `zipfile`（开 ZIP64）
- `.tif`/`.ovr`/`.png` 等已压缩格式用 level 1，其余 level 6；zip 前后 2.2G→2.0G
- 打包前必须 `PRAGMA wal_checkpoint(TRUNCATE)` 合并 SQLite WAL，否则服务器首次启动看不到最近数据
- `dotnet publish` 必须带 `--artifacts-path <全新目录>`（见下坑）

## 已知坑
- **MSBuild Copy 任务在 Windows 上会栈溢出**（Stack overflow / MSB4166）：**正解是 `dotnet publish --artifacts-path <全新目录>`**（`dotnet clean` 清不掉 RID 子目录）；不要用 `rm -rf` 清 bin/obj（会被安全策略拦截），用 `dotnet clean`。
- **CORS 方法表**：前端除 GET/POST 外还有 PUT（副本单位编辑）、DELETE，漏一个在跨源部署时预检就 403。
- **.ter 瓦片坐标换算是闭区间**：`col = clamp(floor((lon-west)/resLon), 0, cols-1)`，边界点归属最后一格。Python 生成端与 C# 读取端都要钳制，浮点在边界会算出越界值。
- 天地图真实 token 在 `server/data/tianditu.json`，**不进版本库、不进部署包**，部署时单独放。

## 天地图密钥：前后端两把，前端优先
- 天地图按**请求 Referer（页面域名）**校验 token 白名单；底图瓦片是**浏览器直接**请求的，
  **后端自己不发天地图请求**（只在 `/api/scenarios` 的 `tiles.tk` 里下发 key）。
- ⇒ 后端那把 key 的白名单不含前端域名时，前端必须配**自己的** key（按前端域名注册）。
- 来源与优先级：`VITE_TIANDITU_TK`（前端 env，构建期注入）> 后端 `data/tianditu.json` 下发。
  解析集中在 `webfront/src/services/tianditu.ts` 的 `resolveTiandituTk()`。
- 留空 env → 回退后端下发（**运行时可改**，改服务端文件重启即可，不用重新构建前端）。
- 多把 key 用逗号分隔（分摊日配额）。

## 独立生成服务 HexFront.GisApi（2026-10-26 新增）
- `server/HexFront.GisApi`：**独立**的「GIS→六角格」生成 Web API（net10.0，引用 Core），与游戏后端解耦、独立存储 `server/data/gis-api/{jobs,grids}`，**不写** `data/hexgrids`。默认端口 5180（appsettings `Urls`，可被 ASPNETCORE_URLS 覆盖）。
- 端点：`/health`、`/api/capabilities`、`/api/plan`、`/api/generate`（异步作业）、`/api/jobs[/{id}][/{id}/download]`、`/api/grids[/{id}][/{id}/download]`。
- DEM 模式（`GisApi:DemMode`）：auto（有 .ter 瓦片即真实）/ real / synthetic。真实数据 = `TerrainSampler` 读 `data/terrain/*.ter`（含 ESA 土地覆盖），无瓦片自动降级合成。生成引擎是 Core 的 `HexGenService.Generate(spec, sampler)`（全 C# hexgen.py 移植）。
- 数据根解析 `RepoLocator`：HF_REPO_ROOT（兼容解压根/server 两种写法）→ 向上找 `<dir>/server/data` → 兜底 `<dir>/data`。**勿用"任意祖先含 data"作首选——用户机器 D:\data 是其个人 GIS 工作目录，曾误命中**。
- HexPackage 落盘契约：SnakeCaseLower + 缩进（同 hexgen.py / 主后端 data/gis/*.json）；作业记录跨请求返回需 `DeepClone()` JsonNode。
- 生成 `.ter`：用 Python `extract_terrain.py`（C# ExtractTerrainCommand 读 global_dem_30s.tif 有 ReadWindow 越界 bug 待修）。

## 测试
`dotnet test server/HexFront.Tests/HexFront.Tests.csproj --no-build`（Windows 上建议先 clean 再 build）。
已知既有失败（与结构/部署无关，需 GDAL 产物或引擎逻辑）：ScenarioBuildP7、DayNightCycle、AiOfficerP3、SaveLoadProtocol。
