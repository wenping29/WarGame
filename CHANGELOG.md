# 更新日志

HEXFRONT-WEB 从 Dart `hexfront/` + 原生 JS `web/` 迁移到 C# `server/` + React `webfront/` 的逐阶段变更记录。

- **条目粒度**：一个阶段（= 排期表里的一个 #）一条，分「新增 / 修复 / 文档 / 验证」四栏。
- **验收数字的唯一出处**是 [HEXFRONT-WEB产品设计文档.md](HEXFRONT-WEB产品设计文档.md) §13.6 的四条门槛命令，本文只记"当时是什么数"，不作为口径来源。
- 进度全景（哪些搬完、哪些没搬）看 [MIGRATION-STATUS.md](MIGRATION-STATUS.md)。
- 括号里是 git 提交短码，只对能从提交信息确认的条目标注。

## 2026-10-27 · GisApi 路径配置跨平台 + Windows 打包脚本

**新增**：`server/HexFront.GisApi/src/GisApiOptions.cs` 路径键从 2 个扩到 6 个——`TerrainDir`/`DataDir` 之外增加 `TerrainDirWindows`/`TerrainDirLinux`/`DataDirWindows`/`DataDirLinux`。运行时按当前系统优先取特化键、取不到回退通用键、都留空再按 `HF_REPO_ROOT` 推导默认值（`<repoRoot>/data/terrain`、`<repoRoot>/data/gis-api`）。**同一份 appsettings.json 即可在 Windows 与 Linux 间通用**，不必为不同系统维护两份文件。

**新增**：`GisApiService` 里 `SelectPlatformPath`（选键）+ `NormalizePath`（归一：展开 `$ENV`/`%ENV%`、展开 `~` 为用户主目录、统一 `/` 分隔符）+ `ResolveDir` 支持相对路径按仓库根解析。生成数据的存储目录（`DataDir` → 其下 `jobs/` 作业记录与 `grids/` 六角格包体）从此可配置。

**新增**：`deploy/build-release-zip.bat`（Windows 版发布包构建入口，与 `deploy/build-release-zip.sh` 等价，共用 `deploy/tools/pack_release.py`）。实跑验证：前端构建 → 后端 publish → 生成服务 publish → WAL checkpoint → 打包 5 步全通，产出 195 文件 / 2.0GB，zip 全量 CRC 校验通过。

**修复（踩坑）**：`.bat` 文件的编码/换行有三条硬约束，实测踩过——
① **不能用 `chcp 65001`**：在 UTF-8 批处理里改代码页，cmd.exe 会在**错误的字节偏移**上继续执行，文件头的 `REM` 注释行被当成命令执行（报一堆"不是内部或外部命令"）。矩阵实测：`chcp` 有无 × 编码 UTF-8/GBK × 换行 CRLF/LF 共 8 组，只有**纯 ASCII + CRLF + 无 chcp** 的组合输出干净。
② **必须 CRLF**：LF-only 的 .bat 同样会让 cmd.exe 解析错位。
③ 文件正文保持**纯 ASCII**（注释与提示用英文），避免任何代码页依赖。

**文档**：`deploy/release/DEPLOY.md` §4.1 补「路径跨系统通用」说明（通用键 + 系统特化键的选键规则、支持的路径写法）。

**注意**：`server/HexFront.GisApi/appsettings.json` 的 `//` 注释被移除（提交 `26021c7`）——本项目的 appsettings.json 保持**纯 JSON 无注释**，配置含义的说明放在 C# 侧注释与 DEPLOY.md 里。

---

## 2026-10-18 · P7-f 性能基线（#93）

两条一直显式挂着的验收线补齐：**14 回合全 AI < 5s**、**地图缩放/拖拽/双击 60fps**。数字与口径的唯一出处仍是 §13.6，这里只记当时是什么数。

