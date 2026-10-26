# 前端迁移说明（`web/` → `webfront/`）

状态：**已完工**。本文件记的是"原生 JS 客户端如何搬到 React"的对照关系与踩坑清单，不是待办计划。技术选型实收见 [../HEXFRONT-WEB产品设计文档.md](../HEXFRONT-WEB产品设计文档.md) §13.3，验收数字见同文 §13.6。

> 2026-10-17 初版把这里写成"纯本地 mock 模式，待接 API / SignalR / Zustand store"，那份已整体过期。会话传输最终是**原生 WebSocket `/ws`**（不是 SignalR），状态由 `room/RoomSocket.tsx` 的 hook 持有（**没有 Zustand store**）。

## 为什么搬

`web/` 是无构建的原生 ESM 客户端。C# 重构后 Dart 侧没了，`web/js/*` 里的逻辑要继续演进就得有个能类型检查、能拆组件的载体；同时拍板 **React 为唯一主前端**，`web/` 冻结退役（排期 #92）。

## 技术栈对比（实收）

| 特性 | 旧版 `web/` | 新版 `webfront/` |
|---|---|---|
| 框架 | 原生 JavaScript ES6（无构建） | React 19.2.8 + TypeScript ~6.0.2 + Vite 8.3.0 |
| 路由 | hash 路由（手写） | React Router 7.18.4 `BrowserRouter` 路径路由 |
| 状态管理 | 全局变量 | hook + `useState/useRef`（**没有 Zustand store**，依赖里的 zustand 零引用） |
| HTTP | `fetch` | `axios` 1.20.0，`services/api.ts` 统一 `/api` 前缀 |
| 实时通信 | 原生 WebSocket `/ws` | **同一个原生 `/ws`**，线上格式逐字一致；不引入 SignalR 客户端 |
| 地图 | 手写 canvas（`js/map.js`） | 手写 canvas（`map/MapView.ts`）+ `components/HexMap.tsx` 外壳；**没有 Konva** |
| UI 库 | 手写 DOM + CSS | 手写 JSX + CSS（沿用原深色终端风 token）；图标 `lucide-react`；**没有 Ant Design** |
| 类型检查 | 无 | `npx tsc -b`（门槛）；lint 用 `oxlint` |

## 文件映射（实收）

```
web/                          →  src/
──────────────────────────────────────────────
index.html                    →  index.html（Vite 模板）
js/main.js                    →  main.tsx
js/router.js                  →  App.tsx（/ 、/lobby、/new、/room/:code/:role）
js/views/home.js              →  pages/Home.tsx
js/views/lobby.js             →  pages/Lobby.tsx（quickStart / 存档载入；**首页没有复用，是第二份实现**）
js/views/wizard.js            →  pages/Wizard.tsx（含 GIS 现场生成作业轮询）
js/views/director.js          →  pages/DirectorView.tsx
js/views/side.js              →  pages/SideView.tsx
js/views/spectator.js         →  pages/SpectatorView.tsx
js/map.js                     →  map/MapView.ts（canvas 内核，1130 行）+ components/HexMap.tsx
js/hex.js                     →  map/hex.ts
js/api.js                     →  services/api.ts
js/ws.js                      →  room/RoomSocket.tsx + room/protocol.ts
js/util.js                    →  就近内联在各页；提示音独立成 services/sound.ts
css/app.css                   →  App.css + index.css
```

面板没有继续拆成 `UnitPanel/DraftBar/ContactList/EventLog` 这类小组件：它们各自只有一处使用，直接写在三个视图页里，拆了反而要多传一层 props。房间容器是 `pages/RoomPage.tsx`（按 role 分支），单位详情按需取全量走 `room/useUnitFull.ts`。

## 搬迁中真踩到的坑（保留，别缩成"已完成"）

