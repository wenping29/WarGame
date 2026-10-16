// 对抗方战术终端：只显示己方全量 + 敌方暴露分级；点击下达指令，维护本地草案。
import { el, toast, clear, hhmm } from '../util.js';
import { api } from '../api.js';
import { getMeta, router } from '../router.js';
import { RoomSocket } from '../ws.js';
import { MapView, attachBasemapControl } from '../map.js';
import { hexLine } from '../hex.js';

const MODES = [['move', '机动'], ['attack', '突击'], ['fire', '炮击'], ['air', '空任务'], ['dig', '构筑工事'], ['rest', '休整'], ['halt', '停止']];

export async function renderSide(root, code, role, connEl) {
  clear(root);
  const meta = await getMeta();
  const typeMap = {}; meta.unit_types.forEach((t) => typeMap[t.id] = t);
  const join = readJoin(code) || {};

  let view = null;
  let selected = null;   // own unit id
  let mode = 'move';
  let mission = 'patrol';
  let draft = [];        // 本地指令草案
  let logs = [];         // 事件
  let ready = false;

  const layout = el('div', { class: 'terminal' }, [
    el('div', { class: 'col' }, [
      panel('己方部队', 'ownList', 'grow'),
    ]),
    el('div', { class: 'col' }, [
      el('div', { class: 'mapwrap' }, [
        el('canvas', { id: 'map' }),
        el('div', { class: 'maphud', id: 'hud' }),
        el('div', { class: 'mapzoom' }, [
          el('button', { onclick: () => map.zoomBy(1.3) }, '+'),
          el('button', { onclick: () => map.zoomBy(1 / 1.3) }, '−'),
          el('button', { onclick: () => { map.fit(); map.requestDraw(); }, title: '复位' }, '⤢'),
        ]),
      ]),
      el('div', { class: 'toolbar', id: 'modebar' }),
    ]),
    el('div', { class: 'col' }, [
      panel('指令草案', 'draftBox'),
      panel('敌情接触', 'contactList'),
      panel('事件日志', 'eventLog', 'grow'),
    ]),
  ]);
  root.appendChild(layout);

  const map = new MapView(root.querySelector('#map'));
  attachBasemapControl(root.querySelector('.mapzoom'), map);
  map.onPick(onPick);
  const ro = new ResizeObserver(() => map.resize());
  ro.observe(root.querySelector('.mapwrap'));

  // ── 面板与控件渲染 ──
  const modebar = el('div', { class: 'seg' }, MODES.map(([v, l]) =>
    el('button', { class: mode === v ? 'on' : '', onclick: () => { mode = v; syncModes(); refresh(); } }, l)));
  function syncModes() { [...modebar.children].forEach((b, i) => b.classList.toggle('on', MODES[i][0] === mode)); }

  function refresh() { renderModebar(); renderOwn(); renderDraft(); renderContacts(); renderHud(); }

  function renderModebar() {
    const extra = [];
    if (mode === 'air') {
      extra.push(el('select', { style: 'margin-left:8px', onchange: (e) => mission = e.target.value },
        [['patrol', '战斗巡逻'], ['strike', '对地突击'], ['recon', '侦察'], ['none', '返回地面']].map(([v, l]) =>
          el('option', { value: v, selected: v === mission ? 'selected' : null }, l))));
    }
    extra.push(el('span', { class: 'spacer' }));
    if (selected) extra.push(el('button', { class: 'btn btn-sm', onclick: clearDraft }, '清空草案'));
    if (view) extra.push(readyButtons());
    root.querySelector('#modebar').replaceChildren(modebar, ...extra);
  }

  function renderHud() {
    const hud = root.querySelector('#hud');
    if (!view) { hud.textContent = '连接中…'; return; }
    const w = view.weather ? view.weather.label : '';
    // 要点归属只披露己方（视图裁剪过），目标取自 victory 里的 kind=nodes
    const held = Object.keys(view.node_holders || {}).length;
    const goal = (view.victory || []).find((v) => v.kind === 'nodes' && v.side === view.side);
    const nodes = held ? ` · 掌握要点 ${held}${goal && goal.amount ? '/' + goal.amount : ''}` : '';
    hud.innerHTML = `<b>${view.name || ''}</b> · ${view.side === 'red' ? '红方' : '蓝方'} · 回合 ${view.turn}/${view.max_turns} · ${hhmm(view.hour)} ${view.day?.label || ''} · ${w} · 迷雾 ${view.fog ? '开' : '关'}${nodes}`;
  }

  function renderOwn() {
    const box = root.querySelector('#ownList');
    const list = (view?.own) || [];
    if (!list.length) { box.textContent = '（等待视图…）'; return; }
    box.replaceChildren(...list.map((u) => ownCard(u)));
  }

  function ownCard(u) {
    const err = (view?.errors || []).find((e) => e.unit === u.id);
    const t = typeMap[u.type] || {};
    const card = el('div', { class: 'unit' + (selected === u.id ? ' sel' : ''), onclick: () => { selected = u.id; refresh(); updateMapSel(); } }, [
      el('div', { class: 'hd' }, [
        el('span', { class: 'id' }, u.id),
        el('span', {}, [statusTag(u), el('span', { class: 'tag' }, u.type_label || u.type)]),
      ]),
      bar('士气', u.morale, '#7fb069'),
      bar('人员', pct(u.personnel, t.personnel), '#4e82b3'),
      bar('弹药', u.ammo * 100, '#d8a24a'),
      el('div', { class: 'meta' }, [el('span', {}, '补给 ' + u.supply), el('span', {}, u.q + ',' + u.r), el('span', {}, '疲劳 ' + u.fatigue), u.hq ? el('span', { class: 'tag' }, '指挥所') : null, u.fort ? el('span', { class: 'tag' }, '工事' + u.fort) : null]),
      err ? el('div', { class: 'errrow' }, '⚠ ' + err.message) : null,
      selected === u.id ? actionButtons(u) : null,
    ]);
    return card;
  }

  function actionButtons(u) {
    return el('div', { class: 'toolbar', style: 'margin-top:6px' }, [
      el('span', { class: 'mini' }, '选中·' + MODES.find((m) => m[0] === mode)?.[1]),
      ['dig', 'rest', 'halt'].includes(mode) ? el('button', { class: 'btn btn-sm', onclick: () => addDraft({ op: mode, unit: u.id }) }, '就地执行') : el('span', { class: 'muted small' }, '点击地图目标格下达'),
    ]);
  }

  function bar(label, v, color) {
    const val = Math.max(0, Math.min(100, v || 0));
    return el('div', { class: 'bar' }, el('i', { style: `width:${val}%; background:${color}` }));
  }
  function statusTag(u) {
    const s = { routed: ['溃退', 'red'], damaged: ['受损', ''], supply_low: ['缺补给', ''] }[u.status];
    return s ? el('span', { class: 'tag ' + s[1] }, s[0]) : el('span', {});
  }

  function renderContacts() {
    const box = root.querySelector('#contactList');
    const list = (view?.contacts) || [];
    if (!list.length) { box.textContent = '（无敌情接触）'; return; }
    box.replaceChildren(...list.map((c) => el('div', { class: 'unit contact' + (c.level === 0 || c.stale ? ' unknown' : '') }, [
      el('div', { class: 'hd' }, [el('span', { class: 'id' }, c.id), el('span', { class: 'tag red' }, 'E' + c.level)]),
      el('div', { class: 'meta' }, tierLine(c)),
    ])));
  }
  function tierLine(c) {
    const out = [el('span', {}, c.q + ',' + c.r)];
    if (c.strength_band) out.push(el('span', {}, '兵力' + c.strength_band));
    if (c.type_label) out.push(el('span', {}, c.type_label));
    if (c.fort) out.push(el('span', {}, '工事' + c.fort));
    if (c.status) out.push(el('span', {}, String(c.status)));
    if (c.personnel != null) out.push(el('span', {}, c.personnel + '人'));
    if (c.commander) out.push(el('span', {}, '指挥 ' + c.commander.name));
    if (c.stale) out.push(el('span', { class: 'tag' }, c.known_turns_ago + '回合前'));
    return out;
  }

  function renderDraft() {
    const box = root.querySelector('#draftBox');
    const items = [...draft.map((c) => draftRow(c, true)),
      ...((view?.draft?.length && !draft.length) ? [el('div', { class: 'muted small' }, '（服务端草案与本地一致）')] : [])];
    box.replaceChildren(...(draft.length ? items : [el('div', { class: 'muted small' }, '（无草案：选中单位后在地图点击目标）')]));
    if (view?.errors?.length) {
      const rows = view.errors.map((e) => el('div', { class: 'errrow' }, [el('code', {}, e.reason), ' ', e.message, el('span', { class: 'muted' }, '(' + e.unit + ')')]));
      box.appendChild(el('div', { style: 'margin-top:6px' }, [el('h3', {}, '被拒指令'), ...rows]));
    }
  }
  function draftRow(c, removable) {
    return el('div', { class: 'draft-item' }, [
      el('span', { class: 'mono' }, c.unit),
      el('span', {}, describe(c)),
      removable ? el('span', { class: 'x', onclick: () => { draft = draft.filter((d) => d !== c); ready = false; submit(); } }, '×') : null,
    ]);
  }
  function describe(c) {
    if (c.op === 'move') return '机动→' + (c.path[c.path.length - 1] || []).join(',') + '（' + (c.path.length - 1) + '格）';
    if (c.op === 'attack') return '突击 ' + c.target;
    if (c.op === 'fire') return '炮击 ' + (c.zone || []).join(',');
    if (c.op === 'air_mission') return '空任务 ' + c.mission + (c.zone ? ' @' + c.zone.join(',') : '');
    return { dig: '构筑工事', rest: '休整', halt: '停止' }[c.op] || c.op;
  }

  function renderEvents() {
    const box = root.querySelector('#eventLog');
    box.replaceChildren(...logs.slice(-80).reverse().map((e) =>
      el('div', { class: 'ev ' + e.code }, [el('span', { class: 't' }, 'T' + e.turn), e.text])));
  }

  // ── 交互：地图点击 ──
  function onPick({ q, r }) {
    if (!view) return;
    const own = (view.own || []).find((u) => u.q === q && u.r === r);
    if (own) { selected = own.id; refresh(); updateMapSel(); return; }
    if (!selected) { toast('先选中一个己方单位', 'warn'); return; }
    const u = (view.own || []).find((x) => x.id === selected);
    if (!u) return;
    if (mode === 'move') {
      const path = hexLine(u.q, u.r, q, r);
      if (path.length < 2) { toast('目标即当前格', 'warn'); return; }
      addDraft({ op: 'move', unit: u.id, path });
    } else if (mode === 'attack') {
      const c = (view.contacts || []).find((x) => x.q === q && x.r === r && x.level >= 1);
      if (!c) { toast('该格无可攻击的已暴露敌情', 'warn'); return; }
      addDraft({ op: 'attack', unit: u.id, target: c.id });
    } else if (mode === 'fire') {
      addDraft({ op: 'fire', unit: u.id, zone: [q, r] });
    } else if (mode === 'air') {
      if (mission === 'none') addDraft({ op: 'air_mission', unit: u.id, mission: 'none' });
      else addDraft({ op: 'air_mission', unit: u.id, mission, zone: [q, r] });
    } else {
      toast('该模式请点击己方单位就地执行', 'warn');
    }
  }

  function addDraft(cmd) {
    draft = draft.filter((d) => d.unit !== cmd.unit); // 每单位单指令
    draft.push(cmd);
    ready = false;
    submit();
    renderOwn();
  }
  function clearDraft() { draft = []; ready = false; submit(); }
  function submit() {
    sock.send({ type: 'orders', replace: true, commands: draft });
    renderDraft(); renderModebar();
  }

  function updateMapSel() {
    if (!view) return;
    const u = (view.own || []).find((x) => x.id === selected);
    if (!u) { map.selection = null; map.rangeCenter = null; map.rangeRadius = 0; map.requestDraw(); return; }
    map.selection = { q: u.q, r: u.r, id: u.id };
    const t = typeMap[u.type] || {};
    if (mode === 'fire') { map.rangeCenter = [u.q, u.r]; map.rangeRadius = (t.range || 0) + 1; }
    else if (mode === 'air') { map.rangeCenter = [u.q, u.r]; map.rangeRadius = t.air_range || 0; }
    else if (mode === 'attack') { map.rangeCenter = [u.q, u.r]; map.rangeRadius = t.range || t.air_range || 1; }
    else { map.rangeCenter = null; map.rangeRadius = 0; }
    const mv = draft.find((d) => d.unit === u.id && d.op === 'move');
    map.draftPath = mv ? mv.path : null;
    map.requestDraw();
  }

  // ── 就绪控件 ──
  function readyButtons() {
    const rd = view?.ready ?? ready;
    const isAI = view?.ai_managed;
    return el('div', { class: 'toolbar', style: 'padding:0' }, [
      el('button', {
        class: 'btn btn-primary', disabled: !!isAI,
        onclick: () => { sock.send({ type: 'ready', value: true }); },
      }, '就 绪'),
      el('button', { class: 'btn', disabled: !!isAI, onclick: () => sock.send({ type: 'ready', value: false }) }, '解除就绪'),
      rd ? el('span', { class: 'ready-yes' }, '● 已就绪') : el('span', { class: 'muted small' }, '○ 未就绪'),
      isAI ? el('span', { class: 'tag warn' }, 'AI 代管中') : null,
    ]);
  }

  // ── WebSocket ──
  const sock = new RoomSocket(code, role, { name: join.name || undefined, force: join.force === true }).connect();
  const setConn = (live, extra) => { if (!connEl) return; connEl.className = 'conn ' + (live ? 'live' : 'dead'); connEl.textContent = (live ? '已连接 · ' : '') + (extra || (role === 'red' ? '红方终端' : '蓝方终端') + ' · 房间 ' + code); };
  sock.on('welcome', (m) => { setConn(true); if (m.view) onView(m.view); });
  sock.on('state', (m) => onView(m.view));
  sock.on('events', (m) => { logs = logs.concat(m.events || []); renderEvents(); });
  sock.on('error', (m) => toast(m.message, 'err'));
  sock.on('kicked', (m) => { toast(m.reason || '本角色已在其他终端加入', 'err'); setConn(false, '本角色已在其他终端加入'); });
  sock.on('close', (m) => setConn(false, m?.reason || '连接中断（角色位可能被占用），重连中…'));
  sock.on('open', () => setConn(true));

  function onView(v) {
    if (!v || v.kind !== 'side') { if (v && v.kind) toast('视图类型异常：' + v.kind, 'warn'); return; }
    const turnChanged = !view || view.turn !== v.turn;
    view = v;
    ready = v.ready ?? ready;
    if (turnChanged) { draft = (v.draft || []).map((d) => ({ ...d })); }
    map.setView(v);
    refresh(); updateMapSel(); renderEvents();
  }

  function readJoin(c) { try { return JSON.parse(sessionStorage.getItem('hf.join.' + c) || 'null'); } catch (_) { return null; } }
  function pct(a, b) { return b ? (a / b) * 100 : 0; }

  return () => { sock.close(); ro.disconnect(); map.dispose(); };
}

function panel(title, id, extra = '') {
  return el('div', { class: 'panel ' + extra }, [el('h3', {}, title), el('div', { class: 'scroll', id })]);
}