- **新增**：`server/tests/HexFront.Tests/Performance/EnginePerfBaselineTests.cs`（2 条，`Trait=Benchmark`）——14 回合全 AI 对抗 + 稳态复跑同指纹；断言不只看耗时，还要求"14 回合真跑完、有交战、零被拒令、两遍同指纹"，防止空转局蒙过 5s 线。`Gis/HexGenBenchmarkTests.cs` 补第 3 条：按 doc §13.1 那块 2.5km 棋盘重测生成耗时。
- **新增**：`scripts/map-perf.mjs`——无头 Chrome + CDP 派发**真受信任事件**的地图帧率取证脚本，红方/导演部各跑 瓦片开 + 瓦片关 × 9 个场景（idle/pan/pan2/wheel-in/wheel-out/dblclick/buttons/pan-fit/saturate），产出 `.tmp/map-perf-<role>.json` + 截图。伪瓦片用 CDP `Fetch.fulfillRequest` 喂本机现造的 256px PNG（直连天地图会被中间层吞请求，拿不到"瓦片就绪"稳态）。
- **实测**：引擎建局 55ms、14 回合整段循环 202ms（补令 74 + 结算 115，均 14.4ms/回合），5s 预算用掉 4%，终局指纹 `5d4bd5b8` 与 `ws-smoke` 8d 逐字相同；GIS 1728 格 94ms（0.0544 ms/格），30s 门槛的 0.3%；地图满载可持续 **235.8–239.3fps**（瓦片开/关几乎无差），所有交互行 `MapView.draw` p95 ≤7.8ms、max ≤10.1ms。
- **翻出 1 条待拍板缺陷**：全场第一次交互那一帧 277–295ms、阻塞 227–243ms，LoAF 归属 `services/sound.ts` 的 `primeAudio`——`new AudioContext()` 同步建在第一个 `pointerdown` 监听里。一次性开销（第二次交互起 0 长帧），不影响 60fps 结论，但是玩家进图后第一次点击的卡顿。**本任务没动代码**，处置等拍板。
- **取证过程本身翻出两个测量陷阱**（都写进 §13.6 的口径八条）：① 无头不做 vsync，rAF 间隔 4.2ms 不能当帧率读，必须用"单帧 draw 预算 + 满载循环帧数"两条替代；② `pan-fit` 两行 0 帧重绘的真因是 toast 浮层吃掉了画布中心的指针事件——脚本因此加了 `elementFromPoint` 自检 + 「画布事件 0 且 draws 0 一律 FAIL」闸门。
- **文档**：§13.1/§13.2/§13.3 三条 `[ ]` 改 `[x]` 并就地写清口径（含 1728 vs 1645 的 bbox 回喂解释）；§13.4 风险表"60fps 数字待测"换成实测；§13.6 门槛命令从四条扩到五条（新增 `map-perf.mjs`）、`dotnet test` 基线 241→244、**`ws-smoke` 路径订正为 `server/scripts/`（原先文档写的根 `scripts/` 路径跑不起来）**。
- **验证**：`dotnet test` **244/244**（`--filter Category=Benchmark` 单跑 6/6）；`node server/scripts/ws-smoke.mjs` 16 组 ALL PASS（8d 15 回合 159ms）；`npx tsc -b` exit 0；`npx oxlint src` 7 warning / 0 error（基线不变）；红方 + 导演部两次完整取证，截图目视复核（地图、图例、悬停格信息、署名齐全）。

## 2026-10-18 · P7-e `web/` 处置与移植遗漏收口（#92）

拍板结果：**`web/` 保留为只读参考，不删除**；零引用依赖与死样式一律"只标注不删"。

- **新增**：`web/README.md` 冻结声明（只读参考的三条理由、"不要加功能/不要删"禁令、仍依赖 `web/` 的脚本登记）。
- **修复**（正反双向 CSS 差分 + 无头 Chrome 计算样式 + 截图查出，四组真没搬过来的规则）：
  - `.steps/.st/.st.on`——五个步骤标题原本竖排成整行纯文本（pill 宽 1382px、无底色无边框），**当前步与其余步视觉完全相同**；
  - `.wiz/.wiz .body/.wiz .side`——`1fr 320px` 双栏塌成单列、两栏各自滚动丢失；连带把 `.wiz` 的内联 `height:calc(100% - 78px)` 改成 `.wizard-page` flex 列 + `.wiz{flex:1 1 auto;min-height:0}` + 独立 `.wiz-foot`，否则页脚"上一步/下一步"被顶出视口；
  - `.oob-table` 基表 + `th/td` + `input/select`——原先只搬了 `.num` 一条，表格不 `border-collapse`、单元格零内边距；
  - `#nav a.on` 被改名成 `.nav a.active` 而 `Layout.tsx` 输出的是 `on`——顶栏当前页永不高亮。
  - 判定为"非遗漏"的三条：`.room-list`（大厅有意改卡片列表）、`label>span`（由 `label` 继承同色）、`.warn-card`（无害空类）。
