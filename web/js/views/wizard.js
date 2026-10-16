// 创建副本向导：5 步。核心原则——未编辑字段绝不进入 overrides，
// 服务端把 overrides 深合并(deepMerge)到剧本默认；未出现的键即"用默认"，null 亦表示不覆盖。
import { el, toast, clear } from '../util.js';
import { api } from '../api.js';
import { getMeta, router } from '../router.js';

const FLAGS = [['fog', '战争迷雾'], ['air_superiority', '制空权'], ['strict_supply', '严格后勤'], ['night', '夜间作战']];
const WMODES = [['', '（继承默认）'], ['fixed', '固定'], ['stochastic', '逐回合概率转移'], ['director', '导演部手控']];
const STEP_TITLES = ['剧本与名称', '战场环境', '天气', '部队编成', '审阅并创建'];
let META = null;

const num = (v) => (v === '' || v == null || isNaN(+v) ? null : +v);
function scenarios() { return (META && META.scenarios) || []; }
function types() { return (META && META.unit_types) || []; }
function weatherList() { return (META && META.weather) || []; }
function firstType() { return (types()[0] && types()[0].id) || 'rifle'; }
function optionFor(value, label, cur) {
  return el('option', { value, selected: value === cur ? 'selected' : null }, label);
}

export async function renderWizard(root) {
  clear(root);
  META = await getMeta();
  const st = newState();
  const body = el('div', { class: 'body card' });
  const side = el('div', { class: 'side' });
  const stepsBar = el('div', { class: 'steps' });
  root.appendChild(el('div', {}, [
    el('div', { class: 'page', style: 'padding-bottom:0' }, stepsBar),
    el('div', { class: 'wiz', style: 'padding:0 18px 18px; height:calc(100% - 78px)' }, [body, side]),
  ]));

  function renderSide() { side.replaceChildren(sidePanel(st, ctx)); }

  async function doValidate() {
    const overrides = buildOverrides(st);
    if (overrides.__invalid) { st.lastValidate = { errors: [overrides.__invalid] }; renderSide(); return; }
    st.lastValidate = await api.validate(st.scenarioId, overrides);
    renderSide();
  }

  async function create() {
    const overrides = buildOverrides(st);
    if (overrides.__invalid) { st.lastValidate = { errors: [overrides.__invalid] }; renderSide(); return toast(overrides.__invalid, 'err'); }
    const v = await api.validate(st.scenarioId, overrides);
    st.lastValidate = v; renderSide();
    if ((v.errors || []).length) return toast('校验未通过：' + v.errors[0], 'err');
    const b = { scenario: st.scenarioId };
    if (st.name.trim()) b.name = st.name.trim();
    if (st.seed !== '' && !isNaN(parseInt(st.seed))) b.seed = parseInt(st.seed);
    if (st.ai.red) b.ai_red = true;
    if (st.ai.blue) b.ai_blue = true;
    if (Object.keys(overrides).length) b.overrides = overrides;
    const res = await api.createInstance(b);
    if (res.code) { toast('副本已创建 · 房间 ' + res.code); router.go(router.link(res.code, 'director')); }
    else toast(res.error || (res.errors && res.errors.join(';')) || '创建失败', 'err');
  }

  function render() {
    stepsBar.replaceChildren(...STEP_TITLES.map((t, i) =>
      el('div', { class: 'st' + (i === st.step ? ' on' : ''), onclick: () => { st.step = i; render(); } }, (i + 1) + '. ' + t)));
    body.replaceChildren(STEP_RENDER[st.step](st, ctx));
    renderSide();
    if (st.step >= 1) doValidate();
  }

  const ctx = { render, doValidate, create, goto: (n) => { st.step = n; render(); } };
  render();
}

function newState() {
  const s = scenarios()[0] || {};
  return {
    step: 0, scenarioId: s.id || s.file, name: s.name || '', seed: '',
    env: { width: '', height: '', cell_km: '', lon: '', lat: '' },
    flags: Object.fromEntries(FLAGS.map(([k]) => [k, 'default'])),
    weather: { initial: '', mode: '' },
    sides: { red: { oob: [], budget: '' }, blue: { oob: [], budget: '' } },
    _oobDirty: { red: false, blue: false },
    ai: { red: false, blue: false },
    lastValidate: null,
  };
}

