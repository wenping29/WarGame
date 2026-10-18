# web/ —— 已冻结的旧版客户端（只读参考）

**这里的东西不再演进。** 唯一主前端是 [`hexfront-react/`](../hexfront-react/)（React 19 + TypeScript + Vite，已接真实 C# 后端并在浏览器里端到端验收）。本目录是迁移起点的原生 ESM 实现（无构建、双击直开），P7 拍板后停止接新需求，**保留作只读参考，不删除**。

拍板记录见设计文档 [`HEXFRONT-WEB产品设计文档.md`](../HEXFRONT-WEB产品设计文档.md) §13.6；迁移过程与两版差异见 [`hexfront-react/MIGRATION.md`](../hexfront-react/MIGRATION.md)。

## 为什么还留着

1. **视觉口径的原始出处**。React 侧的配色变量、面板密度、兵力块画法是从这里搬过去的，两边有争议时以这里 + 设计文档为准。
2. **移植遗漏的对照表**。React 迁移时丢过 CSS 规则（`.scroll`、`.steps/.st`、`.wiz`、`.oob-table`、`#nav a.on` 五组，全部已补回并留了取证），对照只能跟这里比。
3. **几何与渲染算法的第一版口径**：`js/hex.js`（odd-r 轴向换算）、`js/map.js`（画布渲染）。

## 仍在读这个目录的两处脚本（都还能跑）

| 脚本 | 依赖 | 说明 |
|---|---|---|
| [`scripts/hex-offset-check.mjs`](../scripts/hex-offset-check.mjs) | 直接 `import` 了 `web/js/hex.js` | 六角格几何自检（中心/邻居/距离/连线）。删 `web/` 会连带打断它，这也是保留决定的一部分原因。 |
| [`HexFront.CSharp/scripts/web-static.mjs`](../HexFront.CSharp/scripts/web-static.mjs) | `ROOT = ../../web`，默认 8080 端口同源代理到 5018 | 旧客户端的静态托管。要对比新旧两版同一局的渲染时用它。 |

## 目录内容

```
index.html          # 单页入口（hash 路由）
css/app.css         # 199 条规则，视觉 token 与布局口径的原始出处
js/hex.js           # odd-r 六角格几何
js/map.js           # canvas 渲染（底图/地物/兵力块）
js/ws.js  api.js  router.js  main.js  util.js
js/views/            # home / lobby / wizard / side / director / spectator
```

## 不要做的事

- 不要往这里加功能或修 bug——功能一律进 `hexfront-react/`，这里的缺陷由 React 侧修复后不回写。
- 不要把它当交付物：README、设计文档 §12 的运行手册、四条门槛命令都只针对 C# + React。
- 不要删：上面两个脚本还依赖它，且它是 §13.6 移植遗漏对照清单的左半边。
