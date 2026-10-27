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
  1. 前端构建注入 `VITE_API_BASE_URL=http://api.<域名>/api`（**必须以 /api 结尾**；前端路径本身不带 /api，靠 baseURL 提供）
  2. 后端设 `HF_ALLOWED_ORIGINS=http://<前端站 origin>`（逗号分隔，末尾无斜杠）
  3. WebSocket 地址由 `VITE_API_BASE_URL` 的 origin 自动派生（https→wss），见 `RoomSocket.tsx`
  4. nginx 的 `/ws` 必须透传 `Upgrade`/`Connection`，`proxy_read_timeout` 放宽（长连接）
  5. **两边都必须 HTTPS**：https 页面调 http 接口会被浏览器按混合内容拦截
  6. 静态站必须 `try_files $uri $uri/ /index.html`（前端是 path 路由，如 `/room/XXX/red`）

## 发布包（当前主流程：前后端分离 + zip，**含独立生成服务**）
```bash
# 默认占位符 http://43.142.147.244:7777/api（解压后跑 set-api-domain.sh 换域名）
bash deploy/build-release-zip.sh
# 或按真实后端域名直接注入（需以 /api 结尾）：
API_BASE_URL="http://api.<后端域名>/api" bash deploy/build-release-zip.sh
# -> deploy/out/hexfront-release-<ts>.zip  (2.0G, 195 文件)
```
包内结构 `hexfront-release/{web,server/{HexFront.Api/{publish,app.db},HexFront.GisApi/publish},data},deploy}`：
- `web/` 前端静态产物（构建时注入 `VITE_API_BASE_URL`，**必须以 /api 结尾**）
- `server/HexFront.Api/publish/` 后端 dll（宝塔 .NET 站点「运行路径」指向这里，启动文件只写 `HexFront.Api.dll`）
- `server/HexFront.GisApi/publish/` **六角格生成服务**（独立进程，默认 localhost:5180，只听回环，仅同机主后端调用）
- `server/data/` 地图剧本数据（后端与生成服务**共用**）；`deploy/` nginx 配置 + env + DEPLOY.md + set-api-domain.sh + **hexfront-gisapi.service**（生成服务 systemd 模板）
- 已剔除：`tianditu.json`（密钥）、`saves/`、`terrain_test/`、备份、`bin`/`obj`/`node_modules`、`.pdb`

**占位符换域名**：包里 `deploy/set-api-domain.sh` 直接改产物里的地址字符串，**不用重新构建前端**。
产物里 `http://43.142.147.244:7777/api` 是完整字符串（前端 js 中），WS 地址是**运行时**从它派生
（`new URL(apiBase)` → https 转 wss），所以替换一处 REST 与 WS 同时生效。
**生成服务地址无需换**：同机部署主后端 `GisApi:BaseUrl` 默认就是 `http://127.0.0.1:5180`，
跨机才用 `HF_GIS_API_URL` 配，并改 GisApi 的 `appsettings.json` 的 `Urls`。

**启动顺序**：先起 HexFront.GisApi（5180）→ 再起 HexFront.Api（5018）；主后端 `capabilities` 里
`engine=gisapi-remote` 即表示连上生成服务，否则回落本机合成引擎（地形是假的，业务不中断）。

**打包注意**：
- PowerShell 5.1 的 `Compress-Archive` 处理 2G 数据极慢且有单文件 2GB 上限 → 用 Python `zipfile`（开 ZIP64）
- `.tif`/`.ovr`/`.png` 等已压缩格式用 level 1，其余 level 6；zip 前后 2.2G→2.0G
- 打包前必须 `PRAGMA wal_checkpoint(TRUNCATE)` 合并 SQLite WAL，否则服务器首次启动看不到最近数据
- `dotnet publish` 必须带 `--artifacts-path <全新目录>`（见下坑）

**打包注意**：
- PowerShell 5.1 的 `Compress-Archive` 处理 2G 数据极慢且有单文件 2GB 上限 → 用 Python `zipfile`（开 ZIP64）
- `.tif`/`.ovr`/`.png` 等已压缩格式用 level 1，其余 level 6；zip 前后 2.2G→2.0G
- 打包前必须 `PRAGMA wal_checkpoint(TRUNCATE)` 合并 SQLite WAL，否则服务器首次启动看不到最近数据
- `dotnet publish` 必须带 `--artifacts-path <全新目录>`（见下坑）

