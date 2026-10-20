// 大厅：剧本 / 房间 / 存档 一览 + 一键开局 + 按房间码加入
import { el, toast, clear } from '../util.js';
import { api } from '../api.js';
import { getMeta, router } from '../router.js';

const ROLES = [['director', '导演部'], ['red', '红方'], ['blue', '蓝方'], ['spectator', '观战']];

export async function renderLobby(root) {
  clear(root);
  const meta = await getMeta(true); // force：删除副本后由回调整页重渲，须绕过 metaCache 才能看到新列表
  const page = el('div', { class: 'page' });

  // 顶部一键区
  page.appendChild(el('div', { class: 'hero' }, [
    el('button', {
      class: 'btn btn-primary', onclick: () => quickStart(),
    }, '一键开局（默认剧本）'),
    el('a', { class: 'btn', href: '#/new' }, '创建副本向导'),
    el('span', { class: 'muted small' }, '默认剧本 ' + (meta.scenarios[0]?.name || '') + ' · 免覆盖直接进导演部'),
  ]));

  // 活跃房间（先取，加入区要用占用信息）
  let rooms = [];
  try { rooms = (await api.rooms()).rooms || []; } catch (_) {}

  // 加入房间
  const codeInput = el('input', { placeholder: '房间码（6位）', style: 'width:140px', id: 'joincode' });
  const roleSel = el('select', {}, ROLES.map(([v, l]) => el('option', { value: v }, l)));
  const nameInput = el('input', { placeholder: '昵称（可空）', style: 'width:130px' });
  page.appendChild(el('div', { class: 'join-bar' }, [
    el('label', {}, [el('span', {}, '房间码'), codeInput]),
    el('label', {}, [el('span', {}, '角色'), roleSel]),
    el('label', {}, [el('span', {}, '昵称'), nameInput]),
    el('button', {
      class: 'btn btn-1', onclick: () => {
        const code = (codeInput.value || '').trim().toUpperCase();
        if (!code) return toast('请输入房间码', 'warn');
        joinRoom(rooms, code, roleSel.value, nameInput.value);
      },
    }, '加入'),
    el('span', { class: 'spacer' }),
    el('button', { class: 'btn btn-sm', onclick: () => renderLobby(root) }, '刷新列表'),
  ]));

  page.appendChild(section('活跃房间 (' + rooms.length + ')', roomList(rooms)));

  // 剧本
  page.appendChild(section('可用剧本', el('div', { class: 'grid-3' },
    (meta.scenarios || []).map((s) => scenarioCard(s, meta)))));

  // 存档
  page.appendChild(section('已存副本 (' + (meta.saves || []).length + ')',
    saveList(meta.saves || [], () => renderLobby(root))));

  root.appendChild(page);
}

function section(title, body) {
  return el('div', { class: 'card', style: 'margin-bottom:16px' }, [el('h3', {}, title), body]);
}

function scenarioCard(s, meta) {
  return el('div', { class: 'card scn-card' }, [
    el('div', {}, [el('b', {}, s.name || s.id), el('span', { class: 'tag', style: 'margin-left:8px' }, s.era || '')]),
    el('div', { class: 'desc' }, s.description || ''),
    el('div', { class: 'kv' }, [el('span', {}, '回合 ' + (s.max_turns ?? '?')), el('span', {}, '单位 ' + (s.unit_count ?? '?')), el('span', {}, s.scale || '')]),
    el('div', { class: 'foot' }, [
      el('button', {
        class: 'btn btn-sm btn-primary',
        onclick: () => startScenario(s),
      }, '以此开局'),
      el('a', { class: 'btn btn-sm', href: '#/new' }, '向导创建'),
    ]),
  ]);
}

