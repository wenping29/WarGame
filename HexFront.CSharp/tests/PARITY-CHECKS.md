# 引擎侧等价测试对照表（verify.dart 61 项 → xUnit）

基准是 `hexfront/bin/verify.dart`：Dart 时代那条脚本一次跑 61 项断言，是"引擎还活着"的唯一凭证。
迁移到 C# 之后不复刻脚本本身（按设计文档重写，不是按 Dart 重写），但**每一项断言保护的是什么玩法**必须逐条落地成 xUnit，
否则"全绿"只是我们自己写的测试全绿。

- 本文覆盖 **#88 P7-a**：引擎侧 61 项。协议侧 `smoke_ws` 32 项归 **#89 P7-b**，见文末。
- 状态取值：`等价`（同一事实，同一断言）/ `等价+`（断得比 Dart 更严）/ `拆分`（一项拆成多处）/ `红灯`（用例已存在但当前不通过）/ `无等价`（写明理由）。

## 怎么跑

```powershell
cd HexFront.CSharp
dotnet test tests/HexFront.Tests                                  # 全量
dotnet test tests/HexFront.Tests --filter "FullyQualifiedName~P7Tests"   # 只看 P7-a 新增
```

当前实测：**总计 199 / 通过 198 / 失败 1**（唯一红灯是 `AiOfficerP3Tests.Auto_Battle_Fights_And_Keeps_The_Front_Moving`，挂账 #94）。

建局一律走 `tests/HexFront.Tests/Engine/RealScenario.cs`：读随仓剧本 `data/scenarios/rhine_corridor.json`，
overrides 走线上同一份 `ScenarioService.MergeOverrides`，所以测出来的语义就是 `POST /api/instances` 的语义。

---

## §1 剧本 → 副本（1–5）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 1 | 剧本校验通过 | `ScenarioBuildP7Tests.Rhine_Corridor_Builds_A_Valid_Full_Battlefield` | 等价 |
| 2 | 初始单位生成 ≥15 | 同上 | 等价 |
| 3 | 地形格网完整 ≥400 格 | 同上 | 等价 |
| 4 | 全部单位出生在可通行陆地 | 同上（水/绝壁逐单位查） | 等价 |
| 5 | 开局双方互不可见 | `FogViewP7Tests.Fresh_Board_Keeps_Both_Sides_Blind` | 等价+（还断接触列表里查不到对方单位） |

`Validate` / `MergeOverrides` 原先长在 `GameController` 里，已搬进 `HexFront.Core.Services.ScenarioService`，
控制器只剩调用。这条搬动是 #1 能测的前提——否则测的是控制器拷贝，不是线上路径。

## §2 战争迷雾与视图裁剪（6–12）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 6 | 预热后有已暴露目标 | `FogViewP7Tests.Warmup_Discloses_Only_What_The_Archive_Holds` | 等价（预热 8 回合而非 3，见下方说明） |
| 7 | 仍有未暴露目标（非全知） | 同上 | 等价 |
| 8 | 红方视图不含未暴露蓝方单位 | 同上 | 等价（Dart 那条 `leaked == blueAlive - exposed` 是恒等式，不另测） |
| 9 | 视图不含蓝方精确人数（除非 E3） | `FogViewP7Tests.No_Contact_Below_E3_Carries_Personnel`、`Type_Arrives_At_E2_But_Personnel_Only_At_E3` | 等价+ |
| 10 | E1 只给兵力档位不给兵种 | `FogViewP7Tests.E1_Reveals_Only_A_Strength_Band` | 等价+ |
| 11 | 攻击未暴露目标被拒（`not_observed`） | `CommandValidatorP3Tests.Attack_Needs_E1_Exposure_Under_Fog` | 等价+（额外断 level 0 陈旧线索仍不够） |
| 12 | 跳格路径被拒（`not_adjacent`） | `CommandValidatorP3Tests.Path_Rules_Have_Their_Own_Rejection_Reasons` | 等价+（同一用例覆盖 bad_path/path_start/out_of_map/impassable） |

预热回合数从 3 改到 8 的实测依据：`rhine_5km` 真实 DEM 上，夜间 + 恶劣天气把地面侦察半径压到 0.95–1.2 格，
红方第 6 回合才出现第一条航迹，10 回合里蓝方航迹数为 0、双方零伤亡（21/15 兵力原地不动）。
这是 **#94「AI 推进过慢/原地挖工事」**的症状，不是迷雾缺陷，所以这里只锁"照面之后档案与视图一致"，
不拿回合数当验收线。

