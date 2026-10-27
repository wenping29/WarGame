/// <reference types="vite/client" />

// 扩展 vite/client 已声明的 ImportMetaEnv（同名 interface 会自动合并，
// 不要重复声明 ImportMeta —— 会与 vite/client 冲突）
interface ImportMetaEnv {
  /**
   * 前端调用后端的完整前缀，必须以 /api 结尾。
   * 方案 B（宝塔双站分离）时填后端站点的绝对地址，如 http://43.142.147.244:7777/api；
   * 不设置则退回同源相对路径 /api（单进程部署）。
   */
  readonly VITE_API_BASE_URL?: string

  /**
   * 天地图密钥（token）。
   *
   * 天地图按**请求的 Referer（页面所在域名）**校验 token 白名单，而底图瓦片是
   * 浏览器直接向 t0~t7.tianditu.gov.cn 请求的 —— 所以按前端域名注册的 token
   * 必须配在前端，后端下发的 token 在域名对不上时会被拒绝（底图加载失败）。
   *
   * 多把 key 用英文逗号分隔（按顺序取用，配额分摊时用）。
   */
  readonly VITE_TIANDITU_TK?: string
}
