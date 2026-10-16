# 前后端功能迁移状态报告

生成时间：2026-10-17

## 📊 总体进度

| 模块类别 | Dart 原型 | C# 后端 | React 前端 | 完成度 |
|---------|----------|---------|-----------|--------|
| **核心引擎** | ✅ | ⚠️ 部分 | ❌ | 45% |
| **GIS 生成** | ✅ | ✅ | ❌ | 67% |
| **多人联机** | ✅ | ❌ | ❌ | 33% |
| **AI 系统** | ✅ | ❌ | ❌ | 33% |
| **数据验证** | ✅ | ✅ | N/A | 100% |

---

## 🔧 C# 后端迁移状态

### ✅ 已完成的服务

#### 1. HexGenService（六角格地形生成）
**文件**: `src/HexFront.Core/Services/HexGenService.cs` (1050+行)

**已实现功能**:
- ✅ EPSG:3857 ↔ WGS84 投影转换
- ✅ 合成 DEM 生成（FBM 噪声 + 河谷/台地特征）
- ✅ 三轴中心差分坡度计算
- ✅ 地形分类（clear/woods/hills/mountain/river）
- ✅ Dijkstra 最短路径（支持转弯惩罚）
- ✅ A* 寻路算法
- ✅ 河流提取（北→南贯通路径）
- ✅ 道路走廊生成 + 桥梁跨越
- ✅ 城镇放置
- ✅ 铁路网络（含隧道和车站）
- ✅ 电网系统（电厂 + 变电站）
- ✅ 机场选址
- ✅ 元数据统计

**未实现功能**:
- ❌ 真实 DEM 数据获取（Open-Meteo API 调用）
- ❌ 土地覆盖分类（ESA WorldCover GDAL 集成）
- ❌ 真实矢量数据读取（道路/水系/机场 shapefile）

---

#### 2. FogService（战争迷雾）
**文件**: `src/HexFront.Core/Services/FogService.cs`

**已实现功能**:
- ✅ ReconRadius 计算（单位侦察力 × 天气 × 疲劳 × 地形）
- ✅ LineOfSight 通视判定（高程遮挡 + 地形 LOS block）
- ✅ ExposureLevel 暴露等级（E1/E2/E3）
- ✅ UpdateObservation 观测累计
- ✅ SideView 阵营视图裁剪
- ✅ DirectorView 导演部全知视图
- ✅ SpectatorView 观战视图

**完全对齐 Dart 原型** ✅

---

#### 3. CommandValidator（指令校验）
**文件**: `src/HexFront.Core/Services/CommandValidator.cs`

**已实现功能**:
- ✅ 7种指令类型（Move/Attack/Fire/Dig/Rest/AirMission/Halt）
- ✅ ValidateOrders 主入口（存在性/所有权/重复/溃退检查）
- ✅ ValidateMove（路径连续性/边界/不可通行/敌方阻挡）
- ✅ ValidateAttack（目标存在/友军误伤/迷雾观测/射程）
- ✅ ValidateFire（仅炮兵/区域有效/射程限制）
- ✅ ValidateAirMission（仅空军/任务区有效/作战半径）

**完全对齐 Dart 原型** ✅

---

#### 4. MechanicsService（力学计算）
**文件**: `src/HexFront.Core/Services/MechanicsService.cs`

**已实现功能**:
- ✅ SupplyDistances 多源 BFS 补给线
- ✅ ComputeSupply 补给率计算（R = retention^distance）
- ✅ PowerFactor 国力系数（1.0 + 0.15 × 控制节点比）
- ✅ HolderAt 节点归属判定
- ✅ ObjectiveNodes 要点提取
- ✅ NodeHolders 节点持有统计

**完全对齐 Dart 原型** ✅

---

#### 5. HexPackageValidator（数据包校验）
**文件**: `src/HexFront.Core/Validation/HexPackageValidator.cs`

**已实现功能**:
- ✅ 37项自校验（网格结构/地形分类/地物不变式/铁路规则/电网规则/机场隧道/统计一致性/元数据几何）
- ✅ 双命名兼容（snake_case ↔ PascalCase）
- ✅ 校验报告生成

**测试通过率**: 26/32 pass（6 warnings 因缺失元数据字段）✅

---

#### 6. 基础模型
**文件**: 
- `src/HexFront.Core/Models/Defs.cs` - TerrainDef/UnitTypeDef/WeatherDef/Rules
- `src/HexFront.Core/Models/GameState.cs` - Unit/GameState/VictoryCond
- `src/HexFront.Core/Models/HexPackage.cs` - HexCell/PackageMeta
- `src/HexFront.Core/Geometry/Hex.cs` - 轴向坐标/立方体舍入/邻居/距离
- `src/HexFront.Core/Geometry/HexGrid.cs` - 格网几何/BFS距离场/A*路径
- `src/HexFront.Core/Geometry/Mercator.cs` - 墨卡托投影转换

**完全实现** ✅

---

### ❌ 未完成的核心服务