function buildOverrides(st) {
  const ov = {};
  const env = {};
  if (num(st.env.width) !== null) env.width = num(st.env.width);
  if (num(st.env.height) !== null) env.height = num(st.env.height);
  if (num(st.env.cell_km) !== null) env.cell_km = num(st.env.cell_km);
  if (num(st.env.lon) !== null && num(st.env.lat) !== null) env.center_lonlat = [num(st.env.lon), num(st.env.lat)];
  if (Object.keys(env).length) ov.environment = env;

  const flags = {};
  for (const [k] of FLAGS) if (st.flags[k] !== 'default') flags[k] = st.flags[k] === 'on';
  if (Object.keys(flags).length) ov.flags = flags;

  const wx = {};
  if (st.weather.initial) wx.initial = st.weather.initial;
  if (st.weather.mode) wx.mode = st.weather.mode;
  if (Object.keys(wx).length) ov.weather = wx;

  const sides = {};
  for (const side of ['red', 'blue']) {
    const cfg = {};
    const b = num(st.sides[side].budget);
    if (b !== null) cfg.points_budget = b;
    if (st._oobDirty[side]) {
      const oob = st.sides[side].oob.map(rowToOob).filter(Boolean);
      if (!oob.length) return { __invalid: '阵营 ' + side + ' 的 OOB 为空（若要保留默认请不要提交该字段）' };
      cfg.oob = oob;
    }
    if (Object.keys(cfg).length) sides[side] = cfg;
  }
  if (Object.keys(sides).length) ov.sides = sides;
  return ov;
}

function rowToOob(row) {
  if (!row.type) return null;
  const out = { type: row.type };
  if (num(row.qty) !== null) out.qty = num(row.qty);
  const q = num(row.q), r = num(row.r);
  if (q !== null && r !== null) out.at = [q, r];
  if (num(row.training) !== null) out.training = num(row.training);
  if (num(row.experience) !== null) out.experience = num(row.experience);
  if (row.cname && row.cname.trim()) {
    out.commander = { name: row.cname.trim() };
    if (num(row.cquality) !== null) out.commander.quality = num(row.cquality);
  }
  return out;
}

