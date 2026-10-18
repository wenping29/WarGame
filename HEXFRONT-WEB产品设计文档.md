# HEXFRONT-WEB 产品设计文档 · Web 多人对抗兵棋

| 项目 | 内容 |
| --- | --- |
| 文档性质 | **新立项**产品设计与技术架构（目标态），非既有实现说明 |
| 编制日期 | 2026-10-16 |
| 更新日期 | 2026-10-18（P7-d 文档一致性：§12 换成 C# + React 运行手册、§13 回填"规划 vs 实收"、新增 §13.6 交付基线与取证口径；P7-e `web/` 处置：§13.6 补"移植遗漏对照清单 + 已知残留"，`web/README.md` 落冻结声明） |
| 产品代号 | HEXFRONT-WEB（铁幕兵棋 · Web 对抗版） |
| 一句话 | 引擎权威的 Web 多人兵棋：导演部/红/蓝/观战四终端，真实 GIS 生成六角格，在线底图，战争迷雾隔离信息，剧本→副本→存档全链路 |
| 与既有资产关系 | 复用并大改 `D:\Project\Wargames\hexfront_core`（Dart 确定性内核）；沿用 `wargame-prototype.html` 的深色战术视觉 token |

---

## 0. 立项声明（必须先读的三点）

1. **这与 HEXFRONT 既有路线不是一条线。** `dev-design-doc.html` 第 1.3 节把「Web/PC 客户端、实时协作推演」明确列为**边界外（P3+）**；`tech-roadmap.html` 里程碑 P0–P5 里**没有**多人对抗、Web、实时观战。本产品是把它划在边界外的东西变成主线，因此不能"照 roadmap 补做"，需独立立项、独立排期。
2. **`产品说明文档.md` 描述的 Python 系统在本机不存在**（`engine/server/scenarios/tests` 全无，D 盘无一份 `.py`）。它所述 84 剧本/76 测试/Python 引擎不作为本设计的事实依据；本设计只采信可核对的 `hexfront_core` Dart 代码与三份 HTML 文档。
3. **现有内核是 P0 骨架，撑不起多人。** `hexfront_core` 约 940 行有效代码：无序列化（→ 无法存档）、无视野计算、无网络/会话、兵种枚举硬编码（→ 无法自定义 OOB）、移动路径不校验相邻性（→ 客户端可跳格）、断供单位 CF 直接归零、溃退恢复分支不可达。这些在 §9 列为必补项，而不是"后续优化"。

---

## 1. 产品定位

面向**军事爱好者、兵棋推演教学、机构导调**的 Web 多人对抗平台。核心范式三条：

- **引擎权威（engine-authoritative）**：裁决只在服务端发生。前端只渲染与下令，永不持有全量真实态。这既是反作弊的底线，也是迷雾唯一可信的实现方式。
- **信息即武器**：红蓝双方对抗的不是棋子，而是"我知道你不知道什么"。迷雾是产品的核心乐趣来源，不是渲染特效。
- **导调可介入**：导演部是真实推演的灵魂角色 —— 可看全局、可注入情报、可裁决争议、可暂停/回溯，教学与竞技都依赖它。

目标用户与场景：

| 场景 | 谁在用 | 关键诉求 |
| --- | --- | --- |
| 竞技对抗 | 红蓝指挥员各自开浏览器 | 迷雾严格隔离、指令双盲、断线重连 |
| 教学推演 | 教官任导演部 | 实时全局态势、暂停点评、注入突发情况 |
| 复盘研究 | 观战者/事后研究者 | 存档、逐帧回放、战报 |
| UGC 内容 | 剧本作者 | 从剧本造副本、自定义 OOB 与战场环境 |

---

## 2. 角色模型：导演部 / 红 / 蓝 / 观战

一个副本实例（Instance）在运行时是一个**房间（Room）**，四种终端角色：

| 角色 | 视角 | 可下单 | 可触发结算 | 特有权限 |
| --- | --- | --- | --- | --- |
| `RED` 红方指挥 | 己方全知 + 敌方已暴露 | ✅ 本方 | ❌ | 本方指令草案、请求空中任务、增援申请 |
| `BLUE` 蓝方指挥 | 同上（蓝方） | ✅ 本方 | ❌ | 同上 |
| `DIRECTOR` 导演部 | **全知**：双方真实态 + 已受领未结算指令 | ❌（除导调干预） | ✅ | 暂停/继续、注入情报与事件、改天气、裁决争议、强制结算、授予观战全知 |
| `SPECTATOR` 观战 | 受限：公开情报层（默认 = 红蓝已暴露的并集），可配延迟 | ❌ | ❌ | 无 |

关键设计决策：

- **导演部不下场**。导演部的权力是"控局"而非"指挥"，避免既当裁判又当运动员。需要下场时用"代理阵营"临时授权，且该授权对全房间公开可见（审计事件）。
- **红蓝可联军**：一个阵营可容纳多个连接（方面军/旅团各开一屏），同阵营共享视野与指挥权，指令按单位归属校验。这是排级以上推演的刚需。
- **观战默认只看公开层**，但提供两档由导演部或副本设置决定：`restricted`（公开情报 + 可选延迟 N 回合，竞技用）与 `omniscient`（全知，教学/赛后复盘用）。
- **同一浏览器可多开**做双人同桌推演（教学常见），因此角色会话用 `roomId + sessionToken` 标识，不与登录账号强绑定（账号体系见 §11 待拍板）。

---

## 3. 战争迷雾与暴露模型（核心）

### 3.1 服务端裁剪，而非前端隐藏

服务端每回合对每个阵营生成 `side_view`：

```
side_view(side) = {
  map + weather + terrain        // 地形/环境是公开知识（真实 GIS 已定），不下发单位真实态
  own_units:   全量真实属性       // 己方永远全知
  contacts:    [Contact]         // 敌方/中立"已暴露"目标
  last_known:  [Track]           // 曾见过但当前丢失的目标（陈旧情报）
}
```

**全量真实态永不出服务端**。这是铁律：任何"下发全局、前端 CSS 遮住"的做法一律否决 —— 一个 F12 就把整个产品变成笑话。

### 3.2 观测源与侦察强度

每格每回合累计 `obsPoints[side][hex]`，来源：

| 观测源 | 机制 | 修正 |
| --- | --- | --- |
| 地面单位目视/侦察 | `recon` 属性 → 半径 R 格，中心 3R、边缘 1R 强度 | 夜战 −50%、雾/暴雨 −40%、晴 −0%；观察方在林地/谷地 −20% |
| 高地增益 | 目标格与观察格高差 > 阈值 → +20% 并延长通视 | 依赖真实 DEM 高程 |
| 通视遮挡（LOS） | 观察格→目标格 Bresenham 六角直线，中间存在高程超过视线锥的格 → 阻断，仅按"最后目击"保留 | 这是 GIS 的价值兑现点 |
| 雷达站 | 定向扇区、对空中目标远距、对地面目标弱 | 可被压制/摧毁 → 视野缺口 |
| 空中侦察 | 侦察机航线、覆盖扇区 | 遭遇敌方战斗机 → 情报可信度降级、可能损失平台 |
| 导演部注入 | 导演部可人工授予某方某格情报 | 记录审计事件 |

### 3.3 暴露分级（回答"只能看到已暴露的资源装备人员"）

观测累计值决定对每个敌方单位知道多少，分四级，**逐级解锁且需持续维持**：

| 级别 | 触发观测强度 | 暴露内容 |
| --- | --- | --- |
| `E0` 未知 | — | 不可见 |
| `E1` 接触 | ≥ 1 | 某格有敌情：**方位/兵力规模档位**（弱/中/强），不知兵种细节 |
| `E2` 判明 | ≥ 3 | + **兵种构成**、建制级别、工事状态、大致士气/疲劳档位 |
| `E3` 锁定 | ≥ 6 | + **装备明细**（主战装备型号与数量）、**人员水平**（训练度/经验/指挥官）、精确位置与动向 |

设计要点：

- **位置有"当前性"问题**：`E1+` 只保证"该格有敌"，单位移动后原格情报转为 `Track`（最后目击位置 + 陈旧回合数）。陈旧度越大，导演部/引擎按移动可能性给出的"可能位置带"越大 —— 这正是迷雾里"猜"的乐趣来源。
- **装备与人员水平是最高价值情报**（E3），必须难拿：需要抵近/空中侦察/导演部注入。这样"派出侦察"成为真正的决策，而不是自动过场。
- **暴露是可逆的**：脱离观测 N 回合后逐级降级（E3→E2→E1→Track→消失）。
- **己方对自己永远透明**，不受迷雾影响（包括装备、人员、补给、疲劳）。

### 3.4 迷雾下的指令可行性

- 只能对**己方单位**下所有令；只能对**已暴露（E1+）目标**下攻击令。打未知目标 → 服务端拒绝，返回 `reason=not_observed`。
- 允许"盲打"例外：炮兵对**坐标/预判区域**火力覆盖（不要求目视），但精度降级、误伤风险 —— 真实且好用。
- 移动路径只校验己方可通行性；进入未知格可能"遭遇"（结算时揭晓 → 遭遇战事件）。

---

## 4. 三层内容模型：剧本 → 副本 → 存档

```
剧本 Scenario (模板，可复用，作者维护)
   │  默认 OOB / 地形 / 天气 / 胜负 / 规则覆盖
   └─▶ 创建副本 Instantiate
          │  玩家/导演部可选覆盖任一层（不改 = 用剧本默认）
          ▼
        副本 Instance (一次可续玩的局，带 seed 与初始态快照)
           │  运行时 = Room（角色接入、回合推进）
           └─▶ 存档 Save (初始快照 + 每回合指令 + 周期全量快照)
                  │ 载入 = 恢复为新的 Room
                  └─▶ 回放 Replay（逐帧重放，确定性可校验）
```

- **剧本**是内容资产：JSON，含 §5 全部默认值。可被无数次实例化，本身不可变（要改用"另存为新剧本"）。
- **副本**= 剧本 + 覆盖层 + 随机种子 + 初始态快照。副本名、创建者、阵营占用、模式（对抗/教学/排位）都在这里。
- **存档**：追加式指令日志（每回合每方的双盲指令 + 结算顺序）+ 周期性全量快照（每 K 回合）。载入时"快照 + 重放尾部"，回放时对拍 `toSnapshot()` 哈希验证引擎确定性。
- **确定性要求**：同 seed + 同指令序列 → 同结果（`SeededRNG` 状态必须入快照，否则跨进程续局必然破坏一致性）。
- 副本另可派生：**从某回合"另开分岐"** 做假设推演（what-if），导演部/教学高频需求。

---

## 5. 创建副本流程（自定义军队 + 战场环境）

向导式 5 步，**每步都是"覆盖 / 不覆盖"，不覆盖即继承剧本默认** —— 这是用户诉求的直接实现。

| 步骤 | 内容 | 覆盖后效果 |
| --- | --- | --- |
| 1 剧本与名称 | 剧本卡片：时代、战场、规模、胜负目标、难度；副本名与随机种子 | 基线 |
| 2 战场环境与六角格 | 三选一：**沿用剧本默认** / **选已有 GIS 数据包**（元信息卡片：格数、对边距、数据层、bbox）/ **按范围现场生成**（经纬度范围框 + 对边距（可"用推荐对边距"，另有档位快选）+ 数据层开关：高程、土地覆盖、水系（真实矢量河湖）、道路）；生成走后台作业，日志逐行回显，完成后自动选中该包 | `overrides.gis_package`；六角格几何与地形全部来自该数据包（见 §6.2） |
| 3 天气与演变 | 初始天气（晴/云/雨/暴雨/雪/雾/沙尘）+ 演变模型（固定 / 逐回合概率转移 / 导演部手控） | 覆盖剧本默认天气 |
| 4 部队编成（OOB 与兵种属性） | 红蓝各：单位清单 + **人员水平**（训练度、经验、士气基线、指挥官能力）；另有**兵种属性覆盖**：机动/火力/防护/侦察/负荷/消耗（本局双方共用的装备基线） | 替换剧本默认兵力；改兵种装备性能并按偏离度**自动重算点数**，预算不足当场报红（见 §7） |
| 5 审阅并创建 | 完整覆盖 JSON 预览 + 服务端校验错误清单 + 回合上限/胜负等剧本默认展示 | 一个剧本可长出竞技版与教学版 |

OOB 自定义的两条约束（否则内容立刻崩）：

- **点数预算（points）**：每个阵营给定额，装备套件/人员水平都标价，红蓝对等。禁止"自定义"变成"堆最强"。导演部可解锁无限预算用于教学。
- **引用完整性**：删除被剧本引用的兵种/阵营时默认拒绝、可强制（沿用既有数据管理的引用扫描思路）。

**人员水平建模**（可被侦察到 E3）：`training`（训练度）、`experience`（经验，影响战斗效率与恢复）、`morale`（士气基线）、`casualtyTolerance`（承受损失能力）、`commandQuality`（指挥官能力）。用一组 0–100 因子进 CF 的 `K_personnel` 与 `K_morale`，而不是造新公式。

