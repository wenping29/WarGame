/**
 * 天地图（Tianditu）密钥来源集中管理。
 *
 * ## 为什么前端也需要一把 key
 *
 * 底图瓦片是**浏览器直接**向 `t0~t7.tianditu.gov.cn` 发起的请求，天地图服务端
 * 按请求的 `Referer`（即页面所在的域名）校验 token 白名单：域名不在白名单里就
 * 返回 401/403，前端表现为「底图瓦片加载失败」，六角网格不受影响但没有地图。
 *
 * 因此：**按前端域名注册的 token 必须配在前端**，后端下发的 token 只在前端域名
 * 恰好也在其白名单里时才可用。
 *
 * ## 两个来源
 *
 * 1. `VITE_TIANDITU_TK` —— 构建期注入（`.env.development.local` / `.env.production`）。
 *    换前端域名时要改这里并**重新构建**前端。
 * 2. 后端 `/api/scenarios` 下发的 `tiles.tk` —— 运行期可改：改服务端的
 *    `data/tianditu.json` 即生效，**不用重新构建前端**（适合前端域名会变的情况）。
 *
 * 优先级：1 > 2（显式配置优先）。
 */

/** 构建期注入的全部 key（已按逗号拆分、去空、去首尾空白）。 */
export const TIANDITU_ENV_TKS: string[] = (import.meta.env.VITE_TIANDITU_TK ?? '')
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean)

/** 构建期注入的首个 key（无则 null）。 */
export const TIANDITU_ENV_TK: string | null = TIANDITU_ENV_TKS[0] ?? null

/**
 * 解析最终使用的天地图密钥。
 *
 * @param serverTk 后端 `/api/scenarios` 响应里 `tiles.tk` 的值（可为 null / 空串）
 * @returns 密钥字符串；两处都没有时返回 null（前端会提示「未取得天地图密钥」）
 */
export function resolveTiandituTk(serverTk?: string | null): string | null {
  if (TIANDITU_ENV_TK) return TIANDITU_ENV_TK
  const t = (serverTk ?? '').trim()
  return t ? t : null
}
