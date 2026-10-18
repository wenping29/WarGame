# HEXFRONT-REACT

兵棋对抗系统（HEXFRONT-WEB）的 React 前端，**唯一主前端**；原生 JS 版 `../web/` 已冻结为**只读参考**（#92 拍板：不删除，理由与成本见 [../web/README.md](../web/README.md)）。已接真实 C# 后端并做过四终端浏览器端到端验证。

产品与协议口径都在 [../HEXFRONT-WEB产品设计文档.md](../HEXFRONT-WEB产品设计文档.md)（下称"设计文档"）：技术选型 §13.3、运行方式 §12、验收基线与取证口径 §13.6。搬迁对照表与踩坑清单在同目录 [MIGRATION.md](MIGRATION.md)。

## 技术栈（实收）

- **React 19** + TypeScript ~6.0 + Vite 8
- **React Router 7**（`BrowserRouter`，路径路由）
- **axios**（REST）+ 原生 WebSocket `/ws`（**不是 SignalR**）
- **原生 canvas** 地图渲染（`src/map/MapView.ts`）——没有 Konva、没有 Ant Design
- 状态由 `room/RoomSocket.tsx` 的 hook + 各页 `useState/useRef` 持有；**没有 Zustand store**（依赖里的 `zustand` 零引用）
- 图标 `lucide-react`，lint `oxlint`

## 目录

```
src/
├── App.tsx / main.tsx        # 路由：/  /lobby  /new  /room/:code/:role
├── pages/                    # Home · Lobby · Wizard（5 步创建向导）· RoomPage
│   ├── SideView.tsx          # 红/蓝：己方全量 + 接触情报 + 指令草案 + 就绪
│   ├── DirectorView.tsx      # 导演部：全知态势 + 导调 + 强制结算 + 战报因子链
│   └── SpectatorView.tsx     # 观战：公开情报层 + 回放帧
├── components/               # HexMap（canvas 外壳）· Layout（顶栏 + 三列终端）· Toast
├── map/                      # MapView.ts（渲染内核）· hex.ts（odd-r 几何与稳定序）
├── room/                     # RoomSocket.tsx（连接/重连/顶号）· protocol.ts · useUnitFull.ts
├── services/                 # api.ts（axios 封装全部 /api）· sound.ts（提示音）
└── App.css · index.css       # 深色终端风视觉 token（从 web/css/app.css 移植）
```

## 运行

需要后端先在 5018 上跑着（`../HexFront.CSharp`，见设计文档 §12）：

```bash
npm install
npm run dev        # http://localhost:5173（被占则顺延到 5174），/api 与 /ws 由 vite 代理到 5018
npm run build      # = tsc -b && vite build
npm run preview
npm run lint       # oxlint
```

四终端入口：`/`（首页三入口）· `/lobby` · `/new` · `/room/{房间码}/red|blue|director|spectator`。

## 验收门槛

```bash
npx tsc -b         # 类型门槛。--noEmit 不算：项目用 project references，只有 -b 会全量检查
```

后端侧的 `dotnet test`（244 项）与 `node HexFront.CSharp/scripts/ws-smoke.mjs`（16 组）在 `../HexFront.CSharp`，数字含义见设计文档 §13.6。UI 改动必须在真实浏览器里跑通并截图复核，不接受只看单测绿。

## 已知未测

地图缩放/拖拽的 60fps 数字（归 #93 性能基线）。移动端适配与对外部署未开工。

## 设计原则

1. **零配置开局** —— 一键进入导演部体验完整功能，首页只留三个入口
2. **信息分级即玩法** —— E1 只给兵力档、E2 给兵种、E3 才给精确兵员；视图裁剪错了就是泄漏对手底牌
3. **类型与协议对齐后端** —— REST/WS 字段一律 snake_case，契约类型集中在 `room/protocol.ts`
4. **提示要有反馈** —— 指令受理、交战、顶号都有提示音与 Toast；异常态（房间不存在、角色位占用、密钥未配置）在界面上写明原因而不是空白
