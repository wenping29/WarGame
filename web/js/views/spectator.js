// 观战：默认观战情报层（双方暴露并集 + summary）；导演部开启全知后同一 socket
// 会收到 kind:'director' 视图——UI 按 view.kind 自适应，而非假设。含回放控制（frames）。
import { el, toast, clear, hhmm } from '../util.js';
import { RoomSocket } from '../ws.js';
import { MapView, attachBasemapControl } from '../map.js';

export async function renderSpectator(root, code, connEl) {
  clear(root);
  let view = null;         // 实时视图
  let logs = [];
  let frames = null;       // 回放帧
  let replayIdx = -1;      // -1 = 实时
  let replayView = null;

  const layout = el('div', { class: 'terminal', style: 'grid-template-columns:1fr minmax(280px,340px)' }, [
    el('div', { class: 'col' }, [
      el('div', { class: 'mapwrap' }, [
        el('canvas', { id: 'map' }),
        el('div', { class: 'maphud', id: 'hud' }),
        el('div', { class: 'mapzoom' }, [
          el('button', { onclick: () => map.zoomBy(1.3) }, '+'),
          el('button', { onclick: () => map.zoomBy(1 / 1.3) }, '−'),
          el('button', { onclick: () => { map.fit(); map.requestDraw(); } }, '⤢'),
        ]),
      ]),
      el('div', { class: 'scrub', id: 'scrub' }),
    ]),
    el('div', { class: 'col' }, [
      panel('态势', 'summaryBox'),
      panel('公开接触', 'contactList'),
      panel('事件日志', 'eventLog', 'grow'),
    ]),
  ]);
  root.appendChild(layout);

  const map = new MapView(root.querySelector('#map'));
  attachBasemapControl(root.querySelector('.mapzoom'), map);
  const ro = new ResizeObserver(() => map.resize()); ro.observe(root.querySelector('.mapwrap'));

  function currentView() { return replayIdx >= 0 ? replayView : view; }

  function refresh() {
    const v = currentView();
    renderHud(v);
    renderSummary(v);
    renderContacts(v);
    renderScrub();
  }

  function renderHud(v) {
    const hud = root.querySelector('#hud');
    if (!v) { hud.textContent = '连接中…'; return; }
    const kindLabel = v.kind === 'director' ? '全知（导演部已授予）' : v.kind === 'side' ? '对抗方视图' : '观战情报层';
    const rp = replayIdx >= 0 ? ` · 回放 T${v.turn}` : '';
    hud.innerHTML = `<b>${v.name || ''}</b> · ${kindLabel}${rp} · 回合 ${v.turn}/${v.max_turns} · ${hhmm(v.hour)} ${v.day?.label || ''} · ${v.weather?.label || ''}`;
  }

  function renderSummary(v) {
    const box = root.querySelector('#summaryBox');
    if (!v) { box.textContent = '…'; return; }
    if (v.kind === 'spectator' && v.summary) {
      const s = v.summary;
      box.replaceChildren(el('div', { class: 'grid2' }, [
        kv('红方单位', s.red_units), kv('蓝方单位', s.blue_units),
        kv('红方兵力', s.red_personnel), kv('蓝方兵力', s.blue_personnel),
      ]));
    } else if (v.kind === 'director') {
      const units = v.units || [];
      const live = (side) => units.filter((u) => u.side === side && u.status !== 'gone').length;
      const men = (side) => units.filter((u) => u.side === side).reduce((a, u) => a + (u.personnel || 0), 0);
      box.replaceChildren(el('div', { class: 'grid2' }, [
        kv('红方单位', live('red')), kv('蓝方单位', live('blue')),
        kv('红方兵力', men('red')), kv('蓝方兵力', men('blue')),
        kv('接触目标', (v.contacts || []).length), kv('胜负', v.over ? (v.winner || '结束') : '进行中'),
      ]));
    } else {
      box.textContent = '（该视图无汇总）';
    }
  }

  function renderContacts(v) {
    const box = root.querySelector('#contactList');
    const list = v?.contacts || (v?.kind === 'director' ? [] : []);
    if (!list.length) { box.textContent = v?.kind === 'director' ? '（全知：见地图）' : '（暂无公开接触）'; return; }
    box.replaceChildren(...list.map((c) => el('div', { class: 'unit contact' + (c.stale ? ' unknown' : '') }, [
      el('div', { class: 'hd' }, [el('span', { class: 'id' }, c.id), el('span', { class: 'tag ' + (c.side === 'red' ? 'red' : 'blue') }, (c.side === 'red' ? '红' : '蓝') + ' E' + c.level)]),
      el('div', { class: 'meta' }, [el('span', {}, c.q + ',' + c.r), c.type_label ? el('span', {}, c.type_label) : null, c.strength_band ? el('span', {}, '兵力' + c.strength_band) : null, c.stale ? el('span', { class: 'tag' }, c.known_turns_ago + '回合前') : null]),
    ])));
  }

  function renderScrub() {
    const box = root.querySelector('#scrub');
    if (!frames || !frames.length) {
      box.replaceChildren(
        el('button', { class: 'btn btn-sm', onclick: () => sock.send({ type: 'frames' }) }, '载入回放帧'),
        el('span', { class: 'muted small' }, '（结算产生帧后可回放）'));
      return;
    }
    const slider = el('input', { type: 'range', min: 0, max: frames.length - 1, value: Math.max(0, replayIdx), oninput: (e) => setReplay(+e.target.value) });
    box.replaceChildren(
      el('button', { class: 'btn btn-sm', onclick: () => { setReplay(-1); sock.send({ type: 'state' }); } }, replayIdx >= 0 ? '返回实时' : '刷新'),
      slider,
      el('span', { class: 'mini' }, replayIdx >= 0 ? ('T' + frames[replayIdx].turn) : ('共 ' + frames.length + ' 帧')));
  }

  function setReplay(i) {
    replayIdx = i;
    if (i >= 0) {
      const f = frames[i];
      replayView = f.director; // 回放帧带 director 全知视图
      map.setView(f.director);
      // 回放事件：展示至该回合
      const upto = logs.filter((e) => e.turn <= f.turn);
      renderEvents(upto);
    } else {
      replayView = null;
      if (view) map.setView(view);
      renderEvents(logs);
    }
    refresh();
  }

  function renderEvents(list) {
    const box = root.querySelector('#eventLog');
    box.replaceChildren(...(list || logs).slice(-120).reverse().map((e) => el('div', { class: 'ev ' + e.code }, [el('span', { class: 't' }, 'T' + e.turn), e.text])));
  }

  function kv(k, v) { return el('div', { class: 'budget' }, [el('span', { class: 'muted' }, k), el('b', {}, String(v ?? 0))]); }

  const sock = new RoomSocket(code, 'spectator').connect();
  const setConn = (live, extra) => { if (!connEl) return; connEl.className = 'conn ' + (live ? 'live' : 'dead'); connEl.textContent = (live ? '已连接 · 观战 · 房间 ' + code : (extra || '')); };
  sock.on('welcome', (m) => { setConn(true); if (m.view) onView(m.view); });
  sock.on('state', (m) => onView(m.view));
  sock.on('events', (m) => { logs = logs.concat(m.events || []); if (replayIdx < 0) renderEvents(logs); });
  sock.on('frames', (m) => { frames = m.frames || []; toast('回放帧 ' + frames.length); setReplay(frames.length ? frames.length - 1 : -1); });
  sock.on('error', (m) => toast(m.message, 'err'));
  sock.on('close', (m) => setConn(false, m?.reason || '连接中断，重连中…'));
  sock.on('kicked', () => setConn(false, '本角色已在其他终端打开'));
  sock.on('open', () => setConn(true));

  function onView(v) {
    if (!v) return;
    view = v;
    if (replayIdx < 0) map.setView(v);
    refresh();
    if (replayIdx < 0) renderEvents(logs);
  }

  return () => { sock.close(); ro.disconnect(); map.dispose(); };
}

function panel(title, id, extra = '') {
  return el('div', { class: 'panel ' + extra }, [el('h3', {}, title), el('div', { class: 'scroll', id })]);
}
