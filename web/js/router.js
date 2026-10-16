// 哈希路由与共享元数据缓存
import { api } from './api.js';

export const router = {
  current: null, // {name, params}
  parse() {
    const h = location.hash.replace(/^#/, '') || '/home';
    const parts = h.split('/').filter(Boolean);
    if (parts[0] === 'room' && parts.length >= 3) return { name: 'room', params: { code: parts[1].toUpperCase(), role: parts[2].toLowerCase() } };
    if (parts[0] === 'new') return { name: 'new', params: {} };
    if (parts[0] === 'lobby') return { name: 'lobby', params: {} };
    return { name: 'home', params: {} };
  },
  go(hash) {
    if (location.hash === hash) window.dispatchEvent(new HashChangeEvent('hashchange'));
    else location.hash = hash;
  },
  link(code, role) { return `#/room/${encodeURIComponent(code)}/${role}`; },
};

// 元数据（剧本/GIS/天气/兵种/地形/规则）只取一次，缓存在内存
const metaCache = { value: null, loading: null };
export function getMeta(force = false) {
  if (metaCache.value && !force) return Promise.resolve(metaCache.value);
  if (metaCache.loading && !force) return metaCache.loading;
  metaCache.loading = api.scenarios().then((m) => { metaCache.value = m; metaCache.loading = null; return m; });
  return metaCache.loading;
}

// 房间占用令牌：加入已被占用的角色需要 force=1（服务端 409）
export function connStatusClass(state) {
  return state === 'live' ? 'conn live' : state === 'dead' ? 'conn dead' : 'conn';
}