## §3–§4 确定性与存档（13–15）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 13 | 同种子同指令 5 回合逐回合指纹一致 | `DeterminismP1Tests.Same_Seed_Same_Orders_Produce_Identical_Turns` | 等价+（另有 `Rng_Matches_Reference_Vector`、`Different_Seed_Diverges_In_Rng_Stream` 把随机源本身钉住） |
| 14 | 快照反序列化后指纹一致 | `DeterminismP1Tests.Snapshot_Plus_Tail_Replay_Resumes_Exactly`；`ScenarioBuildP7Tests.Ground_Features_…`、`Geo_Geometry_…` 走 snake_case JSON 往返 | 拆分 |
| 15 | 载入后续跑结果一致（RNG 已持久化） | `Snapshot_Plus_Tail_Replay_Resumes_Exactly`（断 `RngState` 相等 + 续跑一回合仍同指纹） | 等价+ |

## §5 自定义 OOB 与天气覆盖（16–20）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 16 | 覆盖后红方仅 6 个单位 | `ScenarioBuildP7Tests.Overrides_Swap_Oob_Personnel_Weather_And_Flags` | 等价+（同时断蓝方仍继承剧本） |
| 17 | 覆盖生效：天气=浓雾 | 同上 | 等价 |
| 18 | 覆盖生效：严格后勤开启 | 同上（`FogService.FlagBool`） | 等价 |
| 19 | 高训练度写进人员水平（92） | 同上 + `Oob_Commander_Lands_On_The_Unit_And_Absent_Commander_Uses_Neutral_Quality` | 等价+ |
| 20 | 点数预算可核算 | `ScenarioBuildP7Tests.Point_Budget_Is_Auditable_And_Overrun_Get_Reported` | 等价+（额外断超预算时**报的是哪条错**） |

#19 是真缺陷：`OobEntry` 原先没有 `training/experience/commander` 三个字段，向导第 4 步填的人员水平和剧本里的
指挥官被静默丢掉，E3 情报永远读不到指挥官。已按 doc §5 补齐（缺省回落兵种出厂值，指挥官质量缺省 0.5）。

## §6 全 AI 自动对抗（21–22）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 21 | 推演可推进且有战斗发生 | `AiOfficerP3Tests.Auto_Battle_Fights_And_Keeps_The_Front_Moving` | **红灯（#94）** |
| 22 | 终局判定生效 | `TurnResolverP3Tests.Ai_Auto_Battle_Terminates_And_Stays_Interactive` | 等价+（还断 ≤5s、`game_over` 事件） |

## §7 GIS 数据包 → 几何同源（23–29）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 23 | 副本格数取自数据包 | `ScenarioBuildP7Tests.Geo_Geometry_Is_Inherited_From_The_Data_Package` | 等价 |
| 24 | 引擎半径来自数据包而非 env 默认 | 同上 | 等价 |
| 25 | 对边距实地↔墨卡托放大自洽 | 同上 | 等价 |
| 26 | 几何记录实地口径 | 同上 | 等价 |
| 27 | 格网原点取自数据包 | 同上 | 等价 |
| 28 | 中心经纬度落回格网中心 | 同上（`Mercator.LonLatToMercator` + `Grid.Nearest`） | 等价 |
| 29 | geo 元信息可往返存档 | 同上（radius / cellGroundKm / ox） | 等价 |

生成侧另有 `Gis/HexGenServiceTests`（meta 与规格一致、板中心落 bbox 中点、snake_case 落盘往返）与
`Gis/GisPlanServiceTests`（阶梯推荐、0.866 行距、墨卡托中点不是纬度均值）在源头守住同一批数。

