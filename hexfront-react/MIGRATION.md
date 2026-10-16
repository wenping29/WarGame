# 前端迁移说明

## 从 web/ 到 hexfront-react/

### 技术栈对比

| 特性 | 旧版 (web/) | 新版 (hexfront-react/) |
|------|------------|------------------------|
| 框架 | 原生 JavaScript ES6 | React 18 + TypeScript |
| 构建工具 | 无（直接运行） | Vite |
| 路由 | hash 路由（手动实现） | React Router v6 |
| 状态管理 | 全局变量 | Zustand |
| HTTP 客户端 | fetch API | Axios（预留） |
| UI 组件 | 手动 DOM 操作 | JSX 组件化 |
| 样式 | CSS | CSS（保留原有设计） |
| 实时通信 | WebSocket | SignalR（待集成） |

### 文件映射关系

```
web/                          →  hexfront-react/src/
─────────────────────────────────────────────────
index.html                    →  index.html (Vite template)
js/main.js                    →  main.tsx
js/router.js                  →  App.tsx (Routes)
js/views/home.js              →  pages/Home.tsx
js/views/lobby.js             →  pages/Lobby.tsx
js/views/wizard.js            →  pages/Wizard.tsx
js/views/director.js          →  pages/DirectorView.tsx
js/views/side.js              →  pages/SideView.tsx
js/views/spectator.js         →  pages/SpectatorView.tsx
css/app.css                   →  App.css
js/map.js                     →  (待集成地图引擎)
js/api.js                     →  services/api.ts (待创建)
js/ws.js                      →  services/signalr.ts (待创建)
```

### 功能完成度

#### ✅ 已完成
- [x] 项目初始化（Vite + React + TS）
- [x] 路由配置（6个页面）
- [x] 基础布局和导航
- [x] 响应式样式（保留原设计）
- [x] 状态管理（Zustand store）
- [x] 开发服务器热更新
- [x] 生产构建优化

#### ⏳ 待集成
- [ ] 六角格地图渲染引擎（Canvas/WebGL）
- [ ] C# WebAPI 接口调用
- [ ] SignalR 实时通信
- [ ] 单位指令系统（移动/攻击/挖掘等）
- [ ] 战争迷雾可视化
- [ ] 回合推进动画
- [ ] 存档/读档功能
- [ ] AI 对手集成

### 本地运行

```bash
cd hexfront-react
npm install        # 首次运行需要安装依赖
npm run dev        # 开发模式（http://localhost:5173）
npm run build      # 生产构建
```

### 与后端集成计划

当前 React 前端为**纯本地模式**，所有数据都是模拟的。后续集成步骤：

1. **启用 API 服务层**
   ```typescript
   // services/api.ts
   import axios from 'axios'
   
   const api = axios.create({
     baseURL: 'http://localhost:5000/api'
   })
   
   export const getGameState = () => api.get('/game/state')
   export const sendOrders = (orders) => api.post('/game/orders', orders)
   ```

2. **集成 SignalR Hub**
   ```typescript
   // services/signalr.ts
   import * as signalR from '@microsoft/signalr'
   
   const connection = new signalR.HubConnectionBuilder()
     .withUrl('http://localhost:5000/hub')
     .build()
   
   connection.on('OnState', (update) => {
     // 更新游戏状态
   })
   ```

3. **更新 Zustand store**
   ```typescript
   // store/gameStore.ts
   import { api } from '../services/api'
   
   export const useGameStore = create((set) => ({
     loadGame: async (id) => {
       const state = await api.get(`/game/${id}`)
       set(state.data)
     }
   }))
   ```

### 优势

1. **类型安全** - TypeScript 在编译时捕获错误
2. **组件复用** - React 组件可在多处使用
3. **热更新** - 修改代码立即看到效果
4. **生态丰富** - 可使用 npm 海量库
5. **性能优化** - Virtual DOM + 按需渲染
6. **可维护性** - 清晰的项目结构和类型定义

### 下一步

优先集成：
1. 六角格地图渲染（Canvas 2D）
2. 基础单位显示和选择
3. 简单的移动指令
4. 连接 C# WebAPI 获取真实数据
