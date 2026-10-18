# 等价测试对照表（verify.dart 61 项 + smoke_ws 32 项 → xUnit）

基准是两条 Dart 脚本：`hexfront/bin/verify.dart` 一次跑 61 项引擎断言，`hexfront/bin/smoke_ws.dart` 一次跑 32 项多人链路断言，
两条合起来是 Dart 时代"引擎还活着、链路还没漏"的唯一凭证。
迁移到 C# 之后不复刻脚本本身（按设计文档重写，不是按 Dart 重写），但**每一项断言保护的是什么玩法**必须逐条落地成 xUnit，
否则"全绿"只是我们自己写的测试全绿。

- §1–§9 是引擎侧（#88 P7-a），§10 是协议侧（#89 P7-b）。
- 状态取值：`等价`（同一事实，同一断言）/ `等价+`（断得比 Dart 更严）/ `拆分`（一项拆成多处）/ `红灯`（用例已存在但当前不通过）/ `无等价`（写明理由）。

## 怎么跑

```powershell
cd HexFront.CSharp
dotnet test tests/HexFront.Tests                                  # 全量
dotnet test tests/HexFront.Tests --filter "FullyQualifiedName~P7Tests"   # 只看 P7-a 新增
dotnet test tests/HexFront.Tests --filter "FullyQualifiedName~Protocol"  # 只看 P7-b 协议侧
```

当前实测：**总计 241 / 通过 241 / 失败 0**（引擎侧 201 + 协议侧 40）。P7-a 遗留的那盏红灯
`AiOfficerP3Tests.Auto_Battle_Fights_And_Keeps_The_Front_Moving` 已在 P7-c 随 `Hex.Range` 遍历序修正一并转绿，
#94 已按此结案（2026-10-18 拍板），不再单独排期改 `AiService`。
协议侧 40 项连跑三次全绿（同一 Kestrel 子进程、同一临时存档目录），全量 241 同样连跑三次全绿。

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

## §10 协议对照（smoke_ws.dart 32 项 → dotnet test，#89 P7-b）

基准是 `hexfront/bin/smoke_ws.dart`（32 项 `check()`）+ `HexFront.CSharp/scripts/ws-smoke.mjs`（51 处 `fail()` 闸门，
它是 Dart 冒烟的 C# 侧改写，另带了 1a/3/5/7/7b/7c/8a/8b/9/10/10b/10c/11/12 这些 Dart 没覆盖的线上契约）。
两边合起来的每一项都搬进了 `tests/HexFront.Tests/Protocol/`，共 **40 个用例**：