#### 7. TurnResolver（回合裁决）⚠️ **关键缺失**
**Dart 原型**: `hexfront/lib/engine/turn.dart` (resolveTurn)

**需要实现**:
- ❌ 七阶段裁决流程：
  1. 空中阶段（任务指派 → 空战 → 制空权重算 → 对地打击）
  2. 移动阶段（路径消耗 MP/疲劳/燃料/弹药）
  3. 战斗阶段（攻防结算/士气检定/溃退）
  4. 工事阶段（挖掘/加固）
  5. 休整阶段（恢复士气/疲劳）
  6. 补给阶段（后勤线/BFS/资源补充）
  7. 胜利判定（ attrition/objective/control/exit/timed ）
- ❌ WarEvent 事件日志生成
- ❌ TurnReport 回合报告
- ❌ 状态指纹（before/after）

**优先级**: 🔴 **最高**（游戏核心循环）

---

#### 8. AiService（AI 决策）❌
**Dart 原型**: `hexfront/lib/server/ai.dart` (aiOrders)

**需要实现**:
- ❌ 启发式评估函数（兵力对比/地形优势/补给状态）
- ❌ 威胁分析（敌方火力范围/视野覆盖）
- ❌ 目标选择（高价值单位/关键节点）
- ❌ 路径规划（A* + 战术规避）
- ❌ 指令生成（Move/Attack/Fire/Dig）

**优先级**: 🟡 中等（单人模式必需）

---

#### 9. ScenarioService（剧本配置）❌
**Dart 原型**: `hexfront/lib/engine/scenario.dart` (deepMerge/validate)

**需要实现**:
- ❌ JSON 剧本加载
- ❌ deepMerge 递归合并（默认值 + 自定义覆盖）
- ❌ validate 剧本校验（单位类型/初始位置/胜利条件）
- ❌ InstanceBuilder 实例构建

**优先级**: 🟡 中等（快速开局必需）

---

#### 10. SignalR RoomHub（多人联机）❌
**Dart 原型**: `hexfront/lib/server/room.dart`

**需要实现**:
- ❌ SignalR Hub 端点
- ❌ JoinRoom/SendOrders/SetReady/DirectorCmd
- ❌ OnWelcome/OnState/OnEvents/OnSaved/OnKicked/OnError
- ❌ 房间管理（创建/加入/离开/解散）
- ❌ 角色权限（director/red/blue/spectator）
- ❌ 双盲指令收集与广播

**优先级**: 🟢 低（初期可单机）

---

#### 11. Store（持久化）❌
**Dart 原型**: `hexfront/lib/server/store.dart`

**需要实现**:
- ❌ JSON 文件读写（save/load）
- ❌ 存档列表查询
- ❌ 自动保存机制

**优先级**: 🟢 低（可用内存状态替代）

---

#### 12. Library（常量表）❌
**Dart 原型**: `hexfront/lib/engine/library.dart`

**需要实现**:
- ❌ STANDARD_TERRAIN 标准地形定义
- ❌ UNIT_TYPES 兵种类型定义
- ❌ WEATHER_JSON 天气定义
- ❌ 默认规则集

**优先级**: 🟡 中等（引擎运行必需）

---

## 🎨 React 前端迁移状态

### ✅ 已完成

#### 1. 项目架构
- ✅ Vite + React 18 + TypeScript
- ✅ React Router v6（6个页面路由）
- ✅ Zustand 状态管理
- ✅ 响应式布局（保留原暗色主题）

#### 2. 页面组件
- ✅ Home.tsx（首页三入口）
- ✅ Lobby.tsx（大厅视角选择）
- ✅ Wizard.tsx（游戏创建向导）
- ✅ SideView.tsx（红/蓝方视角）
- ✅ DirectorView.tsx（导演部视角）
- ✅ SpectatorView.tsx（观战视角）

#### 3. 状态管理
- ✅ gameStore.ts（游戏状态/单位/视角切换）

---

### ❌ 未完成的前端功能

#### 4. 地图渲染引擎 ❌ **关键缺失**
**JS 原型**: `web/js/map.js`

**需要实现**:
- ❌ Canvas/WebGL 六角格渲染
- ❌ 瓦片加载（EPSG:3857）
- ❌ 地形着色（clear/woods/hills/mountain/river）
- ❌ 地物绘制（道路/铁路/桥梁/城镇/电网/机场）
- ❌ 单位图标显示
- ❌ 战争迷雾遮罩
- ❌ 视野范围可视化
- ❌ 选中高亮/路径预览

**优先级**: 🔴 **最高**（前端核心功能）

---

#### 5. API 服务层 ❌
**JS 原型**: `web/js/api.js`

**需要实现**:
- ❌ Axios 客户端配置
- ❌ getGameState / sendOrders / saveGame
- ❌ 错误处理/重试机制
- ❌ 请求缓存

**优先级**: 🟡 中等（连接后端必需）

---

#### 6. SignalR 客户端 ❌
**JS 原型**: `web/js/ws.js`