---

## 6. 地图与 GIS 六角格（真实数据）

### 6.1 为什么必须用真实 GIS

迷雾的可信度来自地形遮蔽，地形遮蔽需要**真实高程**。平面随机地形做不出"山脊挡住谷地里的团"。所以 GIS 不是"让地图好看"，而是"让玩法成立"。同时在线底图让"我守的是不是真的那道梁"一眼可辨，教学价值极高。

### 6.2 管线

```
真实数据源（已落地）
  ├─ DEM 高程：本地 GeoTIFF `data/dem/global_dem_30s.tif`（EPSG:4326，30″≈0.926 km，SRTM+GEBCO 合成，离线）
  ├─ 土地覆盖：ESA WorldCover 2020 10m（AWS 开放桶，3°×3° 瓦片，按需拉取 + 本地缓存 `data/landcover/`）
  ├─ 水系：**Natural Earth 10m 真实矢量**（本地 GeoPackage `data/water/global_water.gpkg`：rivers 线 + lakes_all 面，EPSG:4326）；
  │        与 DEM 谷底脊线并用——真实河线/湖面按占比叠加判 river/water，谷底脊线兜底保证河带纵向贯通（无 OSM，见 §11 问 11）
  ├─ 道路：由地形派生的通行走廊（**明确标注为派生数据，不是实测路网**）
  └─ 聚落：WorldCover 建筑区占比（为可玩性上限 24 格）
        │
        ▼  tools/gis/hexgen.py（服务端作业调用，产出 data/gis/<包名>.json）
  ① 选区 bbox + **对边距（实地 km）** + pointy-top + EPSG:3857
  ② 每格按**面积聚合**（像点中心 → 墨卡托 → 浮点轴坐标 → 向量立方取整 → bincount 求均值/众数），不是格心单点采样
  ③ 高程均值 + 邻域高差 / **实地**格距 → 坡度 → 平地/丘陵/山地
  ④ 土地覆盖众数 + 水体/建筑占比 → 林地/农田/城市/水
  ⑤ 规则融合 → 兵棋地形类别，并给每格留**覆盖判据** `lc`（可追溯是"真判"还是"代理"）
  ⑥ 真实水系叠加（在修路之前）：Natural Earth 河线穿过的格判 river、湖面按占比 ≥ 阈值判 water；
  │   再叠加 DEM 谷底脊线保证河带纵向贯通；走廊道路 / 桥位（= 道路 ∩ 完整河网）/ 聚落覆盖
  ⑦ 输出：{meta(ox,oy,radius_m,cell_km,**cell_ground_m**,**mercator_scale**,center_lonlat,layers,source,**water**), cells:[{q,r,terrain,elev,slope,lc,road,river,city,obs}]}
        │
        ▼
  在线底图瓦片：天地图 WMTS _w（EPSG:3857）+ 六角格叠加 = 严格对齐，零漂移
```