- **CSS 溢出规则丢了**。`web/css/app.css:61` 有 `.scroll{overflow:auto}`，搬到 React 时没带上，结果超长的接触列表/事件日志直接画到相邻面板上。修的时候第一版给非 `.grow` 面板加 `flex:0 1 auto`，列一溢出 `.scroll` 就被压成 0 高、整块列表看不见。最终规则：`.panel .scroll{flex:1;min-height:0;overflow:auto}` + `.terminal .col .panel:not(.grow){flex:0 0 auto;max-height:28%}`。**DOM 矩形探针看不出来（元素被裁切时矩形本来就是超界的），必须目视截图**——这条是 §13.6 取证口径的由来。
- **同一类缺陷不止一处：向导页整块 CSS 没搬过来**。#92 做正反双向 CSS 差分（`web/css/app.css` 199 条 ↔ 本项目 307 条；本项目 105 个 `className` 字面量 token 里有 6 个找不到任何规则）后查出四组，全部只影响观感、不影响点得到，所以之前的功能级 E2E 没发现：`.steps/.st/.st.on`（五个步骤标题竖排成整行纯文本，**当前步与其余步长得一模一样**）、`.wiz/.wiz .body/.wiz .side`（`1fr 320px` 双栏塌成单列、两栏各自滚动丢失）、`.oob-table`（只搬了 `.num` 一条，表格不 `border-collapse`、单元格零内边距）、`#nav a.on` 被改名成 `.nav a.active` 而 `Layout.tsx` 输出的是 `on`（顶栏当前页永不高亮）。**补 `.wiz` 时还有个连锁**：它原本靠内联 `height:calc(100% - 78px)` 撑高，网格一恢复页脚"上一步/下一步"就被顶出视口；改成 `.wizard-page` 做 flex 列 + `.wiz{flex:1 1 auto;min-height:0}` + 独立 `.wiz-foot{flex:0 0 auto}` 才对。逐条数字与截图见设计文档 §13.6 的"移植遗漏对照清单"。
- **`tsc --noEmit` 不算过**。项目用 project references，门槛命令必须是 `npx tsc -b`。
- **令牌要活过整页刷新**。同 token 重连才能接管原会话（`RoomSocket.tsx` 存 `localStorage`，按房间码分键），否则会出现"能发指令、收不到推送"的孤儿会话。
- **顶号与占用要停重连**。阵营位被他人占用时服务端回 `type:error`（不是 HTTP 409）再关连接；前端识别到含"角色位"的错误帧要立刻停在提示态，不能进退避重连——两个标签页互顶会变成死循环。
- **天地图瓦片不带 `crossOrigin`**。带了会整批加载失败（对方不回 CORS 头），代价是叠了瓦片的画布被污染、`getImageData`/`toDataURL` 抛 SecurityError。当前前端从不回读像素（拾取走坐标反算）所以无影响；将来要"导出地图 PNG"必须改成服务端代理瓦片。
- **底图密钥来自服务端**。`GET /api/scenarios` 的 `tiles.tk` 读 `data/tianditu.json`（不进版本库），`HexMap.tsx` 拿到后调 `MapView.setTk()`；没有密钥时底图选择器禁用并在画布左下写明原因，六角格照常上色。
- **一键开局与存档载入被搬成了两份**。web 版是 `quickStart()` / `saveList()` 由大厅导出、首页复用；React 版 `Home.tsx` 和 `Lobby.tsx` 各写了一份 `quickStart`（`Home.tsx:16` / `Lobby.tsx:61`）与 `loadSave`（`Home.tsx:47` / `Lobby.tsx:92`），行为一致但实现重复。搬迁时没保住"只有一份"这条约束，属于待合并项（设计文档 §12 已如实记录）。
- **内置浏览器标签会卡死**。Qoder Browser 面板处于隐藏状态时后台标签的 `requestAnimationFrame` 被冻结，canvas 完全不绘制，看起来像"底图坏了"。做像素级验收先把面板打开，或直接用无头 Chrome + CDP。
- **已知残留（#92 拍板：只标注不删）**。`package.json` 里的 `zustand 5.0.15` 源码零 `import`（本项目没有 store，状态在 `room/RoomSocket.tsx` 的 context + 各页 `useState/useRef`）；`App.css` 里 **58 个类名在整个 `src/` 中一次都没出现**（`.order-*`/`.history-*`/`.command-panel`/`.unit-info`/`.wizard-form`/`.form-group`/`.map-placeholder`/`.conn` 等，占 187 个 CSS 类名的三成）——初版脚手架按规划名写的，组件后来用的是 web 的类名。改样式时别把这些当"在用"，也别顺手删：留着不影响运行，删的风险反而更大。

## 与后端联调

```bash
# 终端 1：后端（端口 5018）
cd ../server && dotnet run --project src/HexFront.Api --urls http://127.0.0.1:5018
# 终端 2：前端
npm run dev        # http://localhost:5173，5173 被占则顺延到 5174
```

`vite.config.ts` 把 `/api` 与 `/ws` 代理到 5018，源码里一律写同源相对路径。四终端入口与回归命令见设计文档 §12 与 §13.6。

**部署提醒**：路由是路径路由不是 hash 路由，静态托管侧必须把未知路径回落到 `index.html`，否则刷新 `/room/XXXX/red` 会 404。
