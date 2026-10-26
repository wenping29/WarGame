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
```
后端定位根目录用 `AppDbService.ServerRoot()`（认 `server` 目录），支持 `HF_REPO_ROOT`/`HF_DATA_DIR`/`HF_APP_DB`/`HF_SAVE_DIR`/`HF_SCENARIO_DIR` 环境变量覆盖。**不要再用"向上 N 层"的硬编码层级。**

## 部署架构：两种方案
- **方案 A（默认，单进程同源）**：后端进程同时托管 `wwwroot` 前端 + `/api` + `/ws`，只需一个站点，无需 nginx、无需 CORS。发布包已含前端。
- **方案 B（宝塔双站分离）**：前端在宝塔「静态网站」，后端在宝塔「.NET 网站」，**分属不同域名 → 跨域**。前提：
  1. 前端构建注入 `VITE_API_BASE_URL=https://api.<域名>/api`（**必须以 /api 结尾**；前端路径本身不带 /api，靠 baseURL 提供）
  2. 后端设 `HF_ALLOWED_ORIGINS=https://<前端站 origin>`（逗号分隔，末尾无斜杠）
  3. WebSocket 地址由 `VITE_API_BASE_URL` 的 origin 自动派生（https→wss），见 `RoomSocket.tsx`
  4. nginx 的 `/ws` 必须透传 `Upgrade`/`Connection`，`proxy_read_timeout` 放宽（长连接）
  5. **两边都必须 HTTPS**：https 页面调 http 接口会被浏览器按混合内容拦截
  6. 静态站必须 `try_files $uri $uri/ /index.html`（前端是 path 路由，如 `/room/XXX/red`）

## 已知坑
- **MSBuild Copy 任务在 Windows 上会栈溢出**（Stack overflow / MSB4166）：publish 前先 `dotnet clean`；不要用 `rm -rf` 清 bin/obj（会被安全策略拦截），用 `dotnet clean`。
- **CORS 方法表**：前端除 GET/POST 外还有 PUT（副本单位编辑）、DELETE，漏一个在跨源部署时预检就 403。
- **.ter 瓦片坐标换算是闭区间**：`col = clamp(floor((lon-west)/resLon), 0, cols-1)`，边界点归属最后一格。Python 生成端与 C# 读取端都要钳制，浮点在边界会算出越界值。
- 天地图真实 token 在 `server/data/tianditu.json`，**不进版本库、不进部署包**，部署时单独放。

## 测试
`dotnet test server/HexFront.Tests/HexFront.Tests.csproj --no-build`（Windows 上建议先 clean 再 build）。
已知既有失败（与结构/部署无关，需 GDAL 产物或引擎逻辑）：ScenarioBuildP7、DayNightCycle、AiOfficerP3、SaveLoadProtocol。