const STEP_RENDER = {
  0: (st, ctx) => {
    const scn = el('select', { onchange: (e) => { st.scenarioId = e.target.value; ctx.doValidate(); } },
      scenarios().map((s) => optionFor(s.id || s.file, s.name || s.id, st.scenarioId)));
    const nm = el('input', { value: st.name, placeholder: '继承剧本名称', oninput: (e) => st.name = e.target.value });
    const sd = el('input', { value: st.seed, placeholder: '留空 = 每局随机', oninput: (e) => st.seed = e.target.value });
    return el('div', {}, [
      el('h2', {}, '第 1 步 · 剧本与名称/种子'),
      el('div', { class: 'row' }, [
        el('label', {}, [el('span', {}, '剧本'), scn]),
        el('label', {}, [el('span', {}, '副本名称'), nm]),
        el('label', {}, [el('span', {}, '随机种子'), sd]),
      ]),
      el('p', { class: 'muted small' }, '不填写的字段不会覆盖剧本默认，服务端以 deepMerge 合并。'),
      nav(ctx, 1),
    ]);
  },

  1: (st, ctx) => {
    const flagRow = FLAGS.map(([k, l]) => {
      const sel = el('select', { onchange: (e) => { st.flags[k] = e.target.value; ctx.doValidate(); } },
        [optionFor('default', '（默认）', st.flags[k]), optionFor('on', '开', st.flags[k]), optionFor('off', '关', st.flags[k])]);
      return el('label', {}, [el('span', {}, l), sel]);
    });
    return el('div', {}, [
      el('h2', {}, '第 2 步 · 战场环境'),
      el('div', { class: 'row' }, [
        numField('宽（格）', st.env.width, (v) => st.env.width = v),
        numField('高（格）', st.env.height, (v) => st.env.height = v),
        numField('格距（km/格）', st.env.cell_km, (v) => st.env.cell_km = v),
      ]),
      el('div', { class: 'row' }, [
        numField('中心经度', st.env.lon, (v) => st.env.lon = v, '如 6.95'),
        numField('中心纬度', st.env.lat, (v) => st.env.lat = v, '如 51.42'),
      ]),
      el('h3', { style: 'margin-top:14px' }, '模式开关'),
      el('div', { class: 'row' }, flagRow),
      el('p', { class: 'muted small' }, '若剧本携带真实 GIS 数据包，宽高/中心由数据包决定，此处覆盖仅作用于程序化降级地形。'),
      nav(ctx, 2),
    ]);
  },

  2: (st, ctx) => {
    const initSel = el('select', { onchange: (e) => { st.weather.initial = e.target.value; ctx.doValidate(); } },
      [optionFor('', '（继承默认）', st.weather.initial)].concat(weatherList().map((w) => optionFor(w.id, w.label, st.weather.initial))));
    const modeSel = el('select', { onchange: (e) => { st.weather.mode = e.target.value; ctx.doValidate(); } },
      WMODES.map(([v, t]) => optionFor(v, t, st.weather.mode)));
    return el('div', {}, [
      el('h2', {}, '第 3 步 · 天气与演变'),
      el('div', { class: 'row' }, [
        el('label', {}, [el('span', {}, '初始天气'), initSel]),
        el('label', {}, [el('span', {}, '演变模式'), modeSel]),
      ]),
      el('p', { class: 'muted small' }, '天气可在导演部终端逐回合手改；此处仅设定初始与默认演变。'),
      nav(ctx, 3),
    ]);
  },

  3: (st, ctx) => el('div', {}, [
    el('h2', {}, '第 4 步 · 红蓝部队编成（OOB）'),
    el('p', { class: 'muted small' }, '留空即继承剧本默认兵力。一旦增删行即整体替换该阵营 OOB（服务端按列表替换语义合并）。'),
    oobWrap(st, 'red', ctx),
    oobWrap(st, 'blue', ctx),
    nav(ctx, 4),
  ]),

  4: (st, ctx) => {
    const ov = buildOverrides(st);
    if (ov.__invalid) {
      return el('div', {}, [el('h2', {}, '第 5 步 · 审阅并创建'), el('ul', { class: 'err-list' }, [el('li', {}, ov.__invalid)]), nav(ctx, 4)]);
    }
    const b = { scenario: st.scenarioId };
    if (st.name.trim()) b.name = st.name.trim();
    if (Object.keys(ov).length) b.overrides = ov;
    return el('div', {}, [
      el('h2', {}, '第 5 步 · 审阅并创建'),
      el('p', { class: 'muted small' }, '以下为本局将提交的请求体（未出现即代表使用剧本默认）：'),
      el('pre', { class: 'card mono', style: 'white-space:pre-wrap; max-height:300px; overflow:auto' }, JSON.stringify(b, null, 2)),
      el('div', { class: 'row', style: 'margin-top:12px' }, [
        el('button', { class: 'btn btn-primary', onclick: ctx.create }, '创建副本并进入导演部'),
        el('button', { class: 'btn', onclick: () => ctx.goto(0) }, '返回修改'),
      ]),
    ]);
  },
};

function nav(ctx, next) {
  const cur = next - 1;
  return el('div', { class: 'row', style: 'margin-top:16px' }, [
    cur > 0 ? el('button', { class: 'btn', onclick: () => ctx.goto(cur - 1) }, '上一步') : null,
    el('button', { class: 'btn btn-1', onclick: () => ctx.goto(next) }, next >= 4 ? '审阅' : '下一步'),
  ]);
}

function oobWrap(st, side, ctx) {
  const rows = st.sides[side].oob;
  const redraw = () => { ctx.doValidate(); ctx.render(); };
  const head = el('tr', {}, ['兵种', '数量', 'Q', 'R', '训练', '经验', '指挥官', '素质', ''].map((h) => el('th', {}, h)));
  const trs = rows.map((row, i) => oobRow(row,
    (patch) => { Object.assign(rows[i], patch); ctx.doValidate(); },
    () => { rows.splice(i, 1); redraw(); }));
  const budget = el('input', { type: 'number', value: st.sides[side].budget, style: 'width:90px', placeholder: '继承默认', oninput: (e) => { st.sides[side].budget = e.target.value; ctx.doValidate(); } });
  return el('div', { style: 'margin-bottom:14px' }, [
    el('div', { class: 'row', style: 'justify-content:space-between; margin-bottom:6px' }, [
      el('b', { class: 'tag ' + side }, side === 'red' ? '红方' : '蓝方'),
      el('label', { style: 'flex-direction:row; align-items:center; gap:6px' }, [el('span', { class: 'mini' }, '点数预算'), budget]),
      el('button', {
        class: 'btn btn-sm',
        onclick: () => { st._oobDirty[side] = true; rows.push({ type: firstType(), qty: 1, q: '', r: '', training: '', experience: '', cname: '', cquality: '' }); redraw(); },
      }, '增一行'),
    ]),
    el('div', { style: 'overflow:auto' }, el('table', { class: 'oob-table' }, el('tbody', {}, [head].concat(trs)))),
    rows.length ? null : el('div', { class: 'muted small' }, '（留空：继承剧本默认 OOB）'),
  ]);
}