## 已知坑
- **MSBuild Copy 任务在 Windows 上会栈溢出**（Stack overflow / MSB4166）：**正解是 `dotnet build/publish --artifacts-path <全新目录>`**（`dotnet clean` 清不掉 RID 子目录）；不要用 `rm -rf` 清 bin/obj（会被安全策略拦截），用 `dotnet clean`。artifacts 产物在 `<dir>/bin/<Proj>/debug/`，可直接跑 exe 或 `dotnet test <dll>`。
- **Api 进程在跑时**：`dotnet build` 会报 MSB3021/3027（HexFront.Core.dll 被锁），同样用 `--artifacts-path` 绕开，不要去杀用户的服务进程。
- **用户会并发改 HexFront.Api**（半重构状态，如 GisService.Plan→PlanAsync）：测试项目引用 Api，此时编不过属正常，不要去"修"用户的在改代码，等其改完或用 artifacts 单独验 Core/Tools。
- **CORS 方法表**：前端除 GET/POST 外还有 PUT（副本单位编辑）、DELETE，漏一个在跨源部署时预检就 403。
- **.ter 瓦片坐标换算是闭区间**：`col = clamp(floor((lon-west)/resLon), 0, cols-1)`，边界点归属最后一格。Python 生成端与 C# 读取端都要钳制，浮点在边界会算出越界值。
- 天地图真实 token 在 `server/data/tianditu.json`，**不进版本库、不进部署包**，部署时单独放。
- **appsettings.json 保持纯 JSON、不写 `//` 注释**（用户 2026-10-27 提交 `26021c7` 主动删掉了注释）。配置含义的说明写 C# 侧 XML 注释或 DEPLOY.md。
- **Windows 批处理三条硬约束**（实测矩阵 2 编码 × 2 换行 × chcp 有无 = 8 组，只有「纯 ASCII + CRLF + 无 chcp」干净）：
  ① **禁止在 .bat 里 `chcp 65001`**——改代码页后 cmd.exe 会在**错误字节偏移**上继续执行，文件头 `REM` 行被当命令执行；② **必须 CRLF**，LF-only 同样解析错位；③ 正文**纯 ASCII**（提示用英文），避开代码页依赖。

## GisApi 路径配置（2026-10-27）
- `GisApi` 段 6 个路径键：`TerrainDir`/`TerrainDirWindows`/`TerrainDirLinux` + `DataDir`/`DataDirWindows`/`DataDirLinux`。
  运行时**系统特化键优先 → 通用键 → 留空按 `HF_REPO_ROOT` 推导**（`<repoRoot>/data/terrain`、`<repoRoot>/data/gis-api`）。
  同一份 appsettings.json 跨 Windows/Linux 通用。
- 归一在 `GisApiService.NormalizePath`：展开 `$ENV`/`%ENV%`、展开 `~`、统一 `/`；`ResolveDir` 支持相对路径**按仓库根**解析（非 CWD、非 appsettings 所在目录）。
- `TerrainDir` = **只读输入**（`terrain_manifest.json` + `*.ter`）；`DataDir` = **生成输出**（其下 `jobs/` 作业记录 + `grids/` 六角格包）。
- ⚠️ `data/gis/` 是**原始 GIS 素材**（dem/landcover/water/roads_pkg/airports），不是 TerrainDir；`.ter` 是从它加工出来的产物。

## 天地图密钥：前后端两把，前端优先
- 天地图按**请求 Referer（页面域名）**校验 token 白名单；底图瓦片是**浏览器直接**请求的，
  **后端自己不发天地图请求**（只在 `/api/scenarios` 的 `tiles.tk` 里下发 key）。
- ⇒ 后端那把 key 的白名单不含前端域名时，前端必须配**自己的** key（按前端域名注册）。
- 来源与优先级：`VITE_TIANDITU_TK`（前端 env，构建期注入）> 后端 `data/tianditu.json` 下发。
  解析集中在 `webfront/src/services/tianditu.ts` 的 `resolveTiandituTk()`。
- 留空 env → 回退后端下发（**运行时可改**，改服务端文件重启即可，不用重新构建前端）。
- 多把 key 用逗号分隔（分摊日配额）。

## 生成链路：主后端 → 独立生成服务（2026-10-26 起）
**六角格生成走 `HexFront.GisApi`，主后端不再本机跑 hexgen.py**（远程优先、本机兜底）：
- `HexFront.Api/src/Services/GisApiClient.cs` 封装 health/capabilities/plan/generate/jobs/{id}/download；
  基址 `GisApi:BaseUrl`（默认 http://127.0.0.1:5180），`HF_GIS_API_URL` / `HF_GIS_API_ENABLED=0` 可覆盖。