- **文档**：设计文档 §13.6 新增「移植遗漏对照清单」与「已知残留」两块并改写 `web/` 处置段落；§6.3/§9/§11.6/§13.1/§13.3 的"退役排期"措辞改为已定结果；`MIGRATION-STATUS.md`、`webfront/MIGRATION.md`、`webfront/README.md` 同步；`scripts/hex-offset-check.mjs` 与 `server/scripts/web-static.mjs` 各加一行指回 §13.6。
- **验证**：`dotnet test` 241/241；`node scripts/ws-smoke.mjs` 编号 0–15 全 ALL PASS（8d 全 AI 15 回合 178ms、终局指纹 `5d4bd5b8`）；`npx tsc -b` exit 0；`oxlint` 0 error；`node scripts/hex-offset-check.mjs` ALL PASS（133 条无向边）；红方/导演部两视图回归截图无新增缺陷。

## 2026-10-18 · P7-d 交付文档一致性（#91）

- **文档**：设计文档 §12 从 Dart 运行手册整体换成 **C# + React 运行手册**（起服务命令、路径路由表、四条门槛命令）；§13 全节回填"原计划 vs 实收"，每处偏离就地标注；新增 **§13.6 交付基线与取证口径**（门槛数字、两条显式未测项、CDP 取证规则）；§6.3.1 编号冲突修正；§11 三条改判为"已定并落地"并新增第 12 条「纯 AI 局的歼灭率（待拍板）」；§12.1 占用语义由"HTTP 409"更正为 **`type:error` 帧 + 关闭连接**；§11.11 道路/铁路出处按 `data/gis/rhine_2p5km.json` 的 `meta.layers` 实测更正（道路是真实 Natural Earth 向量，铁路才是派生走廊）。
- **修复**：React 丢失的 `.scroll{overflow:auto}`（对照 `web/css/app.css:61`）导致超长接触列表/事件日志画到相邻面板上——最终规则 `.panel .scroll{flex:1;min-height:0;overflow:auto}` + `.terminal .col .panel:not(.grow){flex:0 0 auto;max-height:28%}`，四终端截图复核。
- **文档**：`MIGRATION-STATUS.md` 从 399 行过期快照重写为 46 行当前状态表；`webfront/MIGRATION.md`/`README.md` 按实收技术栈重写（React 19 / Router 7 路径路由 / 无 Konva / 无 AntD / **无 Zustand store**）。
- **验证**：`dotnet test` 241/241；ws-smoke 16 组 ALL PASS（8d 两次 237ms / 177ms）；`npx tsc -b` exit 0。四终端无头 Chrome 取证：E1/E2/E3 情报分档、`status=gone` 卡片存活至 `staleTurns`=3 后剪除。

## 2026-10-18 · P7-c 坐标基线 + 遍历序确定性（#90，`27a12b2`）

- **新增**：odd-r 坐标基线定稿进设计文档 **§6.5**（格心/换算公式、两张奇偶邻接表、遍历序、五份同基线实现清单、用校验器「ox/oy 中心对齐」判别 odd-r vs axial：`data/gis` 12 包全 PASS、`data/gis_axial_backup` 11 包全 FAIL Δlon 0.11–0.50）。
- **修复**：`Hex.Range` 由 `IEnumerable` 的 `dq` 主序改为 `IReadOnlyList` 按 **(距离, q, r)** 定序——这是 P4 遗留 AI 战线震荡红灯（#94）的真根因，红灯随本次改动转绿；新增 2 条几何用例钉住分层与同环序。Dart `hexRing` 零调用，按"不复刻死代码"基准未移植。
- **验证**：**新基线 241/241 全绿**（引擎 201 + 协议 40，各连跑三次）。

## 2026-10-18 · P7-b 协议侧回归（#89）

