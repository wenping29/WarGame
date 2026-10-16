# HEXFRONT-REACT

兵棋对抗系统的 React 前端，替代原有的纯 HTML/JS 实现。

## 技术栈

- **React 18** + TypeScript
- **Vite** - 构建工具
- **React Router v6** - 路由管理
- **Zustand** - 轻量级状态管理
- **Axios** - HTTP 客户端（暂未启用）
- **Lucide React** - 图标库

## 项目结构

```
src/
├── components/          # 可复用组件
│   └── Layout.tsx      # 页面布局（顶栏 + 导航）
├── pages/              # 页面组件
│   ├── Home.tsx        # 首页（三入口）
│   ├── Lobby.tsx       # 大厅
│   ├── Wizard.tsx      # 游戏创建向导
│   ├── SideView.tsx    # 红/蓝方视角
│   ├── DirectorView.tsx # 导演部视角
│   └── SpectatorView.tsx # 观战视角
├── App.tsx             # 根组件（路由配置）
├── App.css             # 全局样式
└── main.tsx            # 入口文件
```

## 运行

```bash
npm install      # 安装依赖
npm run dev      # 开发模式（http://localhost:5173）
npm run build    # 生产构建
npm run preview  # 预览生产构建
```

## 当前状态

✅ 基础页面结构和路由
✅ 响应式布局和样式
✅ 本地模式运行（不调用 API）

⏳ 待集成：
- 六角格地图渲染引擎
- 游戏状态管理（Zustand store）
- API 接口调用（连接 C# WebAPI）
- WebSocket/SignalR 实时通信
- 单位指令系统
- 战争迷雾可视化

## 设计原则

1. **零配置开局** - 一键进入导演部体验完整功能
2. **渐进式增强** - 先保证核心流程可用，再添加高级功能
3. **类型安全** - 全面使用 TypeScript，避免运行时错误
4. **组件化** - 地图、单位卡片、指令面板等拆分为独立组件
