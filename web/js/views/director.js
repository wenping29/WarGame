// 导演部：全知视图 + 双方已受领指令 + 就绪态 + 导调动作（天气/AI/迷雾/全知/注入情报/跳回合）+ 强制结算/存档。
import { el, toast, clear, hhmm } from '../util.js';
import { getMeta } from '../router.js';
import { RoomSocket } from '../ws.js';
import { MapView, attachBasemapControl } from '../map.js';

export async function renderDirector(root, code, connEl) {
  clear(root);
  const meta = await getMeta();
  const typeMap = {}; meta.unit_types.forEach((t) => typeMap[t.id] = t);

  let view = null;
  let logs = [];
  let inject = { side: 'red', unit: '' };
  let newWeather = '';
  let jumpTurn = 1;

  const layout = el('div', { class: 'terminal', style: 'grid-template-columns:minmax(240px,300px) minmax(420px,1fr) minmax(260px,320px)' }, [
    el('div', { class: 'col' }, [panel('全局兵力', 'unitList', 'grow')]),
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
      el('div', { class: 'legend', id: 'legend' }),
    ]),
    el('div', { class: 'col' }, [
      panel('导调控制', 'controls'),
      panel('双方已受领指令', 'ordersBox'),
      panel('事件日志', 'eventLog', 'grow'),
    ]),
  ]);
  root.appendChild(layout);

  const map = new MapView(root.querySelector('#map'));
  attachBasemapControl(root.querySelector('.mapzoom'), map);
  map.onPick(({ q, r }) => { const u = (view?.units || []).find((x) => x.q === q && x.r === r); if (u) { inject.unit = u.id; renderControls(); } });
  const ro = new ResizeObserver(() => map.resize()); ro.observe(root.querySelector('.mapwrap'));

  function refresh() { renderHud(); renderUnits(); renderControls(); renderOrders(); }

  function renderHud() {
    const hud = root.querySelector('#hud');
    if (!view) { hud.textContent = '连接中…'; return; }
    hud.innerHTML = `<b>${view.name || ''}</b> · 导演部 · 回合 ${view.turn}/${view.max_turns} · ${hhmm(view.hour)} ${view.day?.label || ''} · ${view.weather?.label || ''} · 迷雾 ${view.flags?.fog ? '开' : '关'}${view.over ? ' · <b>已结束 ' + (view.winner || '') + '</b>' : ''}`;
  }

  function renderUnits() {
    const box = root.querySelector('#unitList');
    const list = view?.units || [];
    const red = list.filter((u) => u.side === 'red'), blue = list.filter((u) => u.side === 'blue');
    box.replaceChildren(
      el('div', { class: 'mini' }, `红 ${red.length} · 蓝 ${blue.length} · 合计 ${list.length}`),
      ...list.map((u) => el('div', { class: 'unit' + (inject.unit === u.id ? ' sel' : ''), onclick: () => { inject.unit = u.id; renderControls(); renderUnits(); } }, [
        el('div', { class: 'hd' }, [el('span', { class: 'id' }, u.id), el('span', { class: 'tag ' + u.side }, u.side === 'red' ? '红' : u.side === 'blue' ? '蓝' : u.side)]),
        el('div', { class: 'meta' }, [el('span', {}, u.type_label), el('span', {}, u.personnel + '人'), el('span', {}, '士气' + u.morale), el('span', {}, u.q + ',' + u.r), el('span', {}, '弹' + u.ammo + ' 油' + u.fuel), u.supply < 0.5 ? el('span', { class: 'tag red' }, '断供') : null, u.status === 'routed' ? el('span', { class: 'tag red' }, '溃退') : null]),
      ])),
    );
  }

  function renderControls() {
    const box = root.querySelector('#controls');
    const ai = view?.ai || { red: false, blue: false };
    const rdy = view?.ready || {};
    box.replaceChildren(
      el('div', { class: 'toolbar' }, [
        el('button', { class: 'btn btn-primary', onclick: () => sock.send({ type: 'resolve' }) }, '强制结算'),
        el('button', { class: 'btn', onclick: () => sock.send({ type: 'save' }) }, '存档'),
      ]),
      row([el('label', {}, [el('span', {}, '改天气'), weatherSelect()]), el('button', { class: 'btn btn-sm', onclick: () => send({ action: 'weather', id: newWeather }) }, '设定')]),
      el('div', { class: 'toolbar' }, [
        toggle('战争迷雾', view?.flags?.fog === true, (on) => send({ action: 'fog', on })),
        toggle('观战全知', view?.spectator_omniscient === true, (on) => send({ action: 'spectator_omniscient', on })),
      ]),
      el('div', { class: 'toolbar' }, [
        toggle('红方 AI 代管', ai.red === true, (on) => send({ action: 'ai', side: 'red', on })),
        toggle('蓝方 AI 代管', ai.blue === true, (on) => send({ action: 'ai', side: 'blue', on })),
      ]),
      el('div', { class: 'mini', style: 'margin:4px 0' }, `就绪：红 ${rdy.red ? '✓' : '✗'} · 蓝 ${rdy.blue ? '✓' : '✗'}`),
      el('h3', { style: 'margin-top:8px' }, '注入情报'),
      el('div', { class: 'row' }, [
        el('select', { onchange: (e) => inject.side = e.target.value }, [el('option', { value: 'red' }, '向红方'), el('option', { value: 'blue', selected: inject.side === 'blue' ? 'selected' : null }, '向蓝方')]),
        el('input', { value: inject.unit, placeholder: '目标单位 id', oninput: (e) => inject.unit = e.target.value, style: 'width:110px' }),
        el('button', { class: 'btn btn-sm', onclick: () => send({ action: 'inject_intel', side: inject.side, unit: inject.unit }) }, '暴露'),
      ]),
      el('div', { class: 'mini' }, '提示：点选地图单位即填入目标 id'),
      el('h3', { style: 'margin-top:8px' }, '跳回合（引擎暂未开放回跳）'),
      el('div', { class: 'row' }, [el('input', { type: 'number', value: jumpTurn, min: 1, oninput: (e) => jumpTurn = +e.target.value, style: 'width:80px' }), el('button', { class: 'btn btn-sm', onclick: () => send({ action: 'jump_turn', turn: jumpTurn }) }, '跳转')]),
    );
  }

  function weatherSelect() {
    return el('select', { onchange: (e) => newWeather = e.target.value },
      (meta.weather || []).map((w) => el('option', { value: w.id, selected: w.id === (view?.weather?.id) ? 'selected' : null }, w.label)));
  }
  function toggle(label, on, cb) {
    return el('button', { class: 'btn btn-sm' + (on ? ' btn-primary' : ''), onclick: () => cb(!on) }, (on ? '● ' : '○ ') + label);
  }
  function send(o) { sock.send({ type: 'director_cmd', ...o }); }
  function row(children) { return el('div', { class: 'row' }, children); }

  function renderOrders() {
    const box = root.querySelector('#ordersBox');
    const ord = view?.orders || { red: [], blue: [] };
    const block = (side) => {
      const list = ord[side] || [];
      return el('div', {}, [
        el('h3', {}, (side === 'red' ? '红方' : '蓝方') + ` 指令 (${list.length})`),
        list.length ? list.map((c) => el('div', { class: 'ordbox' }, el('div', { class: 'o' }, [el('span', { class: 'mono' }, c.unit), el('span', {}, desc(c))]))) : el('div', { class: 'muted small' }, '（无）'),
      ]);
    };
    box.replaceChildren(block('red'), block('blue'));
    if (view?.last_errors) {
      const errs = Object.entries(view.last_errors).filter(([, v]) => v && v.length);
      if (errs.length) {
        const rows = errs.flatMap(([s, es]) => es.map((e) => {
          let o = {}; try { o = JSON.parse(e); } catch (_) { o = { message: e }; }
          return el('div', { class: 'errrow' }, '[' + s + '] ' + o.message);
        }));
        box.appendChild(el('div', {}, [el('h3', {}, '上轮被拒'), ...rows]));
      }
    }
  }
  function desc(c) {
    if (c.op === 'move') return '机动 ' + (c.path?.length ? c.path[c.path.length - 1].join(',') : '?');
    if (c.op === 'attack') return '突击 ' + c.target;
    if (c.op === 'fire') return '炮击 ' + (c.zone?.join(',') || '?');
    if (c.op === 'air_mission') return '空任务 ' + c.mission;
    return { dig: '构筑工事', rest: '休整', halt: '停止' }[c.op] || c.op;
  }

  function renderEvents() {
    const box = root.querySelector('#eventLog');
    box.replaceChildren(...logs.slice(-120).reverse().map((e) => el('div', { class: 'ev ' + e.code }, [el('span', { class: 't' }, 'T' + e.turn), e.text])));
  }

  root.querySelector('#legend').replaceChildren(...[['红方', '#c15b4e'], ['蓝方', '#4e82b3'], ['指挥所', '#f0d9a5']].map(([l, c]) => el('span', {}, el('i', { style: 'background:' + c }), ' ' + l)));

  const sock = new RoomSocket(code, 'director').connect();
  sock.on('welcome', (m) => { if (connEl) { connEl.className = 'conn live'; connEl.textContent = '已连接 · 导演部 · 房间 ' + code; } if (m.view) onView(m.view); });
  sock.on('state', (m) => onView(m.view));
  sock.on('events', (m) => { logs = logs.concat(m.events || []); renderEvents(); });
  sock.on('saved', (m) => toast(`已存档 回合${m.turn} 副本${m.instance}`));
  sock.on('error', (m) => toast(m.message, 'err'));
  sock.on('close', (m) => { if (connEl) { connEl.className = 'conn dead'; connEl.textContent = m?.reason || '连接中断，重连中…'; } });
  sock.on('kicked', () => { if (connEl) { connEl.className = 'conn dead'; connEl.textContent = '本角色已在其他终端打开'; } });
  sock.on('open', () => { if (connEl) { connEl.className = 'conn live'; connEl.textContent = '已连接 · 导演部 · 房间 ' + code; } });

  function onView(v) {
    if (!v) return;
    view = v;
    map.setView(v);
    refresh(); renderEvents();
  }

  return () => { sock.close(); ro.disconnect(); map.dispose(); };
}

function panel(title, id, extra = '') {
  return el('div', { class: 'panel ' + extra }, [el('h3', {}, title), el('div', { class: 'scroll', id })]);
}