function oobRow(row, patch, remove) {
  const td = (input) => el('td', {}, input);
  const typeSel = el('select', { onchange: (e) => patch({ type: e.target.value }) },
    types().map((t) => optionFor(t.id, t.label + '·' + t.points + '点', row.type)));
  return el('tr', {}, [
    td(typeSel),
    td(el('input', { class: 'num', type: 'number', value: row.qty, oninput: (e) => patch({ qty: e.target.value }) })),
    td(el('input', { class: 'num', type: 'number', value: row.q, placeholder: 'q', oninput: (e) => patch({ q: e.target.value }) })),
    td(el('input', { class: 'num', type: 'number', value: row.r, placeholder: 'r', oninput: (e) => patch({ r: e.target.value }) })),
    td(el('input', { class: 'num', type: 'number', value: row.training, placeholder: '-', oninput: (e) => patch({ training: e.target.value }) })),
    td(el('input', { class: 'num', type: 'number', value: row.experience, placeholder: '-', oninput: (e) => patch({ experience: e.target.value }) })),
    td(el('input', { type: 'text', value: row.cname, placeholder: '姓名', oninput: (e) => patch({ cname: e.target.value }) })),
    td(el('input', { class: 'num', type: 'number', step: '0.05', value: row.cquality, placeholder: '0-1', oninput: (e) => patch({ cquality: e.target.value }) })),
    td(el('span', { class: 'x', style: 'cursor:pointer; color:#c85a54', onclick: remove }, '删')),
  ]);
}

function sidePanel(st, ctx) {
  const v = st.lastValidate;
  const errs = v ? (v.errors || []) : [];
  const preview = (v && v.preview && v.preview.sides) || {};
  const budgetRow = (s) => {
    const p = preview[s] || {};
    const over = p.budget != null && p.points > p.budget + 0.001;
    const left = p.points != null ? Math.round(p.points) : '-';
    const right = p.budget != null ? Math.round(p.budget) : '∞';
    return el('div', { class: 'budget' + (over ? ' over' : '') }, [
      el('span', { class: 'tag ' + s }, s === 'red' ? '红方' : '蓝方'),
      el('span', {}, left + ' / ' + right),
      el('span', { class: 'mini' }, (p.units || 0) + ' 单位'),
    ]);
  };
  return el('div', {}, [
    el('div', { class: 'preview-box' }, [el('h3', {}, '点数预算'), budgetRow('red'), budgetRow('blue')]),
    el('div', { class: 'preview-box' }, [
      el('h3', {}, 'AI 代管（开局即托管，便于单端自测）'),
      el('div', { class: 'row' }, [
        el('label', { class: 'sw' }, [el('input', { type: 'checkbox', checked: st.ai.red, onchange: (e) => st.ai.red = e.target.checked }), el('span', {}, '红方')]),
        el('label', { class: 'sw' }, [el('input', { type: 'checkbox', checked: st.ai.blue, onchange: (e) => st.ai.blue = e.target.checked }), el('span', {}, '蓝方')]),
      ]),
    ]),
    errs.length
      ? el('div', { class: 'preview-box' }, [el('h3', {}, '校验错误'), el('ul', { class: 'err-list' }, errs.map((e) => el('li', {}, e)))])
      : el('div', { class: 'preview-box' }, el('span', { class: 'muted small' }, '当前覆盖无校验错误')),
    el('button', { class: 'btn btn-primary', style: 'width:100%', onclick: ctx.create }, '创建副本并进入导演部'),
  ]);
}

function numField(label, value, oninput, ph = '继承默认') {
  return el('label', {}, [el('span', {}, label), el('input', { type: 'number', value, placeholder: ph, oninput: (e) => oninput(e.target.value) })]);
}