| # | smoke_ws.dart 断言 | C# 用例 | 状态 |
|---|---|---|---|
| 1 | 红方收到己方视图（kind=side、own 非空） | `RoleViewProtocolTests.Both_Sides_Get_Their_Own_View` | 等价+（另断本方视图里不许出现全量 `units`） |
| 2 | 蓝方收到己方视图 | 同上 | 等价+ |
| 3 | 导演部为全知视图 | `RoleViewProtocolTests.Director_View_Is_Omniscient` | 等价 |
| 4 | 观战非全知：无未暴露敌方明细 | `RoleViewProtocolTests.Spectator_Is_Not_Omniscient_By_Default` | 等价+（同时断 `state` 包里的 kind） |
| 5 | 红蓝单位集合不交叉 | `RoleViewProtocolTests.Side_Rosters_Do_Not_Overlap` | 等价 |
| 6 | 红位已被占用时第二次加入被拒 | `RoleViewProtocolTests.Occupied_Side_Slot_Is_Refused` | 等价+（Dart 只看到"连接失败"；C# 断 error 文案含「角色位」+「force」，且拒完必须关连接、mjs 10c） |
| 7 | 指令草案回传本方 | `OrderProtocolTests.Draft_Reaches_Owner_And_Director_But_Not_The_Opponent` | 等价 |
| 8 | 蓝方看不到红方指令草案 | 同上 | 等价 |
| 9 | 导演部可见双方已受领指令 | 同上（`view.orders.red` 非空） | 等价+（另断没下令的蓝方那侧必须是空） |
| 10 | 非法指令被服务端拒绝并回传原因 | `OrderProtocolTests.Illegal_Order_Blocks_Ready_Until_It_Is_Fixed` | 等价+（钉住「存在 N 条非法指令」这条 error 恰好一回，且提交阶段只回 `view.errors` 不发 error） |
| 11 | 非法指令下 ready 不生效（不污染对局） | 同上（`ready` 后 `view.ready != true`） | 等价 |
| 12 | 合法指令后就绪成功 | 同上（改成邻格后就绪） | 等价 |
| 13 | 导演部触发结算，回合推进 | `OrderProtocolTests.Resolve_Advances_One_Turn_And_Filters_Events_By_Side` | 等价+（先等草案落地再结算，避免红方 orders 与导演部 resolve 两条连接抢跑） |
| 14 | 红方收到本方事件 | 同上（事件 `code` 里必须有 `move`） | 等价+（Dart 只断"非空"，一条 `turn_end` 也算过） |
| 15 | 蓝方看不到红方私有机动事件 | 同上 | 等价 |
| 16 | 导演部改天气生效 | `DirectorCmdProtocolTests.Weather_Override_Reaches_Every_Terminal` | 等价+（四个终端都断，改出来的天气 id 要进每一端视图） |
| 17 | AI 代管可切换 | `DirectorCmdProtocolTests.Ai_Management_Toggles_Appear_On_The_Director_View` | 等价+（开→关来回切，`view.ai.blue` 跟着变） |
| 18 | 导演部可关闭迷雾（教学模式） | `DirectorCmdProtocolTests.Fog_Can_Be_Turned_Off_For_Teaching` | 等价+（`view.fog` 与 `flags.fog` 两处一起断） |
| 19 | 观战全知档可切换 | `DirectorCmdProtocolTests.Spectator_Omniscient_Flag_Switches_The_Spectator_View` | 等价（新开的观战终端 kind 翻成 director） |
| 20 | 存档列表可见本局 | `SaveLoadProtocolTests.Save_Is_Listed_And_Reloads_At_The_Same_Turn` | 等价+（`saved.instance` 必须等于建局返回的副本 id，手动存档不许另开副本） |
| 21 | 存档可载入为新房间 | 同上（`POST /api/saves/load` 回 code） | 等价 |
| 22 | 载入后回合数一致 | 同上（`resumed_turn` + 新房间首包 `turn`） | 等价+（另断 `saved.turn = meta.last_turn` 是已结算回合） |
| 23 | 对抗方断开后阵营位立刻释放 | `SessionProtocolTests.Disconnect_Frees_The_Slot_And_Token_Reconnect_Takes_It_Back` | 等价（轮询 `side_taken.blue == false`） |
| 24 | 同 token 重连可接管本方视图 | 同上 | 等价 |
| 25 | 重连终端持续收到服务端推送（会话未成为孤儿） | 同上（重连后结算，`turn` 继续推进） | 等价 |
| 26 | 同角色双开：旧终端收到 kicked（不会静默失联） | `SessionProtocolTests.Same_Token_Takeover_Kicks_The_Old_Terminal` | 等价+（另断 `kicked.reason`） |
| 27 | 对面 AI 代管时，人类就绪即自动结算（不再空等） | `SessionProtocolTests.Human_Ready_Against_An_Ai_Side_Auto_Resolves` | 等价 |
| 28 | 覆盖后点数由服务端自动计价 | `OverrideRestProtocolTests.Overridden_Types_Are_Priced_By_The_Server` | 等价+（加强后红方 OOB 破预算，`errors` 要照样报「超出预算」而不是悄悄放行） |
| 29 | 规则语义字段（cls）不允许覆盖 | `OverrideRestProtocolTests.Rule_Semantic_Fields_Cannot_Be_Overridden` | 等价 |
| 30 | 越界属性被拒 | `OverrideRestProtocolTests.Out_Of_Range_Attributes_Are_Rejected` | 等价 |
| 31 | 未知兵种覆盖被拒 | `OverrideRestProtocolTests.Unknown_Unit_Type_Override_Is_Rejected` | 等价 |
| 32 | 非法覆盖建不出副本 | `OverrideRestProtocolTests.Illegal_Override_Cannot_Create_An_Instance` | 等价+（`/api/validate` 与 `POST /api/instances` 走同一套检查，绕过向导直接建也不行） |