- **对齐方案**：六角格直接生成在 Web Mercator（EPSG:3857）上，每格中心有确定经纬度；前端按瓦片坐标系投影六角，**天然对齐底图**。这从根上解决旧记录里"单锚点平移对齐、多锚点纠偏待办"的漂移问题 —— 不是纠偏，而是让格网本来就在同一坐标系里生成。
- **对边距 = 实地口径（唯一的格距单位约定）**：用户填的"对边距"永远是**地面公里**，墨卡托侧要乘 `1/cos(纬度)`（51.4°N ⇒ ×1.603）。因此 `radius_m` 存的是墨卡托半径、`cell_ground_m` 存的是实地口径，坡度基线也一律用实地距离。数据包 `meta` 同时记 `mercator_scale` 与全精度 `center_lonlat`，缺 `mercator_scale` 的旧包按 1.0 处理（`--only-validate` 会 WARN 而不是 FAIL）。
- **推荐对边距**：`--plan` 按"格数 ≈ 面积 /(0.866·c²)"在档位阶梯 `0.5/1/1.5/2/2.5/3/5/7.5/10/15/20 km` 上取最接近目标 2400 格、且每边不少于 12 格、不低于高程栅格分辨率（1.0 km）的档位，并把**推荐理由**逐条返回给前端；服务端 `GET /api/gis/plan` 带键缓存，向导进入"按范围生成"即自动请求，档位快选与"用推荐对边距"都读同一份方案，前端不自己算几何。
- **格距与规模**：战斗级 1–2 km/格、战役级 5–10 km/格；单包上限 30000 格（`gisMaxCells`，超限直接拒），生成作业串行排队、20 分钟超时。
- **几何只能来自数据包**：引擎建格时优先用 `meta.ox/oy/radius_m/cell_km/mercator_scale`，**不再**回落到 `environment.cell_km` 默认值——这条曾经是真 bug（换包不改几何，格网与底图错位），最初由 Dart `verify.dart` 第 [7] 组 7 项锁住，现在由 C# 侧的等价 xUnit 用例守着（映射见 `HexFront.CSharp/tests/PARITY-CHECKS.md`）。
- **离线可玩**：数据包是静态 JSON，无 DEM 时可用程序化地形降级（`meta.source` 标注来源）；底图瓦片要密钥且需要联网，加载失败即降级为纯 GIS 上色六角，**玩法不受联网影响**。
- **数据层诚实标注**：`meta.layers` 逐层记 `elevation/landcover/roads/hydrology/settlements/railway/power/tunnels/airports` 的真实/派生/关闭状态，前端元信息卡片直接展示；水系层标注实际用了哪些图层（`natural-earth-rivers+natural-earth-lakes+dem-spine` 等）并附 `meta.water` 溯源（河/湖要素数、缓冲宽度、命中格数）；道路是真实矢量路网（World_Roads RANK=1 干道 + 地形派生走廊补齐渡河点），不冒充实测路网；铁路/电网/隧道/机场按真实源或派生算法分别标注。`--water off` 或本机无 gpkg/无 geopandas 时，水文降级为 DEM 派生河带并在 note 里说明，玩法不受影响。
- **复现命令**：`python tools/gis/hexgen.py --bbox 6.8,51.2,7.3,51.6 --cell-km 1 --name my_site --out data/gis/my_site.json`；`--plan --bbox ...` 只出方案不生成；`--capabilities` 报告 python/GDAL/DEM/土地覆盖/**水系 gpkg/矢量地物**可用性；`--water auto|off` 开关真实矢量水系叠加；`--network auto|off` 开关真实矢量地物（World_Roads + NE 机场）；`--features on|off` 开关派生地物玩法层（铁路/电网/车站/隧道/电厂/变电站/机场）；`--only-validate --out data/gis/xxx.json` 只校验已有数据包（37 项）。浏览器里等价的入口是创建副本向导第 2 步。

### 6.3 地形对玩法的既有 + 新增作用

通行代价（四类机动 foot/horse/motor/mech）、防御加成、观察限制、**通视遮挡（新）**、渡河与桥梁、构筑工事上限。

#### 6.3.1 地物层玩法（已实现 · 2026-10-17）

真实矢量地物（World_Roads 干道 + Natural Earth 机场/桥隧标注）与派生地物层（铁路/火车站/公路桥/隧道/电网/电厂/变电站/野战/干线机场）共同构成战场基础设施网络，直接影响机动、补给与胜利条件。

**数据口径（能真则真，诚实标注）**：

| 地物 | 数据来源 | 处理方式 | 约束不变式 |
|---|---|---|---|
| 公路 | World_Roads.lpk (RANK=1) + 地形派生走廊 | 真实路网为主，派生走廊补渡河点 | bridge ⊆ river |
| 河流 | Natural Earth 10m rivers/lakes + DEM 派生河带 | 真实矢量叠加于 DEM 纵向贯通河带之上 | 河带始终纵向贯通 |
| 铁路 | Dijkstra 最短路径（西边缘→首都→东边缘） | 派生，只在桥位渡河 | 铁路河 ⊆ 桥位 |
| 隧道 | Natural Earth RD_FEATURE=Tunnel + 地形坡度/起伏派生 | 真实在线上非水面格保留，派生限 6 处 | 隧道在线上且不在水面 |
| 机场 | Natural Earth 10m airports (major/international) | 真实点取前 3 个，否则派生一处野战 | 等级 ∈ {1,2} |
| 电厂/变电站 | 地形平坦度 + 距河距离 + 城市分布 | 派生，每个电厂都连到电网 | 电厂⊆平地，变电站⊆聚落 |
| 火车站 | 铁路格 ∩ 聚落格 | 派生 | 车站铁路 |
| 电网线路 | 电厂→变电站→变电站 最小生成树 | 派生，跨河有惩罚 | 线路占比受控 |

**引擎玩法集成**：

| 机制 | 规则 | 代码位置（C# 实收 ← Dart 原口径） |
|---|---|---|
| 铁路机动 | motor/mech ×0.8（与公路 0.65 可叠加 → 0.52）；foot 不搭车不受惠 | `TurnResolver.StepCost()`（← `turn.dart:stepCost()`） |
| 隧道限行 | motor/mech ×1.5，foot ×1.2 | `TurnResolver.StepCost()`（← `turn.dart:stepCost()`） |
| 机场补给节点 | 夺下场站格 + 该方仍有航空兵在场 ⇒ 距离归 0；航空兵撤离后失效 | `MechanicsService.SupplyDistances()`（← `mechanics.dart:supplyDistances()`） |
| 电网系数 | 必须同时掌握「电厂」和「至少一处变电站」才通电；红利 = 1.0 + 0.15×held/nodes，封顶 +15% | `MechanicsService.PowerFactor()`（← `mechanics.dart:powerFactor()`） |
| 电网进补给 | supply_rate × power_factor | `MechanicsService.ComputeSupply()`（← `mechanics.dart:computeSupply()`） |
| 要点归属 | 争夺中（两方同格）不算占；双盲视图只披露己方占领 | `MechanicsService.NodeHoldersByHex()` + `FogService.SideView()`（← `fog.dart:nodeHolders()`） |
| 要点胜利 | `kind: "nodes", amount: N` ⇒ 同时掌握 N 处要点即达成 | `turn.dart:_checkVictory()` |

**迷雾纪律**：要点地理（城镇/车站/机场/电厂/变电站的位置与类别）是公开知识，随 `_common` 下发所有视图；但**归属（谁占了哪个要点）是情报**，`sideView` 只给己方占领的归属，`directorView` 给双方，`spectatorView` 不给任何归属。导演部 HUD 额外显示双方电网系数。

**前端渲染**（实装：`hexfront-react/src/map/MapView.ts` + `components/HexMap.tsx`；下列条目最初由 `web/js/map.js` 定口径，web 客户端已冻结为只读参考（#92 决定不删，见 §13.6））：铁路用深色钢轨底 + 浅色枕木断点（与公路连续实线区分）；输电线用黄色虚线；机场用跑道符号（干线加塔台角标）；电厂用闪电符号；变电站用菱形；火车站用雨棚矩形；隧道用拱形；杆塔只画在线路端点/分叉上；要点归属用阵营色虚线圈。悬停格内显示地物摘要标签（公路/铁路/桥隧/电网/机场/要点名称与归属）。右上角常驻图例（缩略时隐藏）。

### 6.4 地物玩法验证结果

**验证结果**（原 Dart `bin/verify.dart` [9] 节的 23 项，条目名保留当时的口径；现在跑的是 C# xUnit 等价实现，映射表见 `HexFront.CSharp/tests/PARITY-CHECKS.md`）：

```
✓ 铁路/隧道/电网/机场写入引擎格网
✓ 地物不变式：车站⊆铁路 · 桥⊆河 · 隧道在线上且不在水面
✓ 地物字段随存档往返
✓ 铁路使摩托化机动更省，徒步不搭车不受惠
✓ 公路与铁路折扣可叠加（0.65×0.8）
✓ 隧道限行：重装备比徒步更吃亏
✓ 无桥渡河重罚 +2，桥位免罚
✓ 开局双方都不通电（电网系数 1.0）
✓ 只夺电厂仍不通电
✓ 电厂 + 变电站同时掌握才通电（红利封顶 +15%）
✓ 未掌握电网的一方不吃红利
✓ 电网红利进入补给系数（远端最弱格上浮）
✓ 地面部队站上机场 ⇒ 该格成为补给节点（距离归 0）
✓ 航空兵撤离后机场不再供补给（空运需要机组）
✓ 机场等级只用 1/2（野战/干线）
✓ 要点清单齐备（含类别与中文标签）
✓ 各方视图的要点地理一致（地形不是情报）
✓ 双盲视图只披露己方占领（敌方归属属情报）
✓ 导演部视图给出双方电网系数
✓ 观战视图不给要点归属
✓ 落位后红方确握 3 处要点（odd-r 重采样后实测为 5 处，见 §6.5 与 #90 取证）
✓ 要点数量达成即终局（kind=nodes）
✓ 带地物地图可连续推演（14 回合）且无异常
```

**当前回归基线**（2026-10-18 实测）：`dotnet test HexFront.CSharp/tests/HexFront.Tests` **244/244 通过**（引擎侧 201 + 协议侧 40 + 性能基准 3），
`node HexFront.CSharp/scripts/ws-smoke.mjs` 0–15 共 **16 组全 PASS**。原 Dart 侧的 61 项与 32 项是同一批断言的上游口径，映射表在
`HexFront.CSharp/tests/PARITY-CHECKS.md`；Dart 原型（`hexfront/`）自 P7 起只作对照参考，不再是验收入口。

**情报档位与"已消失"卡片的浏览器取证（2026-10-18，四终端无头 Chrome + CDP）**：E1 接触卡只有兵力档（没有 `type`/`personnel`/`commander` 键）、E2 才出现兵种、E3 才给精确兵员与装备；阵亡且仍在本方观测档案里的单位保留一张 `E1 · 已消失`（`status=gone`）卡片，并按 `staleTurns`=3 个回合（age 1→2→3）从档案里剪掉。复现步骤见 §13.6 —— 纯 AI 局 14 回合零歼灭（§11.12），必须用 `personnel_ratio` 压 OOB 才能拿到歼灭链路。

### 6.5 坐标基线（odd-r 偏移格网）· 2026-10-18 定稿

这一节回答"同一个 `(q,r)` 到底指哪一格"。2026-10 从 axial 切到 odd-r 之后，基线只存在于代码注释里，没进文档；
旧 axial 采样的包与新的 odd-r 包混在一起时，同一个 `(q,r)` 会指向不同的格（格网整体被剪切平移），
所以基线必须写死成可判定的条文。

**主键与棋盘**

- pointy-top（尖顶）**odd-r 偏移坐标**：`q` = 列、`r` = 行，**奇数行整体右移半格**，于是 `width × height` 铺成矩形棋盘（axial 是平行四边形，不能矩形化）。
- 一维索引 `index = r*width + q`（`r` 外层、`q` 内层）；落盘 `cells`/`hexes` 数组就按这个索引序排，自校验第「顺序 r外/q内」项锁住它。
- 线上/存档里的格键一律 `"q,r"` 字符串（负坐标不能用整数编码）。
- 格心（EPSG:3857 米）：`x = ox + √3·R·(q + (r&1)/2)`，`y = oy − 1.5·R·r`；`r` 增大即向南。

**与 axial / cube 的换算**（距离、直线、半径这类立方体运算一律先转 axial 再回写 odd-r）

- 正向 `aq = q − (r − (r&1)/2)`，`ar = r`；反向 `q = aq + (ar − (ar&1)/2)`，`r = ar`。
- 距离 = cube 三分量绝对值的最大值；直线 = cube 插值 + 向量立方取整。

**邻格表与遍历序（引擎的"取用序"，属于玩法契约不是实现细节）**

- 6 邻按行奇偶分两张表，**顺时针自 E 起**：偶数行 `(1,0),(0,-1),(-1,-1),(-1,0),(-1,1),(0,1)`；奇数行 `(1,0),(1,-1),(0,-1),(-1,0),(0,1),(1,1)`。三张客户端/服务端表（C#、React、web）与此逐字同序，点击拾取与相邻性判定才不会各说各话。
- `Range(n)`（半径 n 内所有格，含自身）的返回序固定为 **(距离, q, r)**：距离分层连续，同距离先列后行。
  凡是"取第一个可用格"的调用因此等价于"取最近格"，且同种子可重放——剧本落位兜底 `SpawnAt` 的注释本就写着 *find nearest passable free hex*，此前用的是 `dq` 主序嵌套循环，取到的是"撞到的第一个"而不是最近的；已按本条改正。
  这条不是理论洁癖：真河网淹掉了 `rhine_corridor` 的 14 处开局坐标，兜底几乎每局都触发，旧序把 15 个红方单位甩出名义格、其中 10 个甩到半径上限 4 格
  （HQ `R1 (2,9)→(0,12)`、`R21 (3,13)→(0,15)`、工兵/补给 `R16/R17/R18` 全退到 `q=0` 棋盘左缘），改序后同样名义格只挪 ≤2 格。
  开局落位与随机种子无关（实测 seed 1/7/20261018 三组一致），所以这是**基线层的缺陷**，不是平衡或 AI 问题：纵深单位被扔到角上，战线自然推不动。
- Dart 侧 `hexRange` 的同环 tie-break 是 **axial** `(q,r)`，本栈是 **odd-r** `(q,r)`：距离分层一致、环内次序按各自主键。这是采用 odd-r 主键的**有意结果**，不是漏改。
- Dart 的 `hexRing` 在 Dart 仓库里零调用，属死分支，按"不复刻死代码"的基准**不移植**；"环"这一档由 `Range` 的距离分层表达（距离 `d` 的那一环恰好 `6·d` 格），已由用例锁住。

**同一基线的五份实现**（改一处要五处同步，任何一份漂移都会表现为"点错了格"）

| 用途 | 位置 |
|---|---|
| 引擎几何 | `HexFront.CSharp/src/HexFront.Core/Geometry/Hex.cs`、`HexGrid.cs` |
| C# 生成器 ox/oy 反推 | `HexFront.CSharp/src/HexFront.Core/Services/HexGenService.cs` |
| Python 生成器 | `tools/gis/hexgen.py` 的 `HexGrid` |
| React 前端（主前端） | `hexfront-react/src/map/hex.ts` |
| web 客户端（已冻结，只读参考） | `web/js/hex.js` |

前端 `hexRange()` 目前只用来构造射程高亮的集合，与遍历序无关；若将来要做"就近目标"这类取首元素的逻辑，必须按上面的 (距离, q, r) 同序，不能另立一套。

**中心公式与数据包基线判别**

- `ox/oy` 由"格网几何中心 = bbox 中心 = `center_lonlat`"反推：横向分数索引 `(width−1)/2 + 0.25`，纵向 `(height−1)/2`。
  那个 `0.25` 来自 odd-r 矩形棋盘横向实跨 `width + 0.5` 格（奇数行右移半格），**与行数无关**——axial 基线的中心索引要随 `r` 漂移，这正是两种布局的分水岭。
- **判别式**：`python tools/gis/hexgen.py --only-validate --out <包>` 的「ox/oy 中心对齐」项。PASS ⇒ 该包在 odd-r 基线上；FAIL ⇒ 还是 axial 采样，不能直接建房（格网会与在线底图和点击坐标错开半格以上）。
- **现状（2026-10-18 逐包实测）**：`data/gis/` 的 12 个数据包全部 PASS（`arnhem_rhine`、`demo_lake`、`demo_verify`、`demo_water`、`e2e_job_1792131101`、`gis_1792288947`、`p4c_wizard_1792287272`、`rhine_2p5km`、`rhine_5km`、`rhine_ruhr_2p5km`、`test_direct`、`ui_site_1792131378`）。
- 切换前的 axial 采样包整目录留在 `data/gis_axial_backup/`（11 个），这一项全部 FAIL（Δlon 0.11–0.50 度），只作对照样本，剧本与向导都不许再引用。

---

## 7. 战力影响链与待补系统

战斗裁决沿用 CF（Combat Factor）系数乘积范式，明确各项来源与是否已实现：

```
CF = BasePower(装备·兵种)
   × K_strength   (现存兵力比)
   × K_terrain    (地形防御)
   × K_fort       (工事)
   × K_supply     (补给系数 R = R0 · e^(-k·dist))
   × K_morale     (士气)
   × K_personnel  (人员水平：training/experience)   ← 需求新增
   × K_commander  (指挥链：指挥官能力 + 在半径内)     ← 需求新增
   × K_weather    (天气对攻方/守方分别生效)           ← 需求新增
   × K_air        (制空权：无掩蔽开阔地重罚)          ← 需求新增
   × K_counter    (兵种相克)
```

`BasePower(装备·兵种)` 这层在创建副本时**可逐项改**：向导第 4 步的"兵种属性覆盖"开放 `mobility`（机动）、`firepower`（火力）、`protection`（防护）、`recon`（侦察）、`load`（负荷）、`consumption`（每回合补给消耗）等装备性能，走 `overrides.unit_type_defs`。三条硬约束：

- **改了就要付钱**：补丁未显式标价时按"相对出厂值的偏离度"自动计价（火力 0.55 / 防护 0.45 / 机动 0.35 / 侦察 0.25 / 编制 0.30 加权，负荷与消耗是负担故为负权），倍率夹在 `0.35–2.6`；点数预算因此无法被"偷偷加强"绕过。实测：坦克火力 24→48、机动 11→14、消耗 3→1 ⇒ 26 → **45.9 点**；炮兵火力 30→42、机动 6→4 ⇒ 20 → 22.1 点。
- **只开放装备性能，不开放规则语义**：`cls / is_hq / supply_source / air_role` 一律拒绝（它们动的是通行类别、指挥链、补给源、空权这些机制），越界数值与未知兵种也在服务端拒绝，`force` 也不能绕过建副本这一关。
- **兵种属性是本局双方共用的基线**：同一份定义服务红蓝两侧，阵营差异只能用各自 OOB 的 `training/experience/morale/数量` 表达 —— 否则"红方坦克更强"这种需求会变成改坏全局定义。

覆盖后的定义写进副本的 `defs` 快照，**存档自包含**：载入续跑读到的仍是强化后的属性，不依赖出厂库版本。

需求点名但内核缺失的系统：

| 系统 | 状态 | 设计要求 |
| --- | --- | --- |
| 后勤（补给线） | 内核仅"多源 Dijkstra 跳数 + 指数衰减"静态场 | 需补弹药/油料/口粮三资源库存与消耗、补给队/枢纽容量、断链逐回合见底 |
| 状态（昼夜/天气/季节） | 内核无天气 | 天气进 `K_weather` 与侦察半径、机动代价；昼夜影响侦察/交战/空袭 |
| 指挥官 | 仅"距离增益"，`commandPoints` 零引用 | 指挥半径 + 指挥点消耗 + 技能特性 + 失联扣减（MP/回复/火力） |
| 制空权 | 全缺 | 空域控制分（战斗机 CAP 巡逻 + 防空 + 机场覆盖）→ 逐格空权状态 → 影响侦察与地面战；含防空压制 |
| 战力影响耦合 | 系数部分有 | 用 `战斗力耦合模型.html` 的曲线对齐，全系数外置可配（数据驱动，调参不改码） |

---

## 8. 回合与指令模型

- **阶段序**（内核现为 补给→状态→受理→战斗→损耗→回合终；新增侦察与空战）。C# `TurnResolver` 在①之前还有一个 **Phase 0 服务端指令校验**，遭遇战挂在机动之后（Phase 4b），所以代码里看到的是 0–7 共八段编号、语义上仍是下面这七步：

```
① 空中阶段（空战→制空权重算→对地空袭）
② 侦察与观测更新（生成/刷新 contacts，暴露降级）
③ 补给结算（补给线、三资源）
④ 机动（移动、构筑/撤除工事、渡河）
⑤ 战斗（含遭遇战；按稳定序）
⑥ 消耗与恢复（弹药/油料/口粮、疲劳、士气重建、溃退恢复）
⑦ 状态推进（天气演变、昼夜、回合+1、胜负判定）
```

- **双盲同时提交**：红蓝各自填令→ `ready` 屏障→双方都 ready 或导演部强制→引擎按稳定序（单位 id 序 + 明确跨方冲突仲裁）结算。**顺序不能由 id 命名决定** —— 内核现状是个可被利用的漏洞。
- **指令校验在服务端**：路径相邻性、MP 预算、射程与目标合法性、单位归属、每格堆叠规则。前端**只做几何预览**（射程圈 / 任务区圆 / 草案路径高亮，`hexfront-react/src/map/hex.ts:95` 的 `rangeHexes` + `SideView.tsx` 的 `preview`），不预测 MP 是否够用、也不本地判合法，最终以服务端 `CommandValidator` 的 `rejected` 回执为准。
- **AI 副官**：按迷雾可见情报生成合法指令，可代管缺席阵营或全 AI 自动对抗。
- **断线重连**：房间保留指令草案，回合结算只等 `ready` 屏障 + 导演部可强制超时提交。

---

## 9. 技术架构

**当前实装栈**（2026-10-18，P7 起为唯一交付形态；§9 末尾保留迁移前的选型理由作为历史依据）：

```
┌─ React 前端（Vite + TypeScript，hexfront-react/）
│   首页(三入口) / 大厅 / 副本创建向导 / 红蓝指挥终端 / 导演部 / 观战 / 回放 / 数据管理
│   原生 canvas 渲染（src/map/MapView.ts）+ 天地图 WMTS 底图 + 六角格叠加 + 迷雾/兵力块
│   REST：axios（src/services/api.ts，经 Vite 代理到 :5018）  会话：原生 WebSocket /ws（src/room/）
└──────────────────────────────────────────────────────────
┌─ 服务端（ASP.NET Core，HexFront.CSharp/）
│   HexFront.Api：GameController（REST）+ /ws 端点 + RoomService（房间/角色/会话/重连/就绪屏障）
│   HexFront.Core：FogService（观测累计·通视·暴露分级→裁剪 side_view，唯一真相边界）
│                  TurnResolver（七阶段权威裁决，SeededRNG 纯函数 step）
│                  MechanicsService · CommandValidator · AiService · ScenarioService · LibraryService
│                  HexGenService（六角格现场生成，替代 Python 依赖）
│   Persistence：StoreService（剧本库 / 副本 / JSONL 指令日志 / 周期快照 / 回放帧）
└──────────────────────────────────────────────────────────
```

| 项 | 现在的选择 | 说明 |
| --- | --- | --- |
| 引擎语言 | **C#（`HexFront.Core`）** | 与 WebAPI 同进程同类型系统，避免 Dart/JS 双份 DTO；算法口径仍按本节与 §6/§7 的条文，由 `tests/PARITY-CHECKS.md` 逐条锁住 |
| 服务端 | **ASP.NET Core WebAPI + 原生 `WebSocket` 端点 `/ws`** | 会话协议与迁移前逐字一致（见 §13.2），未采用 SignalR，理由见 §13.2 偏离说明 |
| 前端 | **React 19 + TypeScript + Vite（`hexfront-react/`）** | 唯一主前端；`web/`（无构建原生 ESM）已冻结为只读参考，#92 拍板不删，见 §13.6 |
| 地图渲染 | 原生 `<canvas>` + `requestAnimationFrame` | 未引入 Leaflet/Konva，投影与瓦片自绘，保持与 §6 的几何同源 |
| 存档格式 | JSONL 指令日志 + JSON 快照 | 与迁移前同格式：可读、可 diff、可重放对拍（`/api/load` 续跑） |
| 数据库 | **不用**（文件存储） | 原型规模用不上；避开依赖与运维，未来迁移成本低 |

**迁移前的选型（历史，2026-10-17 之前）**：引擎与服务端均为 Dart（`hexfront_core` + `dart:io` HttpServer/WebSocket，零三方依赖，环境无 pub 网络保障），前端为无框架构建的原生 ESM + Canvas（`web/js/map.js` 自实现瓦片投影、直连天地图 WMTS），并计划用 dart2js 把同一份内核编到浏览器做预览。该形态的产物仍在仓库内（`hexfront/`、`web/`）作为算法与协议的对照参考，不再是验收入口。

**内核必补清单（阻塞性，非优化）**：`toMap/fromMap/toJson/fromJson`（含 RNG 状态）→ 否则存档不可实现；兵种/地形枚举改**数据驱动**（现 `firstWhere` 遇未知直接抛）→ 否则自定义 OOB 不可实现；剥离 `BalanceData.load` 的 `dart:io`（→ `fromJson(String)`）否则无法编译进浏览器；移动路径相邻性校验；跨方行动顺序显式仲裁；`supplyRate==0` 时 CF 保底（不得为 0）；溃退恢复不可达分支修正；`Axial.fromKey` 负坐标修正；`log` 改结构化事件流并限长。

### 9.1 兵力列表的图标与配色（导演部「全局兵力」）

一屏要扫 30+ 单位，逐字段堆文字读不出来。约定：**行内只留图标与颜色能表达的 4 项，其余进悬停提示**。

| 位 | 内容 | 颜色语义 |
| --- | --- | --- |
| 行左边框 | 阵营 | 红方 `--red` / 蓝方 `--blue`（不再逐行写"红/蓝"标签） |
| 兵种色块 | 类型名前两字 | 按 `cls` 五色：徒步绿 `#7fb069`、轮式黄 `#d8a24a`、装甲青 `#56b6c2`、航空紫 `#b48ead`、固定设施灰 `#8b98a4`；未知 `cls` 走灰框 |
| 战力条 + 数值 | 前端重算的战力指数 | 火力 × 人员比 × 素质(训练/经验) × 士气 × 补给 × 弹药，**不含**地形/工事/天候/空权/相克/指挥链；分档以本局在场单位最大值为基准：≥60% 绿、≥30% 黄、其余灰，条宽同比例 |
| 士气圆点 + 数值 | `morale` | ≥70 绿 / ≥45 黄 / ≥25 橙 / <25 红 |
| 徽标（仅异常出现） | 亡 / 溃 / 断 / 弹 / 油 / 固N / 指 | 亡·溃·断供=红，缺弹·缺油=黄，工事=灰，指挥所=金 |

人员/编制、弹油粮存量、补给率、坐标、训练经验、指挥官姓名全部收进 `title`；阵营内按战力降序，被歼灭与受损单位自然沉底（`destroyed` 行整体降到 50% 不透明度）。战力口径与 §7 的 CF 链一致，只是不乘战场上下文项——列表要横向比"谁还能打"，而具体交战裁决仍以服务端 CF 为准。

红蓝终端的「己方部队」与观战「公开接触」仍是旧文本行，未统一；需要时按本节口径改造即可。


---

## 10. 交付里程碑

| 里程碑 | 内容 | 验收 | 状态 |
| --- | --- | --- | --- |
| **M1 引擎地基** | 序列化 + 数据驱动兵种/地形 + 指令校验 + 缺陷修复 + CLI 双跑对拍 | 当年用 `dart run bin/verify.dart` 全绿验收；**现在的门槛是 `dotnet test`（引擎 201 项）**，存档→载入→续跑与原局指纹一致 | ✅ 全绿（含"单位出生在可通行陆地"） |
| **M2 迷雾与观测** | 观测累计、LOS、暴露 E0–E3、`side_view` 裁剪 | 单测断言红方视图不含蓝方未暴露单位任何字段 | ✅ 含"未暴露不可开火"断言；档位颗粒与"要点地理公开/归属私有"由 `FogViewP7Tests` 锁住（§6.4） |
| **M3 多人房间** | 四角色接入、双盲屏障、导演部控局、重连 | 三浏览器真实对局可跑通 | ✅ 协议侧回归：xUnit 40 项 + `HexFront.CSharp/scripts/ws-smoke.mjs` 0–15 共 16 组全 PASS + 四终端浏览器实时对局通过 |
| **M4 剧本→副本→存档** | 剧本库、创建向导（OOB/兵种属性/天气/环境覆盖）、存档列表与载入 | 从剧本造副本、改 OOB、改兵种性能、开局、存档、再载入 | ✅ 五步向导浏览器内跑通（3 辆坦克覆盖 OOB、降雨、后勤关；兵种属性覆盖坦克 26→45.9 点并触发预算报红）|
| **M5 GIS 与在线底图** | `tools/gis` 生成器 + 真实底图叠加 + 零漂移对齐 + 向导内按需生成 | 真实栅格生成六角格，瓦图上目视对齐；范围/数据层/对边距（含推荐值）在浏览器里可选可跑 | ✅ 本地 GeoTIFF DEM + ESA WorldCover 按面积聚合；`--plan` 推荐对边距；服务端作业 API（`/api/gis/capabilities·plan·jobs`）；向导第 2 步现场生成 36×52 格包并建成副本（房间 L52TYZ，格数与包一致）；天地图真实瓦片像素级已验收（z9–z11，格网西缘贴合真实莱茵河） |
| **M6 战力耦合** | 天气/人员/指挥官/制空权/后勤三资源入 CF，系数外置 | 参数可调、战报可解释各系数贡献 | ✅ 系数逐项随战报下发 |
| M7 及后 | AI 副官、回放时间轴、观战档位、账号与权限、移动端适配、部署 | — | 🟡 AI 副官（双盲情报口径）、回放帧（200 帧上限）、观战档位（公开/全知）已在 C# 服务端并经浏览器取证；账号与权限仍是"房间码 + 令牌"（§11.2），移动端适配与部署未开工 |

---

## 11. 待拍板问题

1. **题材与合规**：高敏感近现代题材（抗战/解放/援朝/军阀）在既有文档被列为"法务/版号前置"。Web 多人对外是否走内部/教学先行？首发副本题材建议选低风险（一战/二战国际战场）。
2. **账号体系**：**已定——原型与 MVP 都不上登录**。角色会话就是"房间码 + 令牌"，观战可以是公开链接，房间私密性靠房间码不外传。React 把令牌存 `localStorage`（按房间码分键），整页刷新后仍能同 token 接管原会话。JWT/OAuth 与用户系统留 v2，见 §13.5-2。
3. **回合时限**：双人同时在线是否强制限时（竞技公平 vs 教学从容）。
4. **同步深度**：MVP 用"服务端权威 + 回合制下发"，实时帧同步不在范围。
5. **格距/规模基线**：首发副本选战斗级（1–2 km/格、数百格）还是战役级？影响渲染与节奏。
6. **前端是否引入构建**：**已定并落地**——上构建工具。React + Vite（`hexfront-react/`）成为唯一主前端，"双击直开"的无构建纯静态模式随 `web/` 一起冻结（#92 拍板：`web/` 保留为只读参考，不删除，理由与成本见 §13.6）。代价是审计要认构建产物、部署需要静态托管或把 `dist/` 挂到 Api 上；换来的是类型检查（`tsc -b` 成了门槛）与组件化。§11 早期"保持无构建"的倾向不再有效。
7. **同角色顶号策略**：**已定并落地**（C# `RoomService.JoinLocked`，与 Dart `room.dart:399-406` 一致）——阵营位（红/蓝）只允许"同 token 重连"或显式 `force=1` 接管，否则回 `type:error`"该角色位已被占用（加 force=1 可接管）"并关闭连接；导演部/观战不是对抗信息位，后来者直接接管、原终端收 `kicked`。教学与误操作场景是否要进一步改成"禁止顶号 + 导演部授权转让"仍待拍板。
8. **AI 代管与就绪屏障**：现实现为"代管方视为已就绪，人类就绪即自动结算"。是否要保留导演部逐回合确认（防止 AI 在人类未看战报时抢跑）？
   **实测口径（2026-10-18，房间 A9S3M3/LLMWE8，双 AI 代管 + 红方人类终端）**：屏障是**事件驱动**的——只有 `ready`、`director_cmd{action:ai}` 与 `resolve` 会触发一次自动结算，`AutoResolvePending` 互斥位在结算前就清零，结算完成后没有任何东西再排程。所以"双 AI 代管"不会自己连续推演：房间推进一回合后停在原地，等下一次人类 `ready` 或导演部强制结算。这既是"防止 AI 抢跑"的天然实现，也意味着**全自动跑局必须由导演部逐回合 `resolve`**（`HexFront.CSharp/scripts/ws-smoke.mjs` 的第 8d 项正是这么打的：15 回合、不到 0.3 秒（2026-10-18 两次跑分别 237ms / 177ms）、两局终局指纹一致）。
   同一批取证还量到一个平衡事实：**14 回合双 AI 对抗的歼灭数为 0**（3 个种子：红 7597→6615、蓝 4054→3450 人，约 13% 减员，`destroyed` 事件 0 条；交战回合数逐轮增加但单位人员从不清零）。终局靠的是要点/控制判定而不是歼灭，所以"歼灭"标签在纯 AI 局里几乎不出现——见 §11.12。
9. **重新开雾是否回收情报**：**已定并落地**。Dart 版原行为已在浏览器实测确认（2026-10-16，房间 YZEPCV）：关雾→再开雾后红方仍保留 11 项 E3 精确档案，而 HUD 显示"迷雾 开"——界面自相矛盾；根因是 `hexfront/lib/engine/fog.dart:121` 在全知时把档位写死为 3。C# 版按建议实现：`FogService.UpdateObservation` 的 `!fogOn` 分支**只放开观测场、不写 `tracks`**（明牌展示由 `SideView` 按 E3 实时合成），因此重新开雾不留永久记忆。该行为由 `ws-smoke.mjs` 第 10 组锁住（明牌 15/15 接触 → 开雾后回落到 1 条真实档案）。
10. **进程重启与房间**：**已定——房间保持内存态，不做落盘**（实时对抗语义下，"重启后回到半个回合"比"房间没了、用存档重开"更危险）。重启后用存档重建：`POST /api/saves/load` 走"快照 + 尾部重放"回到原指纹，剧本/覆盖/OOB 都从存档里来，不需要重新配一遍；`GET /api/frames` 在房间不存在时直接提示"重启后请用存档回放"。真要做热恢复得先定义"回合中掉线多久算弃局"，属于对外服务阶段的事，见 §13.5-3。
11. **离线判类的数据源（OSM 已排除）—— 已定并落地**：土地覆盖用 **ESA WorldCover 2020 10m**（AWS 开放桶，本机可达；注意 GDAL/schannel 对该桶 https 报"证书链不完整"，必须走 `http://`），高程用本地 GeoTIFF DEM，水系用"DEM 谷底带 + WorldCover 真实水体占比"，聚落用 WorldCover 建筑区占比。天地图仅用于底图显示，不用于派生判类（其服务条款限制二次建模）。
    **道路与铁路的真实来源已在数据包 `meta.layers` 里分档标注**（以 `data/gis/rhine_2p5km.json` 为准）：
    - 公路 `kind: natural-earth-world-roads+derived-corridors`——**干线来自 Natural Earth 10m `roads` 真实矢量**（RANK 1，231/493 条要素落进 601 格，命名干线 1/221/224/227/288/326/46/52），只有**渡河段**由派生走廊补齐（渡河点 20 处；另有 4 段沿河公路贴水不跨水，不记桥）。
    - 铁路 `kind: derived-route`——**无公开真实铁路矢量**，按首府/次级聚落 + 河谷低地 + 既有公路走廊派生，渡河只走既有桥位。
    即"派生"这个限定词只适用于铁路和公路的渡河补齐段，不要把整条路网说成地形派生走廊。
12. **纯 AI 局的歼灭率（待拍板）**：`rhine_corridor` 剧本 14 回合双 AI 对抗，3 个种子（20261018/90210/7）实测**歼灭数 0**——红 7597→6615、蓝 4054→3450 人（约 13% 减员），`destroyed` 事件 0 条，`rejected` 0 条，交战回合数逐轮增加但单位人员从不清零。终局靠要点/控制判定而不是歼灭，所以前端的"已消失"卡片在纯 AI 跑局里几乎不出现。
    这是**平衡参数问题，不是缺陷**：结算把损耗摊到人员池上，而一个连级单位要被打穿 100% 人员才会 `Personnel=0`（`GameState.Alive` 的定义），AI 又不会把火力集中到残兵上。取证时用 `overrides` 把蓝方 OOB 的 `personnel_ratio` 压到 0.004（每单位 2 人），B23/B26 在第 9 回合、B24/B25 在第 11 回合阵亡，才拿到"歼灭→gone→按 `staleTurns`=3 衰减消失"的完整链路（见 §11.9 的档案老化口径）。
    待拍板的是要不要改节奏：延长 `max_turns`、提高火力系数、或让 AI 优先补射残兵与撤退单位。建议放到引擎调参轮次，不在 P7 交付范围内动。

---

## 12. 原型运行方式

引擎、房间协议、REST 与**原生六角格生成**都是零第三方依赖的 C#（`HexFront.Core` + `HexFront.Api`）；前端是 Vite + React 19 + TypeScript，运行时依赖只有 axios / react-router-dom / lucide-react，地图绘制是原生 canvas（无 Leaflet/Konva，也没有 Ant Design）。只有"在向导里现场生成真实栅格六角格"这一条链路额外需要本机 **Python 3 + numpy（读 WorldCover 走 rasterio/GDAL）**——`GisService` 会探测 `python`/`py`/`python3`（可用 `HEXGEN_PYTHON` 指定），探测不到或缺 GDAL 时**自动降级为原生 C# 合成地形**并在作业日志与 `/api/gis/capabilities` 里明说（`engine` 从 `python-hexgen` 变成 `synthetic`），其余功能照常。

```bash
# 1) 后端（.NET SDK 10）
cd D:/Project/wargame/HexFront.CSharp
dotnet build HexFront.slnx
dotnet run --project src/HexFront.Api --urls http://127.0.0.1:5018
#   REST 前缀 /api，房间 WebSocket 是原生端点 ws://127.0.0.1:5018/ws?room={码}&role={red|blue|director|spectator}
#   端口占用者会顶掉 dotnet build —— 重新编译 Api 前先停掉 5018 上的进程

# 2) 前端（另开终端）
cd D:/Project/wargame/hexfront-react
npm install
npm run dev        # http://localhost:5173（5173 被占则顺延到 5174）
#   vite.config.ts 把 /api 与 /ws 代理到 5018，前端代码一律走同源相对路径，不写死后端端口
```

浏览器开 `http://localhost:5173/`（首页），四终端是**路径路由**（React Router v7 `BrowserRouter`，不再是 Dart 时代的 hash 路由），可多标签页并开：

| 终端 | 入口 | 说明 |
| --- | --- | --- |
| 首页 | `/` | **只有三个入口**：创建副本 → 向导、一键开局 → 默认剧本零覆盖直进导演部、存档副本 → 原地展开存档表载入导调。房间列表、剧本列表、按码加入都不在这里 |
| 大厅 | `/lobby` | 一键开局（默认剧本、无覆盖）、按房间码加入、活跃房间、可用剧本、存档载入（保持原样） |
| 创建副本 | `/new` | 5 步向导；未改动字段不发覆盖，服务端沿用剧本默认 |
| 红/蓝指挥 | `/room/{码}/red`·`/blue` | 仅本方全量 + 敌方已暴露接触；双盲指令 + 就绪 |
| 导演部 | `/room/{码}/director` | 全知态势、双方已受领指令、强制结算、导调、存档 |
| 观战 | `/room/{码}/spectator` | 公开情报层；导演部可切全知；可拉回放帧 |

顶部导航只留「首页 / 大厅」两项。**注意一条实收偏差**：`quickStart()` 在 React 版是**两份实现**（`pages/Home.tsx:16` 与 `pages/Lobby.tsx:61`）——行为一致（取 `scenarios[0]` 零覆盖建副本 → 跳导演部 → toast 反馈），差别只在 Home 现取 `loadMeta(true)`、Lobby 用列表里已有的 `scenarios`，以及 async/await 与 `.then` 两种写法。web 版当年是"大厅导出、首页复用"的一份，搬迁时没保住。这属于可合并的重复，不影响功能，建议随 #92 一并整理——同一个形状的问题在 `loadSave()` 上也有两份（`Home.tsx:47` 与 `Lobby.tsx:92`，都是 `api.loadSave(s.instance)` 后跳导演部），存档表也各自渲染了一份。

自检与回归（这几条就是交付基线，取证口径见 §13.6）：

```bash
cd D:/Project/wargame/HexFront.CSharp
dotnet test tests/HexFront.Tests           # 244 项全绿（引擎 201 + 协议 40 + 性能基准 3）；跑前先停 5018
node scripts/ws-smoke.mjs                  # 多人 0–15 共 16 组全 PASS（需 5018 在跑；脚本在这个工程下，不在根 scripts/）
cd D:/Project/wargame/hexfront-react
npx tsc -b                                 # 前端类型门槛（build 脚本就是 tsc -b && vite build）
cd D:/Project/wargame
node scripts/map-perf.mjs --role red       # 地图帧率取证（需 5018 + 5175 在跑），口径八条见 §13.6
cd D:/Project/wargame/tools/gis
python hexgen.py --only-validate           # 只校验现有 GIS 数据包（37 项自校验，含地物层不变式）
```

Dart 原型 `hexfront/bin/verify.dart`（61 项）与 `bin/smoke_ws.dart`（32 项）自 P7 起**只作对照参考，不再是验收入口**；C# 等价实现的逐项映射表在 `HexFront.CSharp/tests/PARITY-CHECKS.md`。

### 12.1 会话生命周期（多人可用的前提）

- **断开即释放**：终端关闭后该阵营位立刻腾出（指令草案挂在房间的阵营上，不随会话消失），重连后照常带回。
- **重连复用会话**：同 token 重连接管原会话对象，保证推送发得到；否则会出现"能发指令、收不到推送"的孤儿会话。React 端把 token 存在 `localStorage`（按房间码分键），所以整页刷新后仍认得自己人。
- **同角色顶号**：导演部/观战后来者直接接管，原终端收到 `kicked`、弹出提示并停止重连（避免两个标签页互相顶号死循环）。
- **他人占用**：阵营位（红/蓝）不带 `force=1` 加入已占用位时，服务端**先回一帧 `type:error`"该角色位已被占用（加 force=1 可接管）"再关闭连接，不是 HTTP 409**（WS 握手已经成功，状态码此时没有意义；`RoomService.JoinLocked`）。React 端识别到含"角色位"的错误帧立即停在 `kicked` 态不再重连，其他断线才走退避重连、超过 8 次放弃。大厅点已占用阵营位时先 `confirm` 确认接管（带 `force=1` 重连）。
- **房间码必须是已存在的副本**：`/ws?room=` 的码查不到或该副本没有 `GameState` 时直接 HTTP 404 拒绝握手——未知码静默建局会让打错码的人开出一局没人知道的仗，也和 `GET /api/room/{码}` 的 404 自相矛盾。
- **AI 代管即视为就绪**：对面托管时人类点就绪就自动结算，不再空等。屏障是事件驱动的，详见 §11.8。

浏览器实测（2026-10-16 于 Dart 版同页隐藏 iframe 双终端；2026-10-18 在 C# + React 上复验）：红方"R1 构筑工事"草案在整页刷新后仍带回（同 token 复用会话）；两个终端关闭后 `/api/rooms` 的 `roles` 全归零、`side_taken.red` 立即变 `false`，无会话泄漏；导演部关雾/开雾时红方 HUD"迷雾 开/关"字样跟随翻转。阵营位顶号 / 观战顶号 / 阵营位占用被拒 / 断开即释放这四条由 `HexFront.CSharp/scripts/ws-smoke.mjs` 的第 9、13、14、15 项锁住，未知房间码 404 是第 0 项。

数据落盘位置：`data/scenarios/`（剧本）、`data/gis/`（六角地形数据包）、`data/saves/<实例>/`（`meta.json` + `init.json` + `turns.jsonl` + `snapshot_t<N>.json`）。删目录即清空全部副本。

### 12.2 在线底图：天地图密钥

底图走天地图 WMTS（`_w` 球面墨卡托切片，与六角格同坐标系），需要一把 key：

1. 在天地图开放平台申请**浏览器端**密钥（控制台里选"浏览器端"，本地调试先不要锁 IP/域名白名单，否则 127.0.0.1 会被拒）。
2. 写入 `data/tianditu.json`：`{"tk": "你的密钥"}`（刷新页面即生效，无需重启服务端；也可用环境变量 `TIANDITU_TK`，优先级高于文件）。**这把 key 属于访问凭证，不能进版本库**：仓库根 `.gitignore` 需含 `data/tianditu.json`，仓库里保留 `data/tianditu.example.json` 占位即可；密钥一旦出现在提交历史里，只能到天地图控制台重置，删文件并不能收回历史。
3. 服务端把它放进 `/api/scenarios` 响应的 `tiles.tk`（`GameController` 从 `RoomServiceStaticPaths.DataDir` 读文件，别踩这个坑：改工作目录会让它去找 `<repo根>/tianditu.json` 而不是 `data/tianditu.json`，密钥静默失效）。React 端 `components/HexMap.tsx` 从场景元数据取 `tiles.tk` 后调 `MapView.setTk()`，密钥为空时底图选择器直接禁用。

无密钥时的表现是**可控降级**，不是坏：底图选择器禁用并在 `title` 里说明原因，画布左下写着"在线底图未配置…"，六角格照常按 GIS 地形上色、对局功能完全不受影响。密钥错/过期时请求返回 HTTP 418，`onerror` 计数到 6 次即停发瓦片请求，不会刷屏打爆配额。

底图可在矢量（`vec`+`cva`）/影像（`img`+`cia`）/地形（`ter`+`cta`）间切换，每个模式都是"底图+中文注记"两层叠加（天地图把注记拆成独立图层，只拉底图就没有地名）。署名 `© 天地图 · 国家基础地理信息中心` 画在画布右下角；正式发布还需按测绘要求叠加**审图号**，号码随图层与年份变化，此处不预填，上线前补。

一个要记住的取舍：瓦片请求**不带** `crossOrigin`（天地图不保证回 CORS 头，带 `anonymous` 会整批加载失败）。代价是叠了在线瓦片后画布被"污染"，`getImageData`/`toDataURL` 会抛 SecurityError。当前前端从不回读像素（拾取走坐标反算），所以无影响；**若将来要做"从画布取色/导出地图 PNG"，必须改成服务端代理瓦片**，不能在浏览器直连。

验证提醒：开发时若 Qoder 的 Browser 面板处于隐藏状态，后台标签页的 `requestAnimationFrame` 会被冻结，canvas 完全不绘制（宽高停在默认 300×150、瓦片请求为 0），看起来像"底图坏了"。做像素级验收必须先把面板打开；逻辑层可直接调用 `MapView` 的状态方法单测（退避/收割/放弃三态已这样验过）。

---

## 13. C# Core WebAPI 重构（2026-10-17 起 · 规划与实收）

**本节状态（2026-10-18 更新）**：三个阶段（13.1 生成 / 13.2 后端 / 13.3 React）都已交付并确认，P0–P6 逐阶段验收完，P7 收尾进行中（#91 文档一致性已交、#92 `web/` 处置已定为"保留只读参考 + 移植遗漏对照清单收口"、#93 性能基线三条数字已交，见 §13.1/13.2/13.3 与 §13.6）。规划期的表述保留原文，**每处与实收不同都就地标注了"实收/偏离"**——照这份文档重建 C# 侧时会踩到规划与实现不一致的地方，那些标注就是补丁。

规划时的起点：Dart 原型（`hexfront/`）已端到端跑通 GIS 六角格生成 → 引擎推演 → WebSocket 多人房间 → Canvas 渲染，共 61 项引擎自检 + 32 项多人冒烟全部通过。这 93 项断言现已由 C# 侧等价实现接手（`dotnet test` 244 项 + `ws-smoke.mjs` 16 项，映射表 `HexFront.CSharp/tests/PARITY-CHECKS.md`），Dart 原型退为对照参考。

### 13.1 阶段一：六角格生成 C# Core WebAPI

**目标**：把 `tools/gis/hexgen.py` + `rasters.py` 的完整能力重写为 ASP.NET Core WebAPI，提供 RESTful 接口供后续 React 前端调用。

**原计划的输入资产与实收的差别（交付后回填）**：规划时假设用 C# 地理库替换整条 Python 栅格/矢量链路，实收是**两条腿**：

| Python 模块 | 原计划 C# 对应 | 实收 |
|---|---|---|
| `hexgen.py` (2300+ lines) | `HexGenService` + NetTopologySuite/ProjNet | `Services/HexGenService.cs` **只实现原生合成地形**（无 Python 时的降级路径，确定性噪声代理）；真实栅格仍由 `Services/GisService.cs` 起子进程调 `tools/gis/hexgen.py` |
| `rasters.py`（DEM/LC 取样） | `RasterSampler` + GDAL 绑定 | 未做 C# 绑定：读 WorldCover 走 rasterio/GDAL（Python 侧），`/api/gis/capabilities` 探测后如实上报 |
| `roads_by_hex`（pyogrio OpenFileGDB） | `RoadVectorReader` | 同上，留在 Python 侧；C# 只消费产出的数据包 |
| `build_features`（rail/power/tunnel/plant） | `FeatureBuilder` + QuickGraph | 同上；`GisPlanService` 只做对边距推荐阶梯 |
| `validate`（37 项自校验） | `HexPackageValidator` | ✅ `Validation/HexPackageValidator.cs` 完整移植，落盘前跑、不过就不产出（`/api/gis/validate` 与 `/api/validate` 都走它） |
| 投影换算 | ProjNet | `Geometry/Mercator.cs` 手写球面墨卡托，与瓦片同源 |

> **遗留**：`HexFront.Core.csproj` 里 `NetTopologySuite` 2.6.0 与 `ProjNET` 2.1.0 两个包引用**代码里一处都没用到**（是这张原计划表的残留）。它们不影响构建与运行，但会误导下一个读代码的人以为有 C# 地理链路。**#92 拍板：不删，只登记为已知残留**（删依赖属于依赖变更，收益只是"看着干净"；真用它替换 Python 子进程则是另一期工程）。同批登记进 §13.6 的还有 `zustand` 与 React `App.css` 里 58 条从未被 JSX 命中的死样式。

**API 设计（实收，注意 `plan`/`jobs` 是 GET+POST 双动词，对齐 web 客户端既有调用）**：

```
GET/POST /api/gis/capabilities        → 报告本机 DEM/LC/roads/airports/vector 可用性（真探测，不是硬编码）
GET/POST /api/gis/plan?bbox=&cell_km= → 推荐对边距 + 格网方案（含 --plan 阶梯）
POST     /api/gis/jobs                → 异步生成作业，返回 job_id
GET      /api/gis/jobs                → 作业列表
GET      /api/gis/jobs/{id}           → 轮询进度与日志
POST     /api/gis/generate            → 同步生成入口（保留旧名）
GET      /api/gis/packages            → 列出 data/gis/ 下所有可用数据包及其 meta
GET      /api/gis/packages/{name}/meta → 单个数据包的完整元信息（含地物 stats 与 layers 出处）
POST     /api/gis/validate            → 指定已有包名，返回 37 项校验结果
```

响应字段一律 snake_case（`JsonNamingPolicy.SnakeCaseLower`），与 Dart/web 线上格式一致。

**数据格式兼容**：生成的 JSON 结构与现有 Python 版完全一致（`hexes[]`, `meta{width,height,radius_m,cell_ground_m,mercator_scale,layers,stats,...}`），前端无需改动即可消费；`data/gis/` 现有 11 个包已按 odd-r 布局重采样重生成（axial 旧版备份在 `data/gis_axial_backup`）。

**验收标准**：

- [x] 37 项自校验全部通过（含地物不变式）——`python hexgen.py --only-validate` 与 `HexPackageValidator` 双侧一致
- [x] Swagger UI 可交互测试所有端点（`Swashbuckle.AspNetCore.SwaggerUI`，开发环境挂 `/swagger`）
- [x] 向导第 2 步在浏览器里现场生成 36×52 格包并建成副本（房间 L52TYZ，格数与包一致，M5 取证）
- [ ] `rhine_ruhr_2p5km.json` 重新生成后与原版 diff 为零——**这条按原写法不成立**：C# 没有复刻 Python 的栅格取样，重生成必然来自同一个 Python 脚本；真正锁住的是"同种子同参数 ⇒ 同包"，由 37 项自校验 + odd-r 重生成实测覆盖
- [x] 性能：2.5km 格距 1645 格生成 < 30s（本机 i7 + SSD）——**#93 实测：`HexGenService.Generate` 生成 36×48 = 1728 格用时 94ms（0.0544 ms/格），是 30s 门槛的 0.3%**；回归项 `tests/HexFront.Tests/Gis/HexGenBenchmarkTests.Generate_Doc_1645_Cell_Board_Under_30_Seconds`（`Trait=Benchmark`）。**格数为什么是 1728 而不是 1645**：这条基准把出厂包自己的 `meta.bbox` 回喂给生成器，而 `meta.bbox` 记的是"结果格心范围"（比请求范围宽半行半列），`GisPlanService.GridSize` 与 `hexgen.py grid_from_bbox` 用的是同一个 `round(跨度/格距)+1` 公式，两端都必然多出一行一列——**C#/Python 一致，不是对等回归**；断言因此锁成 `BeInRange(1645, 1800)`，口径同时写进测试注释，防止下一个人以为格数漂了

### 13.2 阶段二：推演后端 C# Core WebAPI

**目标**：把 `hexfront/lib/engine/*` (defs, state, turn, mechanics, fog, commands, library, scenario) 重写为 ASP.NET Core WebAPI + 实时会话端点，替代现有 Dart server。

**核心移植清单**：

| Dart 模块 | C# 对应 | 复杂度 |
|---|---|---|
| `defs.dart` (Cell, UnitTypeDef, TerrainDef, WeatherDef, Rules) | `Models/Defs.cs` | ★☆☆ 纯数据类 |
| `state.dart` (Unit, GameState, VictoryCond, InstanceBuilder) | `Models/GameState.cs` | ★★☆ 序列化 + 深拷贝 |
| `library.dart` (STANDARD_TERRAIN/UNIT_TYPES/WEATHER_JSON) | `Services/LibraryService.cs` | ★☆☆ 常量表 |
| `scenario.dart` (deepMerge, validate) | `Services/ScenarioService.cs` | ★★☆ 递归合并 |
| `commands.dart` (Command, validateOrders) | `Services/CommandValidator.cs` | ★★★ 相邻性/视野/弹药校验 |
| `turn.dart` (_movementPhase, _combatPhase, resolveTurn) | `Services/TurnResolver.cs` | ★★★ 七阶段裁决（代码里 Phase 0 校验 + ①–⑦，遭遇战是 4b） |
| `mechanics.dart` (supplyDistances, computeSupply, powerFactor, nodeHolders, objectiveNodes) | `Services/MechanicsService.cs` | ★★★ BFS + 电网算法 |
| `fog.dart` (updateObservation, sideView, directorView, spectatorView) | `Services/FogService.cs` | ★★★ 观测累计 + 视图裁剪 |
| `hex.dart` (Hex, cube rounding, neighbors) | `Geometry/Hex.cs`·`HexGrid.cs` | ★★☆ 轴向↔立方体转换（odd-r 布局） |
| `ai.dart` (aiOrders) | `Services/AiService.cs` | ★★☆ 启发式决策，只吃迷雾内情报 |
| `server/hexgen.dart` (GisJobSpec, GisService) | `Controllers/GisController.cs` + `Services/GisService.cs`(Api) | ★★☆ 作业队列；真实栅格仍调 `tools/gis/hexgen.py`，无 Python 时降级 |
| `server/store.dart` (Store, load/save) | `Services/StoreService.cs` | ★★☆ JSON 文件读写 |

**C# 侧新增（Dart 没有对应模块，按本文档重写时补的）**：`Services/CombatChain.cs`（11 因子战力链，doc §5）、`Services/SeededRng.cs`（确定性随机源，doc §8「同种子同指令同指纹」的前提）、`Services/CommandWire.cs`（指令 JSON 线上格式双向）、`Services/UnitTypeDefService.cs`（兵种属性覆盖与计价）、`Services/GisPlanService.cs`（对边距推荐阶梯）、`Services/HexGenService.cs`（原生合成地形）、`Validation/HexPackageValidator.cs`（37 项数据包自校验）、`Geometry/Mercator.cs`（与瓦片同源投影）。

**会话传输设计（实装）**：原生 WebSocket 端点 `/ws?room=&role=&token=&name=&force=1`（`Program.cs` 用 `UseWebSockets` + `app.Map("/ws")`，心跳 `KeepAliveInterval=30s`）；上行 `ping|orders|clear_orders|ready|resolve|save|frames|state|director_cmd`（`director_cmd` 的动作字段是 `action`，取值 `weather|fog|spectator_omniscient|ai|inject_intel|jump_turn`），下行 `welcome|state|events|saved|frames|error|kicked|pong`（消息语义见 §12.1）。房间码必须是已存在的副本，未知码握手直接 404。

> **偏离说明（2026-10-18 拍板）**：本节原计划用 ASP.NET Core SignalR Hub 承载会话。实装与全量验证走的是原生 `/ws` JSON 协议——它与既有 web/React 客户端的线上格式逐字一致，且已由 `HexFront.CSharp/scripts/ws-smoke.mjs`（编号 0–15 共 16 组断言）与四终端浏览器 E2E 验证。曾按本节写过的 `RoomHub`（自带一套与 `RoomService` 平行的房间字典，与 `/ws` 语义不互通、零引用）已删除。若将来要接 SignalR（浏览器自动重连、降级、流式回包），应做成 `RoomService` 之上的薄适配器，而不是第二套房间真相。

**验收标准**：

- [x] 现有 `bin/verify.dart` 61 项用例全部在 C# 端通过（xUnit 等价实现）——引擎侧 201 项，逐项映射见 `HexFront.CSharp/tests/PARITY-CHECKS.md`；Dart 原型自 P7 起只作对照，不再是验收入口
- [x] 现有 `bin/smoke_ws.dart` 32 项多人冒烟全部通过（会话传输实装为原生 `/ws`，见上方偏离说明）——协议侧 xUnit 40 项 + `ws-smoke.mjs` 16 组全 PASS
- [x] 同种子同指令 → 同指纹（确定性对拍）：`dotnet test` 的 P1 用例 + `ws-smoke` 第 8 项（存档往返指纹一致）
- [x] 存档往返指纹一致（JSON 序列化兼容）：`ws-smoke` 第 7/7b/8/8b 项覆盖"快照 + 尾部重放"两条路径
- [x] 性能：14 回合全 AI 对抗 < 5s（与 Dart 版持平）——**#93 实测：建局 55ms（36 单位 / 520 格），14 回合补令 74ms + 结算 115ms、整段循环 202ms（均 14.4ms/回合，余量是 GC 与计时本身），5s 预算用掉 4%**；回归项 `tests/HexFront.Tests/Performance/EnginePerfBaselineTests.cs` 两条（`Trait=Benchmark`）。断言不止耗时：还要求"14 回合真跑完（`perTurn.Count==14`）+ 有交战（71 场）+ 零被拒令 + 两遍同指纹"，否则空转的 14 回合也能"很快"。种子 731001 与 `ws-smoke` 第 8d 项同一个剧本同一个种子，两侧终局指纹都是 `5d4bd5b8`（胜方 blue），所以进程内基准与整链路冒烟量的是同一局，两条数字可以对着看

### 13.3 阶段三：React 前端

**目标**：把 `web/js/*` (map.js, views/*.js, api.js, ws.js, router.js, util.js, hex.js) 重写为 React + TypeScript SPA，Canvas 渲染保留但封装为组件。

**技术选型（左列是 2026-10-17 的建议，右列是实收与版本）**：

| 层级 | 原建议 | 实收 |
|---|---|---|
| 框架 | React 18 + Vite | React **19.2.8** + Vite **8.3.0**（`@vitejs/plugin-react` 6.1.1，没装 SWC 插件） |
| 语言 | TypeScript 5.x | TypeScript **~6.0.2**；门槛是 `npx tsc -b`（`--noEmit` 会漏掉 project references，不算验过） |
| 状态管理 | Zustand | **没用 Zustand**：房间状态由 `room/RoomSocket.tsx` 的 hook + 各页 `useState/useRef` 持有，REST 走 `services/api.ts`。zustand 5.0.15 仍在 `package.json` 里但零引用（#92 拍板：不删，登记为已知残留，见 §13.6）|
| 路由 | React Router v6 + hash 路由 | Router **7.18.4** `BrowserRouter` **路径路由**：`/`、`/lobby`、`/new`、`/room/:code/:role`（不再是 `#/`，见 §12） |
| UI 组件 | Ant Design 5.x | **没用 AntD**：沿用 web 的深色终端风 CSS（`src/App.css` + `index.css`），图标用 `lucide-react` 1.58.0 |
| Canvas | react-konva 或原生 canvas ref | **原生 canvas ref**（`src/map/MapView.ts` 1130 行，从 `web/js/map.js` 直移植：DPR / ResizeObserver / 拖拽缩放 / fit / 瓦片退避闸门）；Konva 零引用 |
| REST | — | `axios` 1.20.0，`services/api.ts` 统一 `/api` 前缀，开发期由 Vite 代理到 5018 |
| 会话传输 | 原生 WebSocket（`/ws`） | 与 Dart/web 线上格式逐字一致，不引入 SignalR 客户端；理由见 §13.2 偏离说明。实装 `src/room/RoomSocket.tsx`（welcome/state/events/saved/frames/error/kicked/pong + token 持久化 + 退避重连） |
| 构建 | Vite + SWC | Vite 默认（esbuild + plugin-react），`npm run build` = `tsc -b && vite build`；lint 用 `oxlint` |

**组件拆分（实收目录，`hexfront-react/src/`）**：

```
App.tsx / main.tsx          # BrowserRouter + / 、/lobby、/new、/room/:code/:role 四条路由
pages/
  Home.tsx                  # 首页三入口（创建副本 / 一键开局 / 存档副本）
  Lobby.tsx                 # 大厅（一键开局 / 按码加入 / 存档载入；quickStart 与 loadSave 在 Home 各有一份，见 §12）
  Wizard.tsx                # 5 步创建向导（GIS 现场生成作业轮询、/api/validate 实时回显、预算报红）
  RoomPage.tsx              # 按 role 分支的容器
  SideView.tsx              # 红/蓝：己方全量 + 接触情报 + 草案 + 就绪 + 射程圈预览
  DirectorView.tsx          # 导演部：全知态势 + 双方已受领指令 + 强制结算 + 导调 + 战报因子链
  SpectatorView.tsx         # 观战：公开情报层 + 回放帧
components/
  HexMap.tsx                # MapView 的 React 外壳（tk 注入、底图切换、图例、禁用态提示）
  Layout.tsx                # 顶栏 + 三列终端布局
  Toast.tsx                 # 提示层（配 services/sound.ts 的提示音）
map/
  MapView.ts                # canvas 渲染内核（相机 / 瓦片退避闸门 / 地物连线 / 兵力块 / HUD）
  hex.ts                    # odd-r 几何、hexRing / rangeHexes 稳定序
room/
  RoomSocket.tsx            # /ws 连接、退避重连、顶号与占用的停连判定、token 持久化
  protocol.ts               # REST/WS 契约类型（snake_case 字段）
  useUnitFull.ts            # 单位详情按需取全量
services/
  api.ts                    # axios 封装全部 /api 端点（统一 /api 前缀，同源相对路径）
  sound.ts                  # 指令 / 交战 / 顶号提示音
```

原建议里的 `routes/`、`components/MapCanvas.tsx`、`UnitPanel/DraftBar/ContactList/DirectorControls/EventLog/HudOverlay`、`services/ws.ts`、`services/gameState.ts`、`geometry/hex.ts`、`styles/dark-theme.css` 都没有按这个名字出现：面板与日志直接写在三个视图页里（它们各自只有一处使用），画布外壳叫 `components/HexMap.tsx`、内核叫 `map/MapView.ts`，几何在 `map/hex.ts`，样式在 `App.css` + `index.css`。**没有 Zustand store**。

**验收标准**：

- [x] 创建副本向导 5 步流程完整可用（含真实数据包选择 + 地物层展示）——浏览器实测：3 辆坦克覆盖 OOB、降雨、后勤关、兵种属性覆盖（坦克 26→45.9 点）触发预算报红；第 2 步现场生成 36×52 格包并建成副本（房间 L52TYZ，格数与包一致）
- [x] 导演部视图：全局兵力 + 地图 + 导调控制 + 双方指令 + 事件日志 + 战报逐因子贡献
- [x] 红/蓝视图：己方单位 + 接触目标（E1 只给兵力档、E2 给兵种、E3 才给精确兵员；阵亡单位保留 `gone`"已消失"卡片并按 `staleTurns`=3 衰减）+ 指令草案 + 迷雾隔离
- [x] 观战视图：公开情报层 + 无归属披露（`node_holders` 不下发）+ 导演部可切全知
- [x] 地图渲染：天地图瓦片 + 六角格 + 道路/河流/铁路/电网/机场/电厂/变电站/车站/隧道 + 悬停格信息 + 图例（像素级已验收 z9–z11，格网西缘贴合真实莱茵河）
- [x] 缩放/拖拽/双击放大流畅（60fps）——**#93 实测（`scripts/map-perf.mjs`，无头 Chrome + CDP 派发真受信任事件，红方 + 导演部两条路径各 9 个场景）：满载可持续帧率 235.8–239.3fps，瓦片关 239.33 / 瓦片开 239.17（红方）、235.84 / 239.33（导演部）；单帧 `MapView.draw` p50 0.6–2.2ms、所有交互行 p95 ≤7.8ms、max ≤10.1ms，最差也只吃掉 16.7ms 预算的 47%**。测量口径的五个坑（无头不做 vsync、伪瓦片、浮层吃事件等）见 §13.6「地图帧率取证口径」。**同一次取证翻出 1 条真实缺陷**：全场第一次交互的那一帧 277–295ms、阻塞 227–243ms，LoAF 归属 [hexfront-react/src/services/sound.ts:26](file:///D:/Project/wargame/hexfront-react/src/services/sound.ts#L26) 的 `primeAudio`——`new AudioContext()` 同步建在第一个 `pointerdown` 监听里。这是**一次性**开销（同页第二次交互起 0 长帧，瓦片那条 pass 也 0 长帧），不影响 60fps 结论，但确实是玩家进图后第一次点击的卡顿，**处置待拍板，未随本任务改代码**。
- [x] 断线重连恢复会话 + 顶号提示（token 存 localStorage 按房间码分键；`kicked` 停在提示态不再重连；阵营位占用收 `type:error` 立即停连，其他断线退避重连超 8 次放弃）
- [x] 响应式布局（最小 1024×768 可用）；面板滚动条回归已修（见 §13.6 的 P6-d 遗留缺陷一条）

### 13.4 迁移策略与风险控制

**并行开发**：三个阶段不必串行等待，可在 Dart 原型稳定运行的前提下并行推进（下面这份周次表是 2026-10-17 的排期设想，**保留仅作历史记录**；实际按 §10 的里程碑 P0–P7 逐阶段交付、每阶段确认后再开下一阶段，与"三阶段并行"的假设不同）：

```
Week 1-2:  阶段一 API 骨架 + 单元测试
Week 3-4:  阶段一完整 + 阶段二 Defs/State/Library
Week 5-6:  阶段二 Turn/Mechanics/Fog + xUnit 验证
Week 7-8:  阶段二 /ws 会话端点 + smoke 测试
Week 9-10: 阶段三 React 脚手架 + MapCanvas
Week 11-12: 阶段三完整视图 + 联调
Week 13:   集成测试 + 性能对比 + 文档更新
```

**风险与缓解**：

| 风险 | 概率 | 影响 | 缓解措施（括号内为实收结果） |
|---|---|---|---|
| GDAL C# 绑定安装复杂 | 高 | 中 | 备选 RasterSharp 纯 C# 栅格读取；或保留 Python subprocess 调用 hexgen.py（**实收：保留 Python 子进程**，无 Python 时降级为 `HexGenService` 原生合成地形并在 capabilities/作业日志里明说） |
| 确定性浮点差异导致指纹不一致 | 中 | 高 | 使用 decimal 代替 double；或允许 ±1e-9 容差比对（**实收：都不必**——`SeededRng` 固定运算顺序，同种子同指令存档往返指纹逐字相等，由 `ws-smoke` 第 7b/8/8b 项锁住） |
| 双传输并存导致两套房间真相（SignalR Hub 与 /ws） | 已发生 | 中 | 只保留 `/ws`；`RoomHub` 已删除，会话真相收敛到 `RoomService`（§13.2） |
| React Canvas 性能不如原生 | 中 | 中 | 优先用原生 canvas ref + requestAnimationFrame；Konva 仅用于 UI 覆盖层（**实收：全原生 canvas，Konva 零引用**；#93 满载实测 235.8–239.3fps、`draw` p95 1.2ms，见 §13.3 与 §13.6 的取证口径） |
| 迁移期间需求变更 | 高 | 高 | Dart 原型作为"活文档"持续维护；C# 每完成一个模块即同步验证（**实收：P7 起 Dart 版只做对照参考，验收入口换成 xUnit + ws-smoke + 浏览器 E2E**） |

**过渡期运行模式（实收端口）**：

```
┌─────────────────────┐   REST /api + WS /ws   ┌──────────────────────┐
│  React SPA (Vite)   │ ◄────────────────────►  │  HexFront.Api        │
│  localhost:5173     │   （vite 代理到 5018）   │  localhost:5018      │
└─────────────────────┘                          └────────┬───────────┘
                                                          │
                                                 ┌────────▼────────┐
                                                 │ Python hexgen   │ ← 仅"真实栅格生成"这一条链路
                                                 │ (subprocess)    │   缺 Python/GDAL 时自动降级
                                                 └─────────────────┘
```

Dart 原型（hexfront/）在迁移期间保持可运行状态，作为参考实现。**P7 起它不再是验收入口**：C# 侧已有等价 xUnit 用例集（映射表 `HexFront.CSharp/tests/PARITY-CHECKS.md`），后续以 `dotnet test` + `ws-smoke.mjs` + 浏览器 E2E 为准；"Dart 版能通过 → C# 版也能通过"这条准则已完成使命，保留为回归期的历史依据。

### 13.5 待拍板事项（§11 遗留问题）

以下四条是 C# 重构启动前列出的待拍板项。四条都已定，其中两条与 §11 的正式条目重复（重复的以 §11 为准，这里只留结论）：

1. **重新开雾是否回收情报**（正式条目见 **§11.9**）：当初的假设是"情报一旦获得就永久保留，重开雾不回收"，并推荐保持。**实收与推荐相反**：Dart 版这个行为是 bug（`fog.dart:121` 在全知时把档位写死为 3，导致明牌期间凭空长出 E3 精确档案），HUD 还显示"迷雾 开"，自相矛盾。C# 版改成关雾期间**不写 `tracks`**（明牌展示由 `SideView` 按 E3 实时合成），所以重新开雾不留永久记忆；由 `ws-smoke.mjs` 第 10 项锁住。

2. **账号体系**（正式条目见 **§11.2**）：**已定——MVP 不上 JWT/OAuth**。角色会话仍是"房间码 + 令牌"，React 把令牌存 `localStorage`（按房间码分键）以支持同 token 重连接管原会话。用户系统留 v2（M7 之后）。

3. **进程重启丢房间**（正式条目见 **§11.10**）：**已定并按推荐落地**——房间保持内存态（符合实时对抗语义），不做房间落盘；重启后用存档重建：`POST /api/saves/load` 走"快照 + 尾部重放"回到原指纹，剧本/覆盖/OOB 都从存档里来，不用重新配一遍。`GET /api/frames` 在房间不存在时提示"重启后请用存档回放"。

4. **Git 大文件治理**：`.git` 目前 **105MB**（102MB `roads_pkg` + 24MB `global_water.gpkg` 已在历史里）。**已定：不 rewrite history**（成本过高，且历史里的密钥只能靠控制台重置解决）。本轮只新增 `.gitignore` 规则挡住编译产物与 `saves/`；**`.gitattributes`（`*.gdb/** binary` + `*.gpkg binary`）和 Git LFS 仍未做**，等真要对外分发数据时再开一条任务。

> 顺带一条清理项：仓库根有 `_probe/`（4 张 10 月 16 日的浏览器探针截图 + `probe_rasters.py` + `server.log`）和 `_probe_plan.txt`，已被提交进版本库。它们是排查过程的临时产物，不属于交付内容，建议随 #92 一起处置。

### 13.6 交付基线与取证口径（P7 起生效）

这一节回答两个问题：**"验收看哪几条命令的什么数字"**，以及**"截图证据怎么复现"**。前面 §12/§13.1/§13.2/§13.3 的验收项全部指向这里，不再各写一份。

**五条门槛命令（本机 i7 + SSD，.NET SDK 10.0.113 / Node 24）**：

| 命令 | 当前基线 | 锁的是什么 |
|---|---|---|
| `dotnet build HexFront.slnx` | 0 warning / 0 error | 后端可编译；注意 5018 上的进程会占用输出，重编前先停 API |
| `dotnet test tests/HexFront.Tests` | **244 通过 / 0 失败**（引擎 201 + 协议 40 + 性能基准 3；`--filter Category=Benchmark` 单跑 6 条） | 引擎语义 + 会话协议 + 两条性能线；`tests/PARITY-CHECKS.md` 是 Dart `verify.dart` 61 项到 xUnit 的逐项映射表。**跑之前必须先停 5018 上的 API**（测试工程引用 Api，Windows 文件锁会让 `dotnet build` 失败） |
| `node HexFront.CSharp/scripts/ws-smoke.mjs` | **编号 0–15 共 16 组 ALL PASS**（需 5018 在跑） | 真实 WS 往返：接入/占用/顶号/双盲/拒令/导调/存档往返与尾部重放/回放帧 200 上限/迷雾回收/AI 就绪屏障/全 AI 终局指纹（8d：15 回合 159ms、指纹 `5d4bd5b8`）。**注意路径在 `HexFront.CSharp/scripts/` 下，不在根 `scripts/`** |
| `npx tsc -b`（`hexfront-react/`） | exit 0 | 前端类型门槛。**`--noEmit` 不算**：项目用 project references，只有 `-b` 会全量检查。配套 `npx oxlint src` 基线 7 warning / 0 error |
| `node scripts/map-perf.mjs --role red`（`--role director` 同跑） | **满载 235.8–239.3fps，瓦片开/关两路都过**；交互行 `draw` p95 ≤7.8ms；长帧 1 条（`sound.ts` 首点一次性开销，见 §13.3） | 地图 60fps（doc §13.3 那条验收线）。需要 5018 + 5175 在跑；不给 `--room` 时自己 `POST /api/instances` 建副本 |

另有 `python tools/gis/hexgen.py --only-validate`（37 项数据包自校验，含地物层不变式）——只在动 GIS 生成链路时要求。

**#93 已把原先显式挂着两项补齐**（2026-10-18）：14 回合全 AI 对抗 202ms（§13.2）、地图满载 235.8–239.3fps（§13.3），两条都进了 `dotnet test` / 门槛命令，不再靠口头声称。文档里剩下的 `[ ]` 只有 §13.1 那条"重生成 diff 为零"——它在 #93 之前就被判定"按原写法不成立"，理由就地写着。

**地图帧率取证口径（`scripts/map-perf.mjs`，重跑就是这一节那行命令，产物落 `.tmp/map-perf-<role>.json` + 同名 PNG）**：

1. **无头 Chrome 不做 vsync 定帧**：空载 1.2s 能数到 319–322 帧、间隔 p50 4.2ms，交互期同样 4.2ms。所以"rAF 间隔"只用来识别测量环境，**不能当帧率读**——这是本机第一轮取证最大的一个坑。
2. **单帧重绘耗时**：页内包住 `MapView.draw` 记每帧实际毫秒，门槛 p95 ≤16.7ms、max ≤50ms。这条与是否锁帧无关，是"60fps 有没有预算"的直接证据。实测所有交互行 p95 ≤7.8ms、max ≤10.1ms。
3. **长帧**：`PerformanceObserver(long-animation-frame)`（Chrome 123+）记 ≥50ms 的帧并带脚本归属（源文件:行:函数:invoke 类型）。启动期（vite 转译 + 首包 state）的长帧会**迟到落进缓冲**，所以正式测量前先静置 2s 并清一次缓冲，否则首行场景替整个页面加载背锅。
4. **满载可持续帧率**：`saturate` 场景每帧叫一次 `requestDraw()`，页内自己数"循环内画出的帧数 ÷ 循环真实时长（1.5s）"→ 239fps。**分母必须是循环自身时长**：用 rAF 总跨度折算会把循环前那 300ms 静置摊进去，把数字读低约 20%（本机就是这么从 239 读成 185 的）。
5. **事件落点自检**：每个场景派发前 `elementFromPoint` 探测画布中心，把压在上面的浮层（`.toast-item`/`.toast`）临时设 `pointer-events:none`，并统计真正落到 `CANVAS` 的输入数。**「画布事件 0 且 draws 0」的行一律 FAIL**——不加这条闸门，toast 盖住画布时 `pan-fit` 两行会带着"0 帧重绘"被当成 PASS 读。
6. **瓦片两条路径**：关 = `setTk(null)` 只画六角叠加层（纯矢量最坏情况）；开 = CDP `Fetch.enable` + `fulfillRequest` 把天地图瓦片请求 fulfill 成本机生成的 256px PNG。用伪瓦片不是偷懒：本机直连天地图会被中间层吞掉请求（既不 `onload` 也不 `onerror`，正是 `MapView._reapStalled` 存在的原因），真网络拿不到"瓦片已就绪"的稳态，可复现优先。
7. **已知局限**：无头走软件光栅（SwiftShader），比有头 GPU 合成保守；达标即可，不达标要在有头浏览器复测。
8. **改这个脚本时先看 JSON 明细**：汇总对象里曾把 `fpsUpper` 键写了两遍，后一个静默覆盖前一个，于是三轮"换口径"打出来的数字一模一样、还都被我解释成了"环境差异"。新加字段要确认它真的出现在 `.tmp/map-perf-<role>.json` 里。

**浏览器 E2E 取证口径**：验收必须在真实浏览器里跑通，不接受"逻辑层单测过了"替代。做法是起 5018 + `npm run dev`，用无头 Chrome（`--headless=new --remote-debugging-port` + 每次新建 `--user-data-dir`）经 CDP 同时驱动 4 个标签页，按角色路径打开 `/room/{码}/{red|blue|director|spectator}`，读 DOM 断言 + 截图存 `<tmp>/hf-e2e/<用例>-<随机>/`。脚本要点（都是踩过的坑）：`/json/new` 必须用 `PUT`；每个 target 一条独立 WS；己方卡与接触卡选择器要分开（`.unit:not(.contact)` 与 `.unit.contact`）；回合数读 `.maphud` 的 innerText；重开 Qoder 内置浏览器标签会永久卡死，不要用。**取完证要目视截图**——本轮的 `.scroll{overflow:auto}` 缺陷就是只有截图能看出来，DOM 矩形探针反而会误报。

**本轮由浏览器取证翻出的一条 P6-d 遗留缺陷**：React `App.css` 在移植时丢了 web 版 `.scroll{overflow:auto}`（对照 `web/css/app.css:61`），结果是超长的接触列表/事件日志直接画到相邻面板上；第一版修复给非 `.grow` 面板加了 `flex:0 1 auto`，反而让列溢出时 `.scroll` 被压成 0 高、整块列表看不见。最终规则是 `.panel .scroll{flex:1;min-height:0;overflow:auto}`（[hexfront-react/src/App.css:476](file:///D:/Project/wargame/hexfront-react/src/App.css#L476)）+ `.terminal .col .panel:not(.grow){flex:0 0 auto;max-height:28%}`（同文件 L479），三视图各面板可见高度 ≥137px、两个列表各自独立滚动，红/蓝/导演部/观战四张截图复核通过。

**复现"已消失（gone）"情报卡的最短路径**（纯 AI 局 14 回合零歼灭，见 §11.12，所以必须压 OOB）：
1. `POST /api/instances`，`scenario=rhine_corridor`，`overrides.sides.blue.oob` 里把 4 个 r rifle 条目的 `personnel_ratio` 设 `0.004`（每单位 2 人）；
2. 导演部连上后逐回合 `{type:'director_cmd',action:'ai'}` 托管双方 + `{type:'resolve'}` 推屏障（屏障是事件驱动的，不会自己连跑，§11.8）；
3. 红方在 T9 左右拿到 B23/B26 的 `destroyed`，接触卡转为 `E1 · 已消失`，并在 `staleTurns`=3 个回合后（age 1→2→3）从档案里被剪掉。

**`web/` 原生 ESM 客户端的处置（#92 已定：保留为只读参考，不删除）**：自 P7 拍板"React 为唯一主前端"后，`web/` 停止接新需求，只作为 CSS 视觉 token 与 `map.js`/`hex.js` 移植对照的**只读参考**；§6.3、§9、§10 里凡引用 `web/js/*` 的条目都在说"最初口径来自这里"。冻结声明落在 [web/README.md](file:///D:/Project/wargame/web/README.md)，里面同时登记了两处仍在读 `web/` 的脚本——`scripts/hex-offset-check.mjs` 直接 `import ../web/js/hex.js`，`HexFront.CSharp/scripts/web-static.mjs` 是旧客户端的静态托管（`ROOT=../../web`，默认 8080 同源代理到 5018）——两者都还能跑，是"不删"这一决定的直接成本，也是它的直接理由。

**移植遗漏对照清单（#92 收口）**：正反两个方向的 CSS 差分（`web/css/app.css` 199 条规则 ↔ React `App.css`+`index.css` 307 条；React 侧 105 个 `className` 字面量 token ↔ 187 个 CSS 类名），命中的 6 个"React 用了但没有任何规则"的 token 全部用无头 Chrome 读计算样式 + 截图复核，确认是真缺陷而不是"内联样式替代"。逐条结论：

| web 里的规则 | React 实测（修复前） | 结论 |
|---|---|---|
| `.scroll{overflow:auto}`（`web/css/app.css:61`） | 面板内容画到相邻面板上 | 已由 P6-d 修复（§13.6 上面那条），本次复核 12 处 `className="scroll"` 全在 `.panel` 内，无第二例 |
| `.steps` + `.steps .st` + `.st.on`（L91-94） | 五个步骤标题竖排成整行纯文本：pill 宽 1382px、`background:transparent`、`border:0`，**当前步与其余步视觉完全相同** | #92 补回，实测 pill 宽 69–108px 横排、`.st.on` 底色 `rgb(35,43,51)` + 前景/描边 accent 绿 |
| `.wiz` + `.wiz .body` + `.wiz .side`（L95-97） | `display:block`、`grid-template-columns:none`、两栏 `overflow-y:visible` → 1fr+320px 双栏塌成一条整宽长列，右栏（点数预算/AI 代管/校验/创建按钮）掉到正文下方 | #92 补回，实测 `grid:1010px 320px`、两栏各自 `overflow:auto` |
| `.oob-table` 基表 + `th/td` + `input/select`（L98-100） | React 只搬了 `.num` 一条 → 表格不 `border-collapse`、单元格零内边距、输入框不撑满列宽 | #92 补回，实测 `collapse` / `padding:4px` / `1px` 下边框 / 输入框 `3px 5px` |
| `#nav a.on`（L23） | 选择器被改名成 `.nav a.active`，而 `Layout.tsx:20` 输出的是 `on` → 顶栏当前页永不高亮（实测 `color:rgb(139,152,164)`、背景透明，与未选中项一致） | #92 改回 `.nav a.on`，首页截图"首页"已是绿字 + 药丸底色 |
| `.room-list table/th/td`（L84-86） | React 大厅是卡片列表（`.room-card`），无 `.room-list` 类 | **有意重设计，不是遗漏**（密集列表用卡片不用表格是既定偏好） |
| `label>span{color:var(--dim)}`（L54） | React 由 `label{color:var(--dim)}` 继承同一颜色 | 等价，不是遗漏 |
| —— | React 有 `.warn-card`（`SideView.tsx:242`/`DirectorView.tsx:127`/`SpectatorView.tsx:76` 三处）却无规则 | 无害空类：它只是 `.card` 的语义标记，红字由内层 `.warn` 负责，不补规则 |

补 `.wiz` 时踩到一个连带问题，值得记一笔：`.wiz` 原来靠内联 `height:calc(100% - 78px)` 撑高度，网格一恢复，页脚"上一步/下一步"就被顶出视口（截图实测按钮 `bottom > innerHeight`）。改成 `.wizard-page` 做 flex 列、`.wiz{flex:1 1 auto;min-height:0}`、页脚独立成 `.wiz-foot{flex:0 0 auto}` 后，1440×900 下页脚 `bottom=802 == innerHeight=802` 正好贴底可见。**这类"补回规则反而暴露旧内联高度"的连锁，是本轮取证必须看图不能只看 DOM 的又一个理由。**

**已知残留（#92 拍板：只标注，不删）**：
- `HexFront.Core.csproj` 引了 `NetTopologySuite 2.6.0` + `ProjNET 2.1.0`，源码零引用（§13.1 规划期留下的空间计算方案，实收走了自写 odd-r 几何）。
- `hexfront-react` 的 `package.json` 里有 `zustand 5.0.15`，源码零 `import`——React 侧没有 store，状态在 `room/RoomSocket.tsx` 的 context 与页面局部 state 里。
- React `App.css` 里 **58 个类名在 tsx 全文中一次都没出现**（`.order-*`/`.history-*`/`.command-panel`/`.unit-info`/`.wizard-form`/`.form-group`/`.map-placeholder`/`.conn` 等，占 187 个 CSS 类名的三成）：初版脚手架按规划名写的，组件后来用的是 web 的类名。删它风险大于收益，留作死样式，改前端时别误当"在用"。
- `_probe/` 目录的历史探针产物（§13.5 第 4 条）。

`data/tianditu.json` 是访问凭证，`.gitignore` 已挡住，仓库里只留 `data/tianditu.example.json` 占位（§12.2）。
