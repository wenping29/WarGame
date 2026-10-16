// 导演部：全知视图 + 双方已受领指令 + 就绪态 + 导调动作（天气/AI/迷雾/全知/注入情报/跳回合）+ 强制结算/存档。
import { el, toast, clear, hhmm } from '../util.js';
import { getMeta } from '../router.js';
import { RoomSocket } from '../ws.js';
import { MapView, attachBasemapControl } from '../map.js';

const CLS_LABEL = { foot: '徒步', motor: '轮式', mech: '装甲', air: '航空', structure: '固定设施' };
export async function renderDirector(root, code, connEl) {
  clear(root);
  const meta = await getMeta();
  const typeMap = {}; meta.unit_types.forEach((t) => typeMap[t.id] = t);

  let view = null;
  let logs = [];
  let inject = { side: 'red', unit: '' };
  let newWeather = '';
  let jumpTurn = 1;

  const layout = el('div', { class: 'terminal', style: 'grid-template-columns:minmax(280px,340px) minmax(420px,1fr) minmax(260px,320px)' }, [
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
    const held = { red: 0, blue: 0 };
    Object.values(view.node_holders || {}).forEach((s) => { if (held[s] != null) held[s]++; });
    const pw = view.power ? ` · 电网 红×${view.power.red} 蓝×${view.power.blue}` : '';
    hud.innerHTML = `<b>${view.name || ''}</b> · 导演部 · 回合 ${view.turn}/${view.max_turns} · ${hhmm(view.hour)} ${view.day?.label || ''} · ${view.weather?.label || ''} · 迷雾 ${view.flags?.fog ? '开' : '关'}` +
      ` · 要点 红 ${held.red}/蓝 ${held.blue}${pw}${view.over ? ' · <b>已结束 ' + (view.winner || '') + '</b>' : ''}`;
  }

  function renderUnits() {
    const box = root.querySelector('#unitList');
    const list = view?.units || [];
    const red = list.filter((u) => u.side === 'red'), blue = list.filter((u) => u.side === 'blue');
    const cfMax = Math.max(0.001, ...list.map(cfIndex));
    const rows = (arr) => arr
      .map((u) => ({ u, cf: cfIndex(u) }))
      .sort((a, b) => b.cf - a.cf)
      .map(({ u, cf }) => unitRow(u, cf, cfMax));
    box.replaceChildren(
      el('div', { class: 'mini' }, [
        el('b', { class: 'nred' }, '红 ' + red.length), ' · ',
        el('b', { class: 'nblue' }, '蓝 ' + blue.length), ' · 合计 ' + list.length,
      ]),
      el('div', { class: 'legend mini' }, [
        ...['foot', 'motor', 'mech', 'air', 'structure'].map((c) => el('span', {}, [el('i', { class: 'cs ' + c }), CLS_LABEL[c]])),
        el('span', {}, '兵种 ｜ 战力条 · 士气点按档位着色 ｜ 悬停看明细'),
      ]),
      ...rows(red), ...rows(blue),
    );
  }

  /// 战力指数：复刻引擎 CF 链中与战场上下文无关的因子（不含地形/工事/天候/空权/相克）
  function cfIndex(u) {
    const t = typeMap[u.type] || {};
    const strength = t.personnel ? u.personnel / t.personnel : 1;
    const pers = 0.75 + 0.0025 * (u.training ?? 0) + 0.0015 * (u.experience ?? 0);
    const mor = Math.max(0.4, Math.min(1.2, 0.5 + (u.morale ?? 0) / 100 * 0.7));
    const sup = Math.max(0.6, Math.min(1, 0.6 + 0.4 * (u.supply ?? 1)));
    const ammo = u.ammo <= 0 ? 0.3 : Math.max(0.3, Math.min(1, 0.5 + 0.5 * u.ammo));
    return (u.firepower ?? 0) * strength * pers * mor * sup * ammo;
  }

  function unitRow(u, cf, cfMax) {
    const t = typeMap[u.type] || {};
    const band = cf >= cfMax * 0.6 ? 'hi' : cf >= cfMax * 0.3 ? 'mid' : 'lo';
    const m = u.morale ?? 0;
    const mband = m >= 70 ? 'hi' : m >= 45 ? 'mid' : m >= 25 ? 'low' : 'crit';
    const al = [];
    if (u.status === 'destroyed') al.push(el('span', { class: 'al bad', title: '状态：已歼灭' }, '亡'));
    if (u.status === 'routed') al.push(el('span', { class: 'al bad', title: '状态：溃退' }, '溃'));
    if ((u.supply ?? 1) < 0.5) al.push(el('span', { class: 'al bad', title: '补给率 ' + u.supply }, '断'));
    else if ((u.ammo ?? 1) < 0.35) al.push(el('span', { class: 'al warn', title: '弹药 ' + u.ammo }, '弹'));
    if ((u.fuel ?? 1) < 0.35) al.push(el('span', { class: 'al warn', title: '油料 ' + u.fuel }, '油'));
    if (u.fort) al.push(el('span', { class: 'al', title: '工事等级 ' + u.fort }, '固' + u.fort));
    if (u.hq) al.push(el('span', { class: 'al hq', title: '指挥所' }, '指'));
    const detail = [
      (u.type_label || u.type) + ' · ' + u.personnel + '/' + (t.personnel ?? '-') + '人',
      '士气 ' + m + ' · 战力 ' + cf.toFixed(1) + '（编制火力 ' + (u.firepower ?? 0) + '）',
      '弹 ' + u.ammo + ' 油 ' + u.fuel + ' 粮 ' + u.food + ' · 补给 ' + u.supply,
      '(' + u.q + ',' + u.r + ') · 训练 ' + u.training + ' 经验 ' + u.experience + (u.commander ? ' · ' + u.commander : ''),
    ].join('\n');
    return el('div', {
      class: 'urow ' + (u.side === 'red' ? 'red' : u.side === 'blue' ? 'blue' : 'neutral') +
        (u.status === 'destroyed' ? ' dead' : '') + (inject.unit === u.id ? ' sel' : ''),
      title: detail,
      onclick: () => { inject.unit = u.id; renderControls(); renderUnits(); },
    }, [
      el('span', { class: 'uid' }, u.id),
      el('span', { class: 'cls ' + (u.cls || 'other') }, (u.type_label || u.type).slice(0, 2)),
      el('span', { class: 'pwr ' + band }, [el('span', { class: 'tk' }, el('i', { style: 'width:' + Math.round(cf / cfMax * 100) + '%' })), el('b', {}, cf.toFixed(1))]),
      el('span', { class: 'mor ' + mband, title: '士气 ' + m }, [el('i', {}), el('span', {}, String(m))]),
      ...al,
    ]);
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

  root.querySelector('#legend').replaceChildren(...[['红方', '#c15b4e'], ['蓝方', '#4e82b3'], ['指挥所', '#f0d9a5']].map(([l, c]) => el('span', {}, [el('i', { style: 'background:' + c }), ' ' + l])));

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