### mjs 独有闸门（Dart 没有、React 前端依赖的线上契约）

| mjs 项 | 事实 | C# 用例 |
|---|---|---|
| 0 | 未知房间码在握手就被拒，不再静默建局 | `RoleViewProtocolTests.Unknown_Room_Code_Is_Rejected_At_Handshake` |
| 1a | `welcome` 首包自带 `view` 与 `room`，前端靠它落第一帧 | `RoleViewProtocolTests.Welcome_Carries_View_And_Room_Info` |
| 3 | `pending{red,blue}` 按草案条数计数，前端用它显示"对方是否已下令" | `OrderProtocolTests.Pending_Counts_Land_On_The_Director_Packet` |
| 4/9 | 结算权只在导演部；非导演部 `resolve` 被拒且不推进回合 | `OrderProtocolTests.Only_The_Director_Can_Resolve` |
| — | 观战/导演部不许下指令 | `OrderProtocolTests.Non_Combatants_Cannot_Submit_Orders` |
| 5/6 | 回放帧含导演部全知视图，对抗方取用必须被拒 | `SessionProtocolTests.Replay_Frames_Are_Reserved_For_Director_And_Spectator` |
| 10 | 每帧都带 `battles`（无交战也是空数组），帧表裁剪在 200 内 | `SessionProtocolTests.Frame_Rest_Carries_Battles_Array_And_Stays_Capped` |
| 7 | 阵营位被他人占用时，带 `force=1` 才能接管，旧终端收 kicked | `SessionProtocolTests.Foreign_Token_Needs_Force_To_Take_Over_A_Side_Slot` |
| 10b | 观战同角色顶号**不需要** force | `SessionProtocolTests.Spectator_Takeover_Needs_No_Force` |
| 11 | 全部终端下线后 `roles` 归零、`side_taken` 翻回 false | `SessionProtocolTests.All_Terminals_Offline_Releases_Every_Slot` |
| 7/7b | 存档落盘（meta/init/turns.jsonl/snapshot_t1）与 `rng_at_resolve` 起点 | `SaveLoadProtocolTests.Archive_Lands_On_Disk_With_The_Rng_Start_Point` |
| 8/8a | 载入房间的指纹 = 房间指纹；续跑房间补一帧带 `battles` 的 seed 帧 | `SaveLoadProtocolTests.Resumed_Room_Keeps_The_Fingerprint_And_Seeds_A_Replay_Frame` |
| 7c | 快照每 3 回合滚一份，载入只重放尾部那一回合 | `SaveLoadProtocolTests.Snapshots_Roll_Every_Three_Turns_And_The_Tail_Replays` |
| 8/8b | 明牌（fog off）不留档案：开雾时看得见全部敌人，重开雾后 contacts 必须缩回去 | `DirectorCmdProtocolTests.Fog_Off_Illuminates_But_Leaves_No_Archive` |
| — | 未知天气被拒；`ping` 有 `pong`；未知消息类型回错误但不关连接 | `DirectorCmdProtocolTests.Unknown_Weather_Is_Rejected`、`SessionProtocolTests.Ping_Keeps_The_Session_Alive_And_Unknown_Type_Reports_Back` |

### mjs 8c / 8d 没有整条搬过来，理由

`8c. 战报因子链`要的是"红方人工机动 + 突击连推 10 回合，期间真打出交战，每场 `battles` 带满 12 个系数 + `cf` 乘积"。
它的前半段（贪心路线一路推进去打野）依赖对方持续让出通路，正是 **#94 战线震荡**当前过不去的那条；
后半段（因子链齐备）保护的其实是"系数逐项随战报下发"这条文档契约，引擎侧已有
`CombatChainP2Tests.Factor_Map_Exposes_The_Whole_Chain` 直接对着 `TurnResolver` 的战报断，不必再起一个房间等交战发生。
所以协议侧只保留 `battles` 数组恒在（mjs 10 那一行），把"12 个系数齐备 + 真打出来"合到 #94 交付时一起验。
`8d. 全 AI 自动对抗`同理：终局判定与 ≤5s 由引擎侧 `TurnResolverP3Tests.Ai_Auto_Battle_Terminates_And_Stays_Interactive` 承担。