## §8 兵种属性覆盖与自动计价（30–38）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 30 | 火力/机动/消耗写进定义库 | `TypeDefOverrideP4Tests.Apply_Writes_Stats_But_Keeps_Rule_Semantics` | 等价+（另断 cls/is_hq/counters 这类规则语义不许覆盖） |
| 31 | 未覆盖字段保持出厂值 | 同上 + `Scenario_Overrides_Reach_The_Game_State_And_The_Priced_Oob`（未覆盖兵种仍是出厂价） | 等价 |
| 32 | 自动计价：强化后点数上升 | `Pricing_Doubles_Tank_Firepower_To_45_9_Points` | 等价+（钉到 45.9 这个具体成交价） |
| 33 | 点数预算按覆盖后属性核算 | `Scenario_Overrides_Reach_The_Game_State_And_The_Priced_Oob` + `ScenarioBuildP7Tests.Point_Budget_…` | 拆分 |
| 34 | 显式点数优先，不做二次计价 | `Pricing_Keeps_Explicit_Points_And_Load_Costs_Nothing` | 等价 |
| 35 | 削弱后点数下降但不白送 | `Pricing_Clamps_Multiplier_To_0_35_And_2_6` | 等价+（上下限一起钉） |
| 36 | 副本单位读到强化后的兵种 | `Scenario_Overrides_Reach_The_Game_State_And_The_Priced_Oob` | 等价 |
| 37 | 存档自包含：载入后强化属性与成交价都在 | `DeterminismP1Tests.Save_Carries_The_Priced_Type_Definitions` | 等价 |
| 38 | 属性覆盖真的进入结算（同种子指纹分叉） | — | **无等价**，理由见下 |

**#38 不单独建用例的理由**：这条断言的分叉来源是"覆盖后的火力进了结算并打死过人"。
在 `rhine_corridor` 上、当前 AI 状态下 3 回合内不发生交战（#21 同一现象），双方都只走位，
指纹**不该**分叉——把它写成测试等于把 #94 的红灯复制成两条。
该断言保护的因果链已经被拆成三处更硬的地方：#30/#36（覆盖确实写进 `TypeOf(unit)`）、
#32/#33/#37（成交价进 OOB 与存档）、`CombatChainP2Tests`（火力是 CF 因子、CF 决定战损，
`Clash_Is_Reproducible_From_Rng_State` 保证同状态同结果）。
等 #94 修好、前线真的开火，再决定要不要补一条端到端的分叉断言。

## §9 地物层：铁路/机动/电网/机场/要点（39–61）

| # | verify.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 39 | 铁路/隧道/电网/机场写入引擎格网 | `ScenarioBuildP7Tests.Ground_Features_From_The_Gis_Package_Reach_The_Engine_Grid` | 等价 |
| 40 | 地物不变式（车站⊆铁路·桥⊆河·隧道在线上且不在水面） | 同上 | 等价 |
| 41 | 地物字段随存档往返 | 同上（snake_case JSON，铁路/隧道/机场计数不变） | 等价 |
| 42 | 铁路使摩托化更省，徒步不受惠 | `MovementCostP7Tests.Railway_Speeds_Up_Heavy_Traffic_But_Not_Foot` | 等价+（钉住 0.8 倍率） |
| 43 | 公路与铁路折扣可叠加（0.65×0.8） | `MovementCostP7Tests.Road_And_Rail_Stack_Into_Half_The_Cost` | 等价+ |
| 44 | 隧道限行：重装备比徒步更吃亏 | `MovementCostP7Tests.Tunnel_Punishes_Heavy_Vehicles_Harder_Than_Foot` | 等价+（1.5 vs 1.2） |
| 45 | 无桥渡河重罚 +2，桥位免罚 | `MovementCostP7Tests.Fording_Costs_Two_Extra_Steps_Over_A_Bridge` | 等价+ |
| 46 | 开局双方都不通电（系数 1.0） | `SupplyP3Tests.Gridless_Map_Gives_No_Bonus` + `Power_Needs_Plant_And_A_Substation_Together` 首断 | 等价（C# 在合成格网上测，不在真鲁尔包；真包侧由 #39 保证地物确实存在） |
| 47 | 只夺电厂仍不通电 | `Power_Needs_Plant_And_A_Substation_Together` | 等价 |
| 48 | 电厂+变电站同时掌握才通电（封顶 +15%） | 同上（1.15 封顶） | 等价+（另有 `Partial_Grid_Hold_Scales_The_Bonus` 按 2/3 节点给 1.10、`Contested_Grid_Node_Does_Not_Energize`） |
| 49 | 未掌握电网的一方不吃红利 | 同上 | 等价 |
| 50 | 电网红利进入补给系数 | `Grid_Bonus_Flows_Into_Supply` | 等价+（断到 `dry × 1.15` 精确值） |
| 51 | 地面部队站上机场 ⇒ 该格成为补给节点（距离归 0） | `Airport_Node_Needs_Own_Ground_Troops_And_Air_units` | 等价 |
| 52 | 航空兵撤离后机场不再供补给 | 同上 | 等价 |
| 53 | 机场等级只用 1/2 | `ScenarioBuildP7Tests.Ground_Features_…`（`Airport <= 2`） | 等价 |
| 54 | 要点清单齐备（含类别与中文标签） | `FogViewP7Tests.Node_Geography_Is_Public_But_Ownership_Is_Not` | 等价 |
| 55 | 各方视图的要点地理一致（地形不是情报） | 同上 | 等价 |
| 56 | 双盲视图只披露己方占领 | 同上（两侧 holder 键集不相交） | 等价+（另有 `SupplyP3Tests.Node_Holders_Exclude_Contested_And_Filter_By_Side` 钉争夺格） |
| 57 | 导演部视图给出双方电网系数 | `Director_Sees_Both_Grid_Factors_And_Spectator_Sees_Neither_Ownership` | 等价 |
| 58 | 观战视图不给要点归属 | 同上 | 等价+（同时断观战 summary 的四项兵力/人员计数） |
| 59 | 落位后红方确握 3 处要点 | `ScenarioBuildP7Tests.Holding_Three_Battlefield_Nodes_Ends_The_Game_For_Red` | 等价 |
| 60 | 要点数量达成即终局（kind=nodes） | 同上 | 等价（`TurnResolverP3Tests.Nodes_Condition_Counts_Objective_Hexes` 另测空清单不足额） |
| 61 | 带地物地图可连续推演且无异常 | `ScenarioBuildP7Tests.Auto_Battle_Runs_On_The_Feature_Map_Without_Stalling` | 等价+（额外断回合号推进 4、指纹变化、两侧补给表非空） |