- **新增**：`smoke_ws.dart` 32 项 + `ws-smoke.mjs` 独有闸门搬进 `dotnet test`——`tests/HexFront.Tests/Protocol/`（6 个测试类 + ApiFixture/WsClient/RoomSessions/DirectorCmdHelper 地基，40 用例），对照表在 `tests/PARITY-CHECKS.md` §10（含 8c/8d 为何不整条搬）。
- **验证**：当时基线 239 总计 / 238 通过 / 1 红灯（`AiOfficerP3Tests`，即 #94，后由 #90 转绿）；协议侧 `--filter "FullyQualifiedName~Protocol"` 连跑三次 40/40。**生产代码零改动**。
- **顺带查明的线上事实**：`/api/validate` 的 `errors` 是字符串数组；`saved.turn` 是 `meta.last_turn`（已结算回合）而非房间 `turn`；fresh 房间 `frames` 首包是空数组。

## 2026-10-18 · P7-a 引擎侧等价测试（#88，`08d98d3`）

- **新增**：Dart `verify.dart` 61 项 → xUnit 等价实现，逐项映射表落在 `server/tests/PARITY-CHECKS.md`；补齐地物、几何、要点、连续推演用例。
- **意义**：从此改引擎有回归网，"能编译/能推进回合"不再被当成"语义对等"。

## 2026-10-18 · P6 协议与视图细节对等（#67，`#84–#87`）

- **新增/修复**：`welcome` 帧必须带 `view` + `room`（C# 曾缺，导致首包要等第二帧）；顶号分档——红/蓝阵营位要同 token 或 `force=1`，导演部/观战后来者直接接管；`publicInfo.roles` 计数恒为 0/1 是 `RoomService.Slots` 的既定语义（`role→单条连接`），不是 bug；回放帧 **200 帧上限** + 每帧恒带 `battles` 数组，且 `frame.turn` 才是被结算的那一回合（UI 标注用它，否则滑杆与 HUD 差一回合）。
- **前端**：React 草案改为**本地镜像优先**（点击即上屏，回合推进才用服务端回抄）；令牌存 `localStorage hf.tok.<role>`，刷新可复用会话；重连 8 次封顶后停在提示态并写明原因。
- **拍板**：SignalR `RoomHub` **删除**，会话传输保持原生 `/ws`，偏离说明记进 §13.2。

## 2026-10-18 · P5 React 地图渲染补齐（#66，`#79–#83`，`cd5eb31`/`cc0735d`）

- **新增**：几何与相机（`view.geo` 驱动 + DPR/ResizeObserver + 拖拽/缩放/fit + 军用配色含 `void`）；天地图 WMTS 在线底图（退避闸门 + 署名）；地物连线与符号、要点归属虚线圈、常驻图例、悬停格信息；兵力块（阵营色圆角块 + 兵种文字 + HQ 金环 + 机降三角 + E 档透明度 + 陈旧虚线 + 极小缩放降级）；交互接线（草案路径、射程预览、三视图）。
- **修复**：canvas 点击坐标必须乘 `canvas.width / rect.width`（CSS 拉伸）；内置浏览器面板隐藏时后台标签 `requestAnimationFrame` 被冻结，看起来像"底图坏了"——取证口径改为无头 Chrome + CDP。

## 2026-10-18 · P4 GIS 契约对齐 + 现场生成（#65，`#74–#78`，`8618284`/`8fd7ac7`/`7e85f34`/`9e45254`）

- **新增**：GIS 路由与契约全量 snake_case（`capabilities`/`plan`/`jobs`/`packages`/`validate`，GET+POST 同结果，无 `cell_km` 时显式 `current: null`，作业产物 `<spec.name>_<epoch>.json`）；`/api/validate` 出真 `type_points` + `unit_type_defs` 覆盖进引擎并自动计价；React 向导第 2 步现场生成六角格（plan 阶梯 → 作业轮询 → 直接进房）。
- **数据**：`data/gis` 的包按 odd-r 中心公式原地重采样重生成，axial 原件整目录备份在 `data/gis_axial_backup`（`7e85f34`）。今天盘上两目录**同名成对的是 11 个**，`data/gis` 另有 1 个 P4 之后现场生成跑出来的 `gis_1792288947.json` 无 axial 对应，故当前共 12 个；提交信息里写的"10 个包"是当时的口径，以盘上配对为准。校验器加第 38 项「ox/oy 中心对齐」。
- **验证**：当时基线 **177/178**，唯一红灯是 odd-r 数据暴露的 AI 战线震荡——后于 #90 结案，真根因是 `Range` 取用序而非 AI 逻辑。
- **安全**：天地图密钥 `data/tianditu.json` 退出版本库（`.gitignore` 挡住，仓库只留 `data/tianditu.example.json` 占位），并修好服务端一直读不到的路径（`a51ffba`）。**密钥一旦进过历史只能去控制台重置，删文件收不回历史。**