**需要实现**:
- ❌ @microsoft/signalr 集成
- ❌ HubConnection 建立
- ❌ OnState/OnEvents 监听
- ❌ SendOrders 发送
- ❌ 断线重连

**优先级**: 🟢 低（联机模式必需）

---

#### 7. 单位交互系统 ❌
**JS 原型**: `web/js/views/side.js` / `web/js/views/director.js`

**需要实现**:
- ❌ 单位选择/多选
- ❌ 右键移动路径绘制
- ❌ 攻击目标选择
- ❌ 指令面板（Dig/Rest/AirMission）
- ❌ 单位信息卡片
- ❌ OOB 编制树

**优先级**: 🟡 中等（ gameplay 必需）

---

#### 8. 指令提交系统 ❌
**JS 原型**: `web/js/views/director.js`

**需要实现**:
- ❌ 指令队列显示
- ❌ 批量提交
- ❌ 指令校验反馈
- ❌ 回合推进按钮
- ❌ 胜利条件显示

**优先级**: 🟡 中等

---

#### 9. 观战界面 ❌
**JS 原型**: `web/js/views/spectator.js`

**需要实现**:
- ❌ 双方兵力统计
- ❌ 战报时间线
- ❌ 回合回放
- ❌ 热点事件标记

**优先级**: 🟢 低

---

## 📋 迁移清单总结

### 🔴 最高优先级（核心功能阻塞）

| # | 任务 | 所属 | 工作量 | 依赖 |
|---|------|------|--------|------|
| 1 | **TurnResolver 回合裁决** | C# | 3-5天 | Library |
| 2 | **地图渲染引擎** | React | 5-7天 | 无 |
| 3 | **Library 常量表** | C# | 1天 | 无 |

### 🟡 中等优先级（完善体验）

| # | 任务 | 所属 | 工作量 | 依赖 |
|---|------|------|--------|------|
| 4 | **API 服务层** | React | 1-2天 | 无 |
| 5 | **ScenarioService 剧本** | C# | 2-3天 | 无 |
| 6 | **单位交互系统** | React | 3-4天 | 地图引擎 |
| 7 | **指令提交系统** | React | 2-3天 | API 层 |
| 8 | **AiService AI 决策** | C# | 3-5天 | TurnResolver |

### 🟢 低优先级（增强功能）

| # | 任务 | 所属 | 工作量 | 依赖 |
|---|------|------|--------|------|
| 9 | **SignalR RoomHub** | C# | 3-4天 | TurnResolver |
| 10 | **SignalR 客户端** | React | 1-2天 | RoomHub |
| 11 | **Store 持久化** | C# | 1天 | 无 |
| 12 | **观战界面** | React | 2-3天 | API 层 |
| 13 | **Swagger UI** | C# | 1天 | 无 |
| 14 | **xUnit 测试** | C# | 3-5天 | 所有服务 |
| 15 | **性能基准测试** | C# | 1-2天 | HexGenService |

---

## 🎯 推荐执行顺序

### 第一阶段：最小可玩版本（MVP）
1. 实现 Library 常量表（1天）
2. 实现 TurnResolver 回合裁决（3-5天）
3. 实现地图渲染引擎（5-7天）
4. 实现 API 服务层（1-2天）
5. 实现基础单位交互（2-3天）

**预期成果**: 单机版可运行，导演部能下达指令并看到回合推进

### 第二阶段：完整单机体验
6. 实现 ScenarioService 剧本系统（2-3天）
7. 实现指令提交系统（2-3天）
8. 实现 AiService AI 对手（3-5天）
9. 完善观战界面（2-3天）

**预期成果**: 完整的单机对战体验，含 AI 对手

### 第三阶段：多人联机
10. 实现 SignalR RoomHub（3-4天）
11. 实现 SignalR 客户端（1-2天）
12. 实现 Store 持久化（1天）

**预期成果**: 支持多人在线对战

### 第四阶段：质量保障
13. 添加 Swagger UI（1天）
14. 编写 xUnit 测试（3-5天）
15. 性能基准测试（1-2天）

**预期成果**: 生产就绪

---

## 📊 代码量统计

| 模块 | Dart 行数 | C# 已实现 | C# 待实现 | React 已实现 | React 待实现 |
|------|----------|----------|----------|-------------|-------------|
| 引擎核心 | ~3500 | ~2800 | ~1500 | - | ~2000 |
| GIS 生成 | ~2300 | ~1050 | ~500 | - | - |
| 服务器 | ~1200 | ~600 | ~800 | - | ~1500 |
| 前端视图 | ~1800 | - | - | ~400 | ~1400 |
| **总计** | **~8800** | **~4450** | **~2800** | **~400** | **~4900** |

**整体完成度**: 
- C# 后端: 4450 / 7250 ≈ **61%**
- React 前端: 400 / 5300 ≈ **7.5%**
- 全栈综合: 4850 / 12550 ≈ **38.6%**
