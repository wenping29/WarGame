# HEXFRONT-REACT

兵棋对抗系统（HEXFRONT-WEB）的 React 前端，**唯一主前端**（原生 JS 版 `web/` 已不在当前工作副本，历史口径见设计文档）。已接真实 C# 后端并做过四终端浏览器端到端验证。

产品与协议口径都在 [../HEXFRONT-WEB产品设计文档.md](../HEXFRONT-WEB产品设计文档.md)（下称"设计文档"）：技术选型 §13.3、运行方式 §12、坐标基线 §6.5、验收基线与取证口径 §13.6。搬迁对照表与踩坑清单在同目录 [MIGRATION.md](MIGRATION.md)。

## 技术栈（实收）

- **React 19** + TypeScript ~6.0 + Vite 8
- **React Router 7**（`BrowserRouter`，路径路由）
- **axios**（REST）+ 原生 WebSocket `/ws`（**不是 SignalR**）
- **原生 canvas** 两套渲染内核：`src/map/MapView.ts`（战斗地图）+ `src/components/GeoMapView.tsx`（在线底图 + 六角格叠加）——没有 Konva、没有 Ant Design
- 状态由 `room/RoomSocket.tsx` 的 hook + 各页 `useState/useRef` 持有；**没有 Zustand store**（依赖里的 `zustand` 零引用）
- 图标 `lucide-react`，lint `oxlint`

## 页面与路由

| 路由 | 页面 | 职责 |
|---|---|---|
| `/` | Home | 首页三入口（零配置开局） |
| `/lobby` | Lobby | 找房间 / 加入 / 接管确认 |
| `/new` | Wizard | 5 步创建副本（含现场生成六角格） |
| `/saves` | SaveManager | 副本管理（存档、引擎口径格网预览） |
| `/grid` | GridManager | 六角格管理（服务器包下载/删除、本地涂刷挖格、导入导出） |
| `/scenarios` | ScenManager | 剧本管理（OOB 落位图、绑包、校验保存） |
| `/room/{码}/{red\|blue\|director\|spectator}` | RoomPage | 四终端对局（SideView / DirectorView / SpectatorView） |
| `/admin` | Admin | 系统管理（用户 · 角色 · 菜单 · 日志，仅超级管理员，路由级 + 后端双重校验） |
| `/guide` | Guide | 使用说明（各页操作手册与默认管理员改密提示） |
| `/login` | Login | 登录 |
| `*` | NotFound | 404 兜底，不再空白页 |

## 目录

```
src/
├── App.tsx / main.tsx        # 路由与 AuthProvider / Layout / Toast 外壳
├── pages/                    # 上表 11 个路由页 + SideView/DirectorView/SpectatorView
├── components/               # GeoMapView（在线地图引擎）· HexMap（战斗地图外壳）
│                             # · HexGenPanel（按范围现场生成，Wizard 与 ScenManager 共用）
│                             # · Layout · Toast · Auth
├── map/                      # MapView.ts · hex.ts（odd-r 几何与稳定序）· gisVector.ts
├── gis/hexgen.ts             # GIS 数据包 → 本地六角格映射（地形/道路/河流折线推导）
├── room/                     # RoomSocket.tsx（连接/重连/顶号）· protocol.ts · useUnitFull.ts
├── services/                 # api.ts（axios 封装全部 /api）· auth.ts · token.ts · tianditu.ts · sound.ts
└── App.css · index.css       # 深色终端风视觉 token
```

## 六角格与 GIS 数据链

数据根目录 = `../server/HexFront.Api/appsettings.json` 的 `DataPath.DataDir`（本机指向 `server/HexFront.Api/data`），可用环境变量 `HF_DATA_DIR` 覆盖；GIS 作业状态在 `data/gis-api/jobs/`。目录位置不合适就改配置，别搬数据。

生成：`POST /api/gis/jobs`（bbox / cell_km / seed / dem / landuse / roads / water / network / features）→ 自校验门（37 项）→ 两份产物：

- **源包** `data/gis/<名>.json`（格级地形与线要素，`meta.mercator_ox/oy` 等）
- **网络包** `data/hexgrids/<id>.zip`（`hg_xxxxxxxxxx`，启动时同步进 `app.db` 的 `map_packages` 表）

DEM 瓦片（terrarium）现存在 `data/gis/dem/tiles_z7/`（16384 张 / 696MB），与同目录的 `global_dem_z7_webmercator.tif`、`global_dem_30s.tif` 并存；由 `../server/tools/gis/fetch_global_dem.py` 或 `HexFront.Tools` 的 `fetch-dem` 生成。**注意默认目录不一致**：`fetch-dem` 缺省写到 `<DataDir>/dem/tiles_z{z}`，而消费方（`extract-terrain`、`rasters`）读的是 `<DataDir>/gis/dem/…tif` —— 跑下载要显式 `--root` 指到 `data/gis/dem`。剧本在顶层写 `"hexgrid": "hg_…"` 绑包；开局时 `HexGridPackageService.Build` 还原引擎棋盘，缺包则回退 `environment` 程序化生成。

**锚点口径（2026-10-27 定，改动前务必看清）**：