### 地基：为什么协议测试必须起一个真进程

第一版把 `Program` 直接在测试进程里 `WebApplication.CreateBuilder` 起来，Kestrel 起来了、端口也通了，
但**每个控制器路由都 404**：MVC 的控制器发现默认扫 `Assembly.GetEntryAssembly()`，在 `dotnet test` 下那是测试宿主，
不是 `HexFront.Api.dll`。顶号、释放、存档往返这些语义只有"第二条连接真站在另一端"才验得到，
所以 `ApiFixture` 改成把 `HexFront.Api.dll` 当**子进程**用 `dotnet` 拉起来：

- 端口用 `FreeTcpPort()` 现挑的环回口，`--urls` 传进去；`HF_SAVE_DIR` 指到本次运行的临时目录，测试存档不落进 `data/saves`。
- 就绪判定是轮询 `GET /api/rooms`（40s 超时），超时把子进程 stdout/stderr 尾部一起抛出来，避免"起不来但看不见日志"。
- `DisposeAsync` 用 `Kill(entireProcessTree: true)` 收尾并删临时目录；`HexFront.Tests.csproj` 保留对 `HexFront.Api` 的项目引用，只为保证它先被编译。
- 生产代码零改动（`Program.cs` 里为了进程内宿主加过的 `public partial class Program {}` 已回滚）。

---

## verify.dart 没有、C# 侧补上的用例

不复刻 Dart 意味着按设计文档补齐文档写过但 Dart 没测的分支，主要包括：

- **迷雾纪律**：`FogAndAiP0Tests` 的 fog off 不写档案 / 重新开雾不继承明牌情报；`CommandValidatorP3Tests.Unobserved_Enemy_Hex_Is_Traversable`（进入未知格交给遭遇战，不在服务端一票否决）。
- **接触档案的中间态**：`A_Dead_Under_Observation_Still_Reports_Gone`——档案按龄衰减而非死亡即清除，前端因此需要 `gone` 标签（`hexfront-react/src/room/protocol.ts`）。
- **AI 只用看得见的信息**：`AiOfficerP3Tests` 9 项（不打看不见的目标、未知格是靠过去而不是猜着打、只有防空兵打飞机、炮兵在射程边缘射击不硬冲、固定设施不下令、渡河不拜河、同一局势同一指令、每存活单位恰好一令）。
- **结算分支**：`TurnResolverP3Tests` 32 项（七阶段顺序、五种战斗类型、空中交战走同一条 CF 链、遭遇战取稳定序的一个防守方、伏击、溃退往自己纵深、同回合不开火回血、集结阈值、断供回落宽松默认、四种胜利 + 回合耗尽人员判定、终局幂等）。
- **存档边界**：`DeterminismP1Tests` 的 jsonl 回退重开（rebranch 截断日志）、存档目录清单、实例 id 不许越出 saves 根目录。
- **坐标基线与取用序**（#90 P7-c，doc §6.5）：`HexTests.Range_Visits_Distance_Bands_In_Stable_Order` 与
  `Range_First_Band_Is_The_Neighbor_Table_Sorted` 把"半径遍历按 (距离, q, r) 定序、同环按列再按行"钉成用例——
  剧本落位兜底 `SpawnAt` 这类"取第一个可用格"的调用只有在这个序下才等于"取最近格"，Dart 侧靠 `hexRange` 内部 sort 实现，
  verify.dart 没有对应断言（Dart 的 `hexRing` 零调用，按不复刻死代码的基准不移植，环这一档由 `Range` 的距离分层 6·d 格代证）。
