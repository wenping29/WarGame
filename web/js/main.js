// SPA 引导：按哈希路由挂载视图，负责上一视图控制器销毁（关 socket）。
import { renderHome } from './views/home.js';
import { renderLobby } from './views/lobby.js';
import { renderWizard } from './views/wizard.js';
import { renderSide } from './views/side.js';
import { renderDirector } from './views/director.js';
import { renderSpectator } from './views/spectator.js';
import { router, getMeta } from './router.js';
import { configureTiles } from './map.js';

const app = document.getElementById('app');
const conn = document.getElementById('conn');
let destroy = null;

function setActiveNav(name) {
  const cur = '#' + (name === 'home' ? '/' : '/' + name);
  document.querySelectorAll('#nav a').forEach((a) => a.classList.toggle('on', a.getAttribute('href') === cur));
}

async function dispatch() {
  if (typeof destroy === 'function') { try { destroy(); } catch (_) {} destroy = null; }
  conn.className = 'conn';
  conn.textContent = '未连接';
  const r = router.parse();
  setActiveNav(r.name);
  // 视图里的 MapView 在构造时读密钥，所以挂载前必须先配好（getMeta 有缓存，不重复请求）
  try { configureTiles((await getMeta()).tiles?.tk); } catch (_) {}
  try {
    if (r.name === 'new') destroy = await renderWizard(app);
    else if (r.name === 'room') destroy = await mountRoom(r.params.code, r.params.role);
    else if (r.name === 'home') destroy = await renderHome(app);
    else destroy = await renderLobby(app);
  } catch (e) {
    console.error(e);
    app.textContent = '页面错误：' + e.message;
  }
}

async function mountRoom(code, role) {
  if (role === 'director') return renderDirector(app, code, conn);
  if (role === 'spectator') return renderSpectator(app, code, conn);
  if (role === 'red' || role === 'blue') return renderSide(app, code, role, conn);
  // 未知角色 → 当作观战
  return renderSpectator(app, code, conn);
}

window.addEventListener('hashchange', dispatch);
dispatch();
