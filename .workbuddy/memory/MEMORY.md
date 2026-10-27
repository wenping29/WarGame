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

## 发布包（当前主流程：前后端分离 + zip，单进程）
```bash
# 默认占位符 http://43.142.147.244:7777/api（解压后跑 set-api-domain.sh 换域名）
bash deploy/build-release-zip.sh
# 或按真实后端域名直接注入（需以 /api 结尾）：
API_BASE_URL="http://api.<后端域名>/api" bash deploy/build-release-zip.sh
# -> deploy/out/hexfront-release-<ts>.zip
```
包内结构 `hexfront-release/{web,server/HexFront.Api/{publish,app.db},data},deploy}`：
- `web/` 前端静态产物（构建时注入 `VITE_API_BASE_URL`，**必须以 /api 结尾**）
- `server/HexFront.Api/publish/` 后端 dll（**含进程内六角格生成**，宝塔 .NET 站点「运行路径」指向这里，启动文件只写 `HexFront.Api.dll`）
- `server/data/` 地图剧本数据；`deploy/` nginx 配置 + env + DEPLOY.md + set-api-domain.sh
- 已剔除：`tianditu.json`（密钥）、`saves/`、`terrain_test/`、备份、`bin`/`obj`/`node_modules`、`.pdb`

**占位符换域名**：包里 `deploy/set-api-domain.sh` 直接改产物里的地址字符串，**不用重新构建前端**。
产物里 `http://43.142.147.244:7777/api` 是完整字符串（前端 js 中），WS 地址是**运行时**从它派生
（`new URL(apiBase)` → https 转 wss），所以替换一处 REST 与 WS 同时生效。

**单进程**：只有 HexFront.Api 一个后端（端口 5018）。六角格生成在进程内完成，没有独立生成服务、没有 5180 端口；
`capabilities` 的 `engine=local-terrain`（真实瓦片就绪）或 `synthetic`（瓦片缺失回落合成）。部署只需起这一个后端。

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
- **用户会并发改 HexFront.Api**（GisService 已合并进程内生成、结构仍在演进）：测试项目引用 Api，用户改 Api 导致编不过时属正常，不要去"修"用户的在改代码，等其改完或用 artifacts 单独验 Core/Tools。
- **CORS 方法表**：前端除 GET/POST 外还有 PUT（副本单位编辑）、DELETE，漏一个在跨源部署时预检就 403。
- **.ter 瓦片坐标换算是闭区间**：`col = clamp(floor((lon-west)/resLon), 0, cols-1)`，边界点归属最后一格。Python 生成端与 C# 读取端都要钳制，浮点在边界会算出越界值。
- 天地图真实 token 在 `server/data/tianditu.json`，**不进版本库、不进部署包**，部署时单独放。
- **appsettings.json 保持纯 JSON、不写 `//` 注释**（用户 2026-10-27 提交 `26021c7` 主动删掉了注释）。配置含义的说明写 C# 侧 XML 注释或 DEPLOY.md。
- **Windows 批处理三条硬约束**（实测矩阵 2 编码 × 2 换行 × chcp 有无 = 8 组，只有「纯 ASCII + CRLF + 无 chcp」干净）：
  ① **禁止在 .bat 里 `chcp 65001`**——改代码页后 cmd.exe 会在**错误字节偏移**上继续执行，文件头 `REM` 行被当命令执行；② **必须 CRLF**，LF-only 同样解析错位；③ 正文**纯 ASCII**（提示用英文），避开代码页依赖。

## GisApi 配置段（2026-10-27 合并后，位于 HexFront.Api 的 appsettings.json）
- 段名仍为 `GisApi`，但键已变：**去掉 `BaseUrl`/`Enabled`**（不再远程调用），只剩地形相关：
  `TerrainDir`/`TerrainDirWindows`/`TerrainDirLinux`（系统特化键优先）+ `DemMode`（auto/real/synthetic）。
- `TerrainDir` 解析：特化键 → 通用键 → 留空按仓库根（server 目录）推导默认 `data/terrain`；
  归一逻辑复用 Api 的 `PathResolver`（展开 `$ENV`/`%ENV%`、`~`、统一 `/`，相对路径按仓库根解析）。
- `TerrainDir` = **只读输入**（`terrain_manifest.json` + `*.ter`）；真实高程由 `TerrainSampler` 读取。
- 作业记录落盘在 `data/gis-api/jobs/<id>.json`（由 `rooms.DataDirectory` 派生），后端重启会重跑未完成的作业；
  数据包仍写 `data/gis/<包名>.json`，并发布为 `data/hexgrids/<id>.zip`。
- ⚠️ `data/gis/` 是**原始 GIS 素材**（dem/landcover/water/roads_pkg/airports），不是 TerrainDir；`.ter` 是从它加工出来的产物。

## 天地图密钥：前后端两把，前端优先
- 天地图按**请求 Referer（页面域名）**校验 token 白名单；底图瓦片是**浏览器直接**请求的，
  **后端自己不发天地图请求**（只在 `/api/scenarios` 的 `tiles.tk` 里下发 key）。
- ⇒ 后端那把 key 的白名单不含前端域名时，前端必须配**自己的** key（按前端域名注册）。
- 来源与优先级：`VITE_TIANDITU_TK`（前端 env，构建期注入）> 后端 `data/tianditu.json` 下发。
  解析集中在 `webfront/src/services/tianditu.ts` 的 `resolveTiandituTk()`。
- 留空 env → 回退后端下发（**运行时可改**，改服务端文件重启即可，不用重新构建前端）。
- 多把 key 用逗号分隔（分摊日配额）。

## 生成链路：进程内生成（2026-10-27 合并进 HexFront.Api）
**六角格生成在 HexFront.Api 进程内直接调用 Core 的 `HexGenService.Generate`**（`HexFront.GisApi` 项目已删除，不再有 5180 独立进程、不再本机跑 hexgen.py）：
- `GisService`（HexFront.Api）串行执行作业：`StartJobAsync` 入队 → `RunInProcess` 调 `HexGenService.Generate(spec, sampler)`；
  真实高程来自 `TerrainSampler` 读 `data/terrain/*.ter`（含 ESA 土地覆盖），瓦片缺失回落合成。
- 进度经 `OnProgress` 回报（stage + 0..100）；完成后自校验、`WriteAllText` 到 `data/gis/<包名>.json`、再 `PublishToHexGrid` 存成 hexgrid；
  `capabilities` 的 `engine=local-terrain`/`synthetic`，并把地形实况挂在 `remote.dem`（兼容前端 HexGenPanel 的 capsNotes）。
- 作业记录落盘 `data/gis-api/jobs/<id>.json`（重启后未完成的作业自动重跑）；`GET /api/gis/jobs/{id}/download` 下载数据包。
- 前端 `HexGenPanel` 显示进度条 + 阶段 + 完成后「下载数据包」。

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