- 包与 `GET /api/hexgrids` 的 `west/south` = **棋盘外接矩形的西缘/底缘**，行号向北增大。
- 引擎侧 `mercator_ox/oy` = **hex(0,0) 中心、行向南**，只在 `Build()` 内部换算，不外泄。
- `GeoMapView` 的缺省覆盖层口径把 `west/south` 当**底行格心**，所以从 bbox 造 overlay 的入口要先加一个外接圆半径（`size = flatToFlat/√3`）；`southward: true` 走引擎口径则不用。
- 钉在 `../server/HexFront.Tests/src/Gis/HexGridPackageAnchorTests.cs`（3 项）。
- **绑大包不再挪部队到一角（2026-10-28）**：剧本写 `"oob_space": "environment"` 后，`sides.*.oob[].at`、简式 `units[]` 与 `victory[].target` 按剧本 `environment` 小棋盘排版，建局时中心对齐等比拉伸铺满实际棋盘（包是 58×60 / 64×71 / 84×104，剧本只有 22~26 见方）。缺省口径是 `board` = 绝对格号原样落位，向导提交的正是这个口径（经纬度已在浏览器换算过），不会被二次映射。南京/沈阳/长沙三个剧本已绑包 + 声明 `environment`，口径详见设计文档 §6.5 末与 `../server/HexFront.Tests/src/Engine/OobHexSpaceTests.cs`（10 项）。
- **未绑包的程序化棋盘按 `environment.center_lonlat` 定位**（2026-10-28）：`ScenarioService.BuildProceduralGrid` 把棋盘中心摆到剧本自己的经纬度上，之前恒等于硬编码点（40.42°E/27.61°N），24 个剧本共用同一块底图、`GET /api/gis/vector` 一律命中空框，看起来就是"所有地图都一样"。同一个字段还喂 `MercatorScaleFrom`，所以它一旦没绑上（`center_lonlat` 是一个词，`SnakeCaseLower` 会拆成 `center_lon_lat`，必须显式 `[JsonPropertyName]`），格宽会退化成 1/cos 不生效。钉在 `../server/HexFront.Tests/src/Engine/ProceduralAnchorTests.cs`（6 项）。

## 运行

后端先在 5018 上跑着（`../server`，见设计文档 §12）：

```bash
cp .env.example .env.development.local   # 填 VITE_API_BASE_URL 与 VITE_TIANDITU_TK
npm install
npm run dev        # http://localhost:5173（被占则顺延到 5174）
npm run build      # = tsc -b && vite build
npm run preview
npm run lint       # oxlint
```

- **端口不是随便选**：后端 `HF_ALLOWED_ORIGINS` 默认只放行 5173/5174，跑在别的端口页面能开但每个请求都吃 CORS。
- **本机用 `localhost` 而不是 `127.0.0.1`**：vite 在这台机器只监听 `[::1]:5173`。
- `.env.development.local` 里设了 `VITE_API_BASE_URL` 时，axios 是**跨源直连 5018**，`vite.config.ts` 的 `/api`、`/ws` 代理实际不走；留空才回退同源（单进程部署）。
- 底图瓦片由浏览器直接向 `t0~t7.tianditu.gov.cn` 请求，token 要按**前端域名**注册白名单，不能抄后端那把（详见设计文档 §12.2）。

## 验收门槛

```bash
npx tsc -b         # 类型门槛。--noEmit 不算：项目用 project references，只有 -b 会全量检查
npx oxlint src     # 基线 35 warnings / 0 errors，看新增行有没有被点名
```

后端侧 `dotnet test` 2026-10-28 实测 302 项（290 通过 / 12 失败，失败项是缺 GIS 数据包、非回归），WebSocket 冒烟在 `../server/scripts/ws-smoke.mjs`，数字含义见设计文档 §13.6 与 [../server/PARITY-CHECKS.md](../server/PARITY-CHECKS.md)。

UI 改动必须在真实浏览器里跑通并截图复核，不接受只看单测绿。地图/像素类断言用**无头 Chrome + CDP**（`--headless=new --remote-debugging-port`，`Page.navigate` 后等 `loadEventFired`；`/json/new?url=` 不会真的导航）。注意两件事：Qoder 内置浏览器面板 hidden 时 `requestAnimationFrame` 不触发，画布不会重绘、截图也不可用；`canvasOnly` 底图的经纬网线是 `rgba(255,255,255,.06)` 叠底色，像素分类必须只认**不透明涂装色**并用行列直方图取边界，否则整张画布会被误认成棋盘。

## 已知未测 / 残留登记

- 地图缩放/拖拽的 60fps 数字（归 #93 性能基线，跑 `../server/scripts/map-perf.mjs`）。移动端适配与对外部署未开工。
- 设计文档 §12 仍写着"前端代码一律走同源相对路径"，与 `.env.development.local` 的跨源直连不一致，待订正。
- 零引用/未接线（**只登记不删**）：依赖 `zustand`；空目录 `src/hooks/`、`src/store/`；`gis/hexgen.ts` 的 `gisFromPackage()` 与 `GridDoc.gis` 除统计区外没有入口调用（地图通道已支持 `gis` prop）。

## 设计原则

1. **零配置开局** —— 一键进入导演部体验完整功能，首页只留三个入口
2. **信息分级即玩法** —— E1 只给兵力档、E2 给兵种、E3 才给精确兵员；视图裁剪错了就是泄漏对手底牌
3. **类型与协议对齐后端** —— REST/WS 字段一律 snake_case，契约类型集中在 `room/protocol.ts`
4. **提示要有反馈** —— 指令受理、交战、顶号都有提示音与 Toast；异常态（房间不存在、角色位占用、密钥未配置）在界面上写明原因而不是空白
5. **几何口径只有一处真相** —— 格网锚点、odd-r 行列方向、`flat_to_flat` 与外接圆半径的换算集中在 `map/hex.ts` 与 `HexGridPackageService`，消费端不各自补偿