- `GisService.StartJobAsync` 提交后由 `TrackRemoteAsync` 1s 轮询同步 progress/stage/log，完成后下载包体
  写 `data/gis/<包名>.json` 并 `PublishToHexGrid` 存成 hexgrid；远程不可达回落 python hexgen.py → 原生 C# 合成。
- `GET /api/gis/jobs/{id}/download` 下载数据包；作业 JSON 含 progress / stage / remote_id。
- 前端 `HexGenPanel` 显示进度条 + 阶段 + 完成后「下载数据包」。

## 独立生成服务 HexFront.GisApi（2026-10-26 新增）
- `server/HexFront.GisApi`：**独立**的「GIS→六角格」生成 Web API（net10.0，引用 Core），与游戏后端解耦、独立存储 `server/data/gis-api/{jobs,grids}`，**不写** `data/hexgrids`。默认端口 5180（appsettings `Urls`，可被 ASPNETCORE_URLS 覆盖）。
- 端点：`/health`、`/api/capabilities`、`/api/plan`、`/api/generate`（异步作业）、`/api/jobs[/{id}][/{id}/download]`、`/api/grids[/{id}][/{id}/download]`。
- DEM 模式（`GisApi:DemMode`）：auto（有 .ter 瓦片即真实）/ real / synthetic。真实数据 = `TerrainSampler` 读 `data/terrain/*.ter`（含 ESA 土地覆盖），无瓦片自动降级合成。生成引擎是 Core 的 `HexGenService.Generate(spec, sampler)`（全 C# hexgen.py 移植）。
- 数据根解析 `RepoLocator`：HF_REPO_ROOT（兼容解压根/server 两种写法）→ 向上找 `<dir>/server/data` → 兜底 `<dir>/data`。**勿用"任意祖先含 data"作首选——用户机器 D:\data 是其个人 GIS 工作目录，曾误命中**。
- HexPackage 落盘契约：SnakeCaseLower + 缩进（同 hexgen.py / 主后端 data/gis/*.json）；作业记录跨请求返回需 `DeepClone()` JsonNode。
- 生成 `.ter`：Python `extract_terrain.py` 可用；C# `ExtractTerrainCommand` 的 GeoBand.ReadWindow 越界 bug **已修**（2026-10-26）。

## C# 工具链 server/HexFront.Tools（8 个 Python 工具已移植，2026-10-26）
控制台 EXE `hexfront-tools <command>`：hexgen / extract-terrain / rasters / fetch-dem / fetch-landcover /
fetch-water / update-scenarios / pack-release / **tiff2json**。复用库在 `HexFront.Core/src/GisTools/`
（GeoBand、GeoTiffWriter、TerrainTileWriter、CogReader、PngDecoder、ShapefileReader、GeoPackageReader/Writer、GridCoverage），**全程零 GDAL**。
- `tiff2json`：GeoTIFF → 带地理参考 JSON（`bbox/pixelSize/stats/values[/normalized]`），供流量/通行能力网格用；
  `--scale N` 做 N×N 分块均值下采样，`--null-nodata` 出 null，`--normalize` 出 0..1。

### GeoTIFF 读写约定（踩过坑，务必遵守）
- **标签内联规则**：count × 类型宽度 ≤ 4 时值内联在 4 字节字段，不得另存数组；反之 4 字节存文件偏移。
  读写两侧都要判别（曾因读侧一律 Seek、写侧单瓦片数组不内联而双双出错）。
- **ModelTransformationTag(34264)** 4×4 行主序，平移量在索引 **3(X) / 7(Y)**（规范原文 "d 与 h 表示 X/Y 平移"，GDAL 同）。
  本仓库三份真实 GDAL 产物都用 **ModelTiepoint(33922) + ModelPixelScale(33550)**，不用 34264。
- **PixelToGeo 是「像素左上角」约定**（同 GDAL 仿射）：栅格外框 = `PixelToGeo(0,0)` .. `PixelToGeo(W,H)`，不要再偏移半像素。
- 边缘瓦片按 `min(TileSize, 剩余)` 解码；它同时是 predictor=2 水平差分的**行步长**。
  块内源坐标 = `row0 + dr - sr0`（不是 `max(sr0,row0)+dr-sr0`）。
- predictor 做差分时必须保存**原始**前值，不能就地读已改写成的差分值。

## 测试
`dotnet test server/HexFront.Tests/HexFront.Tests.csproj --no-build`（Windows 上建议先 clean 再 build）。
已知既有失败（与结构/部署无关，需 GDAL 产物或引擎逻辑）：ScenarioBuildP7、DayNightCycle、AiOfficerP3、SaveLoadProtocol。