function roomList(rooms, codeInput) {
  if (!rooms.length) return el('div', { class: 'muted small' }, '暂无活跃房间');
  const tr = (r) => el('tr', {}, [
    el('td', {}, el('b', { class: 'mono' }, r.code)),
    el('td', {}, r.name || ''),
    el('td', {}, (r.turn ?? '?') + '/' + (r.max_turns ?? '?')),
    el('td', {}, el('span', { class: 'tag' + (r.fog ? '' : ' blue') }, r.fog ? '迷雾' : '明牌')),
    el('td', {}, Object.entries(r.roles || {}).map(([k, v]) => el('span', { class: 'tag', style: 'margin-right:4px' }, k + ':' + v))),
    el('td', {}, el('div', { class: 'row', style: 'gap:4px' }, ROLES.map(([v, l]) =>
      el('button', {
        class: 'btn btn-sm',
        onclick: () => joinRoom(rooms, r.code, v, ''),
      }, l)))),
  ]);
  return el('div', { class: 'room-list' }, el('table', {}, [
    el('tr', {}, [el('th', {}, '码'), el('th', {}, '名称'), el('th', {}, '回合'), el('th', {}, '迷雾'), el('th', {}, '在线'), el('th', {}, '加入')]),
    ...rooms.map(tr),
  ]));
}

export function saveList(saves, onChanged) {
  if (!saves.length) return el('div', { class: 'muted small' }, '暂无存档');
  return el('div', { class: 'room-list' }, el('table', {}, [
    el('tr', {}, [el('th', {}, '副本'), el('th', {}, '名称'), el('th', {}, '剧本'), el('th', {}, '最后回合'), el('th', {}, '操作')]),
    ...saves.map((s) => el('tr', {}, [
      el('td', { class: 'mono' }, s.instance || ''),
      el('td', {}, s.name || ''),
      el('td', {}, s.scenario_name || s.scenario || ''),
      el('td', {}, String(s.last_turn ?? s.turns_recorded ?? '?')),
      el('td', {}, el('div', { class: 'row', style: 'gap:4px' }, [
        el('button', {
          class: 'btn btn-sm btn-primary',
          onclick: async () => {
            const res = await api.loadSave(s.instance);
            if (res.code) { toast('已载入房间 ' + res.code); router.go(router.link(res.code, 'director')); }
            else toast(res.error || '载入失败', 'err');
          },
        }, '载入并导调'),
        el('button', {
          class: 'btn btn-sm btn-red',
          onclick: () => deleteSave(s, onChanged),
        }, '删除'),
      ])),
    ])),
  ]));
}

// 删除已存副本：二次确认（不可恢复，删磁盘目录 + 库中目录/内容行）；活跃房间占用时后端拒删
async function deleteSave(s, onChanged) {
  const label = (s.name ? s.name + '（' + s.instance + '）' : s.instance);
  if (!window.confirm('删除副本 ' + label + '？将同时删除其存档目录与全部历史数据，不可恢复。')) return;
  const res = await api.deleteSave(s.instance);
  if (res.deleted) {
    toast('已删除副本 ' + s.instance);
    if (onChanged) onChanged();
  } else {
    toast(res.error || '删除失败', 'err');
  }
}

async function startScenario(s) {
  toast('正在创建副本…');
  const res = await api.createInstance({ scenario: s.id || s.file, name: s.name });
  if (res.code) { toast('房间已创建 ' + res.code); router.go(router.link(res.code, 'director')); }
  else toast(res.error || '创建失败', 'err');
}

// 加入房间：对抗位已有人时必须先确认，接管=顶掉对方终端（不静默抢位）
function joinRoom(rooms, code, role, name) {
  const r = rooms.find((x) => (x.code || '').toUpperCase() === code);
  const taken = (role === 'red' || role === 'blue') && r?.side_taken?.[role] === true;
  let force = false;
  if (taken) {
    const label = role === 'red' ? '红方' : '蓝方';
    if (!window.confirm(`${label}已有指挥官在线。加入将接管该角色并把对方终端踢下线，确认接管？`)) return;
    force = true;
  }
  try { sessionStorage.setItem('hf.join.' + code, JSON.stringify({ role, name, force })); } catch (_) {}
  router.go(router.link(code, role));
}

// 一键开局：默认剧本、无覆盖、直接进导演部（首页与大厅共用）
export async function quickStart() {
  const meta = await getMeta();
  const s = meta.scenarios[0];
  if (!s) return toast('没有可用剧本', 'err');
  toast('一键开局中…');
  const res = await api.createInstance({ scenario: s.id || s.file }); // 不发送 overrides
  if (res.code) { toast('开局成功 · 房间 ' + res.code); router.go(router.link(res.code, 'director')); }
  else toast(res.error || (res.errors && res.errors.join(';')) || '创建失败', 'err');
}
