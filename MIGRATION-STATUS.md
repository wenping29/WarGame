# 迁移状态报告（Dart `hexfront/` + 原生 JS `web/` → C# `HexFront.CSharp/` + React `hexfront-react/`）

最近更新：2026-10-18（P7-e）。**验收数字只看 [HEXFRONT-WEB产品设计文档.md](HEXFRONT-WEB产品设计文档.md) §13.6 的四条门槛命令**，本文只记"哪些搬完了、哪些还没搬"。逐阶段改了什么（含当时的基线数字与被修掉的静默致命缺陷）见 [CHANGELOG.md](CHANGELOG.md)。

> 本文档 2026-10-17 的初版按"C# 侧还缺 TurnResolver/AiService/ScenarioService/Store/Library、多人联机走 SignalR RoomHub"写过一版状态表，那份已经整体过期并被重写。若你在别处看到对它的引用，一律以本文与设计文档 §13 为准。

## 阶段进度

| 阶段 | 任务 | 内容 | 状态 |
|---|---|---|---|
| P0 | #61 | 阻断级与公平性修复（6 项） | ✅ 已交付并确认 |
| P1 | #62 | 确定性内核：种子 RNG 与存档往返 | ✅ 已交付并确认 |
| P2 | #63 | CF 战力链按设计文档重写（11 因子） | ✅ 已交付并确认 |
| P3 | #64 | 结算分支补全 + 补给/电网/胜利 + AI 只用迷雾情报 | ✅ 已交付并确认 |
| P4 | #65 | GIS 契约对齐 + React 向导现场生成 + `data/gis` 按 odd-r 重生成 | ✅ 已交付并确认 |
| P5 | #66 | React 地图渲染补齐（底图/缩放/地物/交互/兵力块） | ✅ 已交付并确认 |
| P6 | #67 | 协议与视图细节对等 + SignalR 死代码处置 | ✅ 已交付并确认 |
| P7 | #68 | 回归测试地基与交付收尾 | 🟡 进行中（见下） |

## P7 子任务

| 子任务 | 内容 | 状态 |
|---|---|---|
| #88 P7-a | `verify.dart` 61 项 → xUnit 等价实现，映射表 `HexFront.CSharp/tests/PARITY-CHECKS.md` | ✅ |
| #89 P7-b | `smoke_ws.dart` 32 项 → 协议侧 xUnit 40 项 + `scripts/ws-smoke.mjs` 增补 | ✅ |
| #90 P7-c | odd-r 坐标基线文档化 + `hexRing`/`Range` 排序确定性（顺带结掉 AI 战线震荡红灯 #94） | ✅ |
| #91 P7-d | 交付文档一致性 + 本文件处置 + 浏览器取证（`gone` 情报卡、三视图面板滚动） | ✅ |
| #92 P7-e | `web/` 处置：**拍板保留为只读参考（不删）**，冻结声明落 `web/README.md`；正反双向 CSS 差分收口"移植遗漏对照清单"（补回 `.steps/.st`、`.wiz/.body/.side`、`.oob-table`、`.nav a.on` 四组）+ 登记已知残留 | 🟡 本轮交付待确认 |
| #93 P7-f | 性能基线：14 回合全 AI < 5s + 地图 60fps | ⬜ |

## C# 后端

引擎与会话全部在 `HexFront.Core` / `HexFront.Api` 下落地，模块对照见设计文档 §13.2 的移植清单。要点：

- 会话传输是**原生 WebSocket `/ws`**，不是 SignalR。`RoomHub` 已删除（2026-10-18 拍板），房间真相只有 `RoomService` 一份，理由与偏离说明记在 §13.2。
- REST 响应一律 snake_case，与 Dart/web 线上格式逐字一致。
- 回合裁决是 `TurnResolver` 的 Phase 0 校验 + ①–⑦（遭遇战 4b），文档口径仍是"七阶段"（§8）。
- 真实栅格六角格生成**仍然调用 `tools/gis/hexgen.py` 子进程**（Python + GDAL/rasterio）；没有 Python 时降级为 `HexGenService` 原生合成地形并在 `/api/gis/capabilities` 与作业日志里如实上报。`HexFront.Core.csproj` 里的 `NetTopologySuite` / `ProjNET` 两个包引用**代码零使用**，是 §13.1 原计划的残留（#92 拍板：不删，登记为已知残留）。

## React 前端

`hexfront-react/` 是**唯一主前端**，已接真实后端并浏览器端到端验证。技术选型实收与原建议的差别（React 19 而非 18、路径路由而非 hash、没有 AntD、没有 Konva、**没有 Zustand store**——`zustand` 依赖仍在 `package.json` 里但零引用）记在 §13.3。#92 又收掉一批"组件用了 web 的类名但 CSS 没搬"的缺陷，并把 `App.css` 里 58 个从未在 `src/` 出现的类名登记为死样式（只标注不删）；对照清单在 §13.6，前端侧的坑同步写在 [hexfront-react/MIGRATION.md](hexfront-react/MIGRATION.md)。

## 未开工（不在本次迁移范围）

账号与权限体系（仍是房间码 + 令牌，§11.2 / §13.5-2）、移动端适配、对外部署与测绘审图号、AI 歼灭节奏调参（§11.12）、`.gitattributes` + Git LFS（§13.5-4）。