---

## verify.dart 没有、C# 侧补上的用例

不复刻 Dart 意味着按设计文档补齐文档写过但 Dart 没测的分支，主要包括：

- **迷雾纪律**：`FogAndAiP0Tests` 的 fog off 不写档案 / 重新开雾不继承明牌情报；`CommandValidatorP3Tests.Unobserved_Enemy_Hex_Is_Traversable`（进入未知格交给遭遇战，不在服务端一票否决）。
- **接触档案的中间态**：`A_Dead_Under_Observation_Still_Reports_Gone`——档案按龄衰减而非死亡即清除，前端因此需要 `gone` 标签（`hexfront-react/src/room/protocol.ts`）。
- **AI 只用看得见的信息**：`AiOfficerP3Tests` 9 项（不打看不见的目标、未知格是靠过去而不是猜着打、只有防空兵打飞机、炮兵在射程边缘射击不硬冲、固定设施不下令、渡河不拜河、同一局势同一指令、每存活单位恰好一令）。
- **结算分支**：`TurnResolverP3Tests` 32 项（七阶段顺序、五种战斗类型、空中交战走同一条 CF 链、遭遇战取稳定序的一个防守方、伏击、溃退往自己纵深、同回合不开火回血、集结阈值、断供回落宽松默认、四种胜利 + 回合耗尽人员判定、终局幂等）。
- **存档边界**：`DeterminismP1Tests` 的 jsonl 回退重开（rebranch 截断日志）、存档目录清单、实例 id 不许越出 saves 根目录。

## P7-b 待补：smoke_ws 32 项

协议基准是 `hexfront/bin/smoke_ws.dart`（32 项 check，覆盖三角色视图裁剪、pending 屏障、回合推进、手动存档落在同一副本、
`turns.jsonl` 记随机源、载入续跑指纹一致、seed 帧带 battles 等）。
C# 侧目前对应的是 **`HexFront.CSharp/scripts/ws-smoke.mjs`**（Node 手工跑，51 处 `fail()` 闸门），还没进 `dotnet test`。
#89 的计划是把这 32 项逐条搬成 xUnit（对真实 Kestrel 起 `/ws`），并在此文档追加"§10 协议对照"表。
在 #89 交付前，这张表只覆盖引擎侧，不代表协议侧有等价保护。