## 2026-10-18 · P3 结算分支补全与 AI 对等（#64，`#69–#73`，`4fbf025`）

- **新增/修复**：补给与电网算法对齐 `MechanicsService`；`CommandValidator` 校验对齐；`TurnResolver` 七阶段（Phase 0 校验 + ①空域 ②观测 ③补给 ④机动 ④b 遭遇战 ⑤战斗 ⑥恢复 ⑦状态推进）分支补全；**AI 副官重写**为只用 `FogService` 同源情报（兵种/类型要 `track.Level >= 2`，不是常量 `ExposureE2=6`）、指令先过校验器再入库、绝不碰 `g.Rng`。
- **修复（只有浏览器 E2E 才暴露）**：`CommandWire` 写 `zone` 漏了 `WritePropertyName` → 整行 `turns.jsonl` 变非法 JSON 被静默丢弃，**一条空袭令就能让一回合凭空消失**；React `HexMap` 对有单位的格只回调 `onUnitClick`，导致就地指令（构筑工事/休整/停止）在 UI 上永远下不出去。
- **推论已固化进验收口径**：任何写进存档或线上的数组/对象都要有"落盘再读回"的断言；回合推进必须断言坐标变化，不能只看 turn。
- **验证**：当时基线 xUnit 156/156；ws-smoke 含 8d 双 AI 自动对抗（同种子两局终局指纹一致，14 回合 223ms）。

## 2026-10-17~18 · P0–P2 阻断缺陷、确定性内核、战力链（#61–#63）

- **P2**（`#63`）：CF 战力链按设计文档 §5 重写为 **11 因子**（`Services/CombatChain.cs`）并输出 `cf`，不再用简化公式。
- **P1**：种子 RNG 进引擎 + 存档往返——`turns.jsonl` 记 `rng_at_resolve`，尾部重放指纹一致；此前"存档能读回"并不等于"能重放出同一局"。
- **P0**（`2d1915e`）：6 项阻断级与公平性修复——回放帧鉴权、未知房间码返回 **404**（不再静默建房）、迷雾档案泄漏、机动预算等。
- **同批**：全栈统一 odd-r 偏移砖墙棋盘坐标（`933dc4d`）。

## 2026-10-17 · 骨架期（迁移前置）

- **后端**：Dart 引擎语义 + Python GIS 链路搬进 `HexFront.Core`/`HexFront.Api`；Program.cs 接 `AddSignalR` + `UseWebSockets` + 原生 `/ws`（单例 `RoomService`，按 `web/js/ws.js` 协议）；`GameController` 实现 7 个 REST 端点并与 `/ws` 共用同一份房间真相；`LibraryService` 按 `library.dart` 1:1 全量移植（STANDARD_TERRAIN 10 项 / UNIT_TYPES 18 项 / WEATHER 7 项 / DEFAULT_RULES）；`FogService` 按 `fog.dart` 重写三视图（SideView 用 `own`+`contacts`，**side 视图没有 `view.units`**）；ready 自动结算屏障 + `director_cmd` 全量对齐 + `clear_orders`。
- **两处曾静默致命的缺陷**：`RoomService.HandleAsync` 无 finally 注销 → 对端进程被杀时角色位永久占死；`TurnResolver.MovementPhase` 把整个 MP 预算 `Clamp(budget*hq,0.6,1.15)` → **所有机动静默失败且无报错**。
- **前端**：`webfront/` 建立并全面去 mock（`room/protocol.ts`、`RoomSocket.tsx`、`useUnitFull.ts`、`services/api.ts` 对齐真实信封，vite 代理 `/api`+`/ws` 到 5018），五页 + 四终端浏览器 E2E 跑通。
- **仓库**：`.gitignore` 收敛编译产物规则并停止跟踪运行期存档（`763bb23`/`bce2e1a`）。