- **#94 红灯的真实根因不是 AI**（探针实测，探针文件已删）：`Range` 改序前后逐字复刻两种枚举，对比 36 个开局落位，
  与引擎实际落位 **0 处不符**（说明复刻准确），且落位与随机种子无关（seed 1 / 7 / 20261018 三组完全一致）。
  旧的 `dq` 主序把 **15 个红方单位**甩出名义格，其中 10 个甩到半径上限 `d=4`：HQ `R1 (2,9)→(0,12)`、
  `R21 (3,13)→(0,15)`、补给/工兵 `R17/R18/R16` 全部退到棋盘左缘 `q=0`；改序后同样的名义格只挪 `d≤2`（多数 `d=1`），
  `R1→(3,8)`、`R21→(2,13)`、`R15` 直接留在名义格。真河网把 14 处开局坐标淹成 `river` 才让兜底这么高频率触发，
  而兜底一不按距离取格，就等于把红方纵深单位整建制扔到棋盘角上——副官再怎么权衡也推不动战线，表现为"战线震荡/不推进"。
  改序后用一次性探针扫 5 个种子 × 14 回合双 AI 全托管：`rejected` 5 例全 0，交战 25–54 场，30/36 单位有位移，
  红方平均深度 13.6 → 11.5–11.8 单调推进（第 4 回合一次回撤后续推）。#94 因此不需要再单独改 `AiService`。
- **浏览器侧取证（同一份代码，红方终端 21 张部队卡）**：逐卡读 `.meta` 里的 `q,r`，断言每个单位距剧本名义格 ≤2 格
  （最大位移 `R12 7,7→6,5` 恰为 2）、`R1@3,8`、`R21@2,13`、西南角（q≤1 且 r≥10）无单位、HUD 正常渲染回合/天气/掌握要点。
  顺带发现 doc §6 结尾那句"落位后红方确握 3 处要点"是 Dart 时代数字，现在红方开局握 **5** 处，归 #91 文档一致性改。

## 协议侧没有 Dart 对应、按设计文档补上的用例

`smoke_ws.dart` 只验"跑通了"，下面这些是文档写过、Dart 没断的分支，全部按线上契约补进 §10：

- **权限矩阵**：结算权只在导演部（`Only_The_Director_Can_Resolve`，被拒后还要断回合没被偷偷推进）、
  指令权只在阵营方（`Non_Combatants_Cannot_Submit_Orders`）、导调权只在导演部（`Only_The_Director_Can_Issue_Cmd`）、
  回放帧只给导演部与观战（`Replay_Frames_Are_Reserved_For_Director_And_Spectator`）。
- **顶号三档**（doc §12.1）：同 token 接管→旧终端收 `kicked`；他人占阵营位→不带 `force` 直接被拒且给出原因；
  观战/导调位免 `force` 直接顶（`Spectator_Takeover_Needs_No_Force`）。
- **断开即释放**（doc §12.1 实测点）：全部终端下线后 `roles` 每个计数归零、`side_taken` 翻回 false，
  不留半开会话（`All_Terminals_Offline_Releases_Every_Slot`）。
- **明牌不留档案**（doc §11.9）：导演部关迷雾做教学演示之后重新开雾，红方 contacts 必须缩回正常水平
  （`Fog_Off_Illuminates_But_Leaves_No_Archive`）——这条同时是协议断言和引擎侧 `FogAndAiP0Tests` 的线上镜像。
- **welcome 首包形状**（P6-a）：`token` / `room` / `view` 三项齐备，前端靠它落第一帧，不用等 `state`。
- **回放帧契约**（P6-b）：每帧带 `battles`（无交战也是空数组）、帧表裁剪在 200 帧内、
  REST `GET /api/frames/{code}` 与 WS `frames` 两条路同形。
- **存档一致性**：`saved.instance` 等于建局的副本 id（手动存档不许悄悄开新副本）、`snapshot_t*.json` 每 3 回合滚一份、
  载入只重放快照之后的尾部回合且 `replay_warnings` 为空。
- **非法覆盖建不出副本**：`/api/validate` 与 `POST /api/instances` 用同一把尺子，向导被绕过也一样拦。

`scripts/ws-smoke.mjs` 继续保留：它的 8c/8d 需要前线真的开火（挂 #94），加上要跑 10 回合贪心推进，
不适合放进 `dotnet test` 的秒级路径，留作手工长测；上面这 40 项已经把同样的闸门搬进可重复执行的入口，
交付验收以 `dotnet test --filter "FullyQualifiedName~Protocol"` 为准。

