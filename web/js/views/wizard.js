// 创建副本向导：5 步。核心原则——未编辑字段绝不进入 overrides，
// 服务端把 overrides 深合并(deepMerge)到剧本默认；未出现的键即"用默认"，null 亦表示不覆盖。
import { el, toast, clear } from '../util.js';
import { api } from '../api.js';
import { getMeta, router } from '../router.js';

const FLAGS = [['fog', '战争迷雾'], ['air_superiority', '制空权'], ['strict_supply', '严格后勤'], ['night', '夜间作战']];
const WMODES = [['', '（继承默认）'], ['fixed', '固定'], ['stochastic', '逐回合概率转移'], ['director', '导演部手控']];
const STEP_TITLES = ['剧本与名称', '战场环境', '天气', '部队编成', '审阅并创建'];
// 兵种属性覆盖：只放"装备性能"，规则语义（cls/指挥所/补给源/航空角色）不在创建副本时开放
const TYPE_ATTRS = [['mobility', '机动'], ['firepower', '火力'], ['protection', '防护'], ['recon', '侦察'], ['load', '负荷'], ['consumption', '消耗']];
let META = null;

const num = (v) => (v === '' || v == null || isNaN(+v) ? null : +v);
function scenarios() { return (META && META.scenarios) || []; }
function gisPackages() { return (META && META.gis) || []; }
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
  const env = envOf(s);
  return {
    step: 0, scenarioId: s.id || s.file, name: s.name || '', seed: '',
    // 程序化降级参数（没有数据包时才起作用）
    env: { width: '', height: '', cell_km: String(env.cell_km || ''), lon: String(env.lon || ''), lat: String(env.lat || '') },
    gis: {
      mode: 'existing',        // existing=用已有数据包 / generate=按范围现场生成
      pkg: '',                 // existing 模式选中的包名（'' = 用剧本默认）
      bbox: { w: env.w, s: env.s, e: env.e, n: env.n },
      cell_km: '',
      dem: 'auto', landuse: 'auto', roads: true, water: true,
      name: '',
      plan: null, planBusy: false, planErr: '',
      job: null, jobErr: '', caps: null,
    },
    flags: Object.fromEntries(FLAGS.map(([k]) => [k, 'default'])),
    weather: { initial: '', mode: '' },
    sides: { red: { oob: [], budget: '' }, blue: { oob: [], budget: '' } },
    _oobDirty: { red: false, blue: false },
    typeRows: [],                      // 被覆盖属性的兵种 id（按加入顺序）
    types: {},                         // id → {属性: 输入值}，空字符串=继承默认
    ai: { red: false, blue: false },
    lastValidate: null,
  };
}

/// 剧本里的 environment → 表单初值（范围框按中心 ±0.9° 起算，够一次营级交战）
function envOf(s) {
  const e = (s && s.environment) || {};
  const lon = num(e.center_lonlat && e.center_lonlat[0]);
  const lat = num(e.center_lonlat && e.center_lonlat[1]);
  if (lon === null || lat === null) return { cell_km: e.cell_km, lon: '', lat: '', w: '', s: '', e: '', n: '' };
  const fmt = (v) => String(Math.round(v * 1000) / 1000);
  return {
    cell_km: e.cell_km, lon: fmt(lon), lat: fmt(lat),
    w: fmt(lon - 0.95), e: fmt(lon + 0.95), s: fmt(lat - 0.55), n: fmt(lat + 0.55),
  };
}

function gisBbox(g) {
  const w = num(g.bbox.w), s = num(g.bbox.s), e = num(g.bbox.e), n = num(g.bbox.n);
  if (w === null || s === null || e === null || n === null) return null;
  if (!(w < e && s < n)) return null;
  return [w, s, e, n];
}

/// 生效的数据包名：generate 模式用作业产物，existing 用下拉选择
function effectivePkg(st) {
  const g = st.gis;
  if (g.mode === 'generate') return (g.job && g.job.status === 'done') ? g.job.name : '';
  return g.pkg || '';
}

function buildOverrides(st) {
  const ov = {};
  const pkg = effectivePkg(st);
  if (st.gis.mode === 'generate' && !pkg) {
    return { __invalid: '战场数据尚未生成：请先在「战场环境」里点「生成六角格网格」并等待完成' };
  }
  if (pkg) ov.gis_package = pkg;

  const env = {};
  if (!pkg) {
    // 无数据包 ⇒ 这些参数决定程序化格网；有数据包 ⇒ 几何由数据包 meta 决定，不再提交
    if (num(st.env.width) !== null) env.width = num(st.env.width);
    if (num(st.env.height) !== null) env.height = num(st.env.height);
    if (num(st.env.cell_km) !== null) env.cell_km = num(st.env.cell_km);
    if (num(st.env.lon) !== null && num(st.env.lat) !== null) env.center_lonlat = [num(st.env.lon), num(st.env.lat)];
  }
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

  const tdefs = [];
  for (const id of st.typeRows) {
    const p = st.types[id] || {};
    const e = { id };
    for (const [k] of TYPE_ATTRS) {
      const v = num(p[k]);
      if (v !== null) e[k] = v;
    }
    if (Object.keys(e).length > 1) tdefs.push(e);
  }
  if (tdefs.length) ov.unit_type_defs = tdefs;
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
      el('h2', {}, '第 2 步 · 战场环境与六角格'),
      gisBlock(st, ctx),
      el('h3', { style: 'margin-top:14px' }, '模式开关'),
      el('div', { class: 'row' }, flagRow),
      el('details', { style: 'margin-top:12px' }, [
        el('summary', { class: 'muted small' }, '程序化地形参数（仅在没有任何数据包时生效）'),
        el('div', { class: 'row', style: 'margin-top:8px' }, [
          numField('宽（格）', st.env.width, (v) => st.env.width = v),
          numField('高（格）', st.env.height, (v) => st.env.height = v),
          numField('对边距（实地 km）', st.env.cell_km, (v) => st.env.cell_km = v),
        ]),
        el('div', { class: 'row' }, [
          numField('中心经度', st.env.lon, (v) => st.env.lon = v, '如 6.95'),
          numField('中心纬度', st.env.lat, (v) => st.env.lat = v, '如 51.42'),
        ]),
      ]),
      el('p', { class: 'muted small' }, '六角格几何（原点、半径、行列数）始终取自所选数据包，与在线天地图瓦片同一墨卡托框架；对边距按实地公里计。'),
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
    el('h2', {}, '第 4 步 · 红蓝部队编成（OOB 与兵种属性）'),
    el('p', { class: 'muted small' }, '留空即继承剧本默认兵力。一旦增删行即整体替换该阵营 OOB（服务端按列表替换语义合并）。'),
    oobWrap(st, 'red', ctx),
    oobWrap(st, 'blue', ctx),
    typeDefsWrap(st, ctx),
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

function typeDefsWrap(st, ctx) {
  const ids = st.typeRows;
  const pick = el('select', {}, types().map((t) => optionFor(t.id, t.label + '·' + t.points + '点', '')));
  const head = el('tr', {}, ['兵种'].concat(TYPE_ATTRS.map(([, l]) => l)).concat(['']));
  const rows = ids.map((id, i) => {
    const base = types().find((t) => t.id === id) || {};
    const cur = st.types[id] || (st.types[id] = {});
    return el('tr', {}, [
      el('td', {}, el('b', {}, base.label || id)),
      ...TYPE_ATTRS.map(([k, label]) => el('td', {}, el('input', {
        class: 'num', type: 'number', step: '0.1',
        value: cur[k] == null ? '' : cur[k], placeholder: String(base[k] == null ? '' : base[k]),
        title: label + '：留空=出厂默认 ' + (base[k] == null ? '-' : base[k]),
        oninput: (e) => { cur[k] = e.target.value; ctx.doValidate(); },
      }))),
      el('td', {}, el('button', { class: 'btn btn-sm', onclick: () => { ids.splice(i, 1); ctx.render(); } }, '移除')),
    ]);
  });
  return el('div', { class: 'typedev' }, [
    el('div', { class: 'row', style: 'justify-content:space-between; align-items:baseline; margin-bottom:6px' }, [
      el('b', {}, '兵种属性覆盖（机动 / 战力 / 后勤）'),
      el('div', { class: 'row', style: 'gap:6px' }, [
        pick,
        el('button', {
          class: 'btn btn-sm',
          onclick: () => {
            const id = pick.value;
            if (!id) return toast('请选择兵种', 'err');
            if (ids.includes(id)) return toast('该兵种已在列表里', 'err');
            ids.push(id); ctx.render();
          },
        }, '加入覆盖'),
      ]),
    ]),
    el('p', { class: 'muted small' },
      '留空的字段继承出厂定义；改动即按属性偏离度自动加价/减价（见右侧「兵种成交价」）。'
      + '兵种属性是本局双方共用的装备性能基线，阵营差异请用上面的 OOB 人员水平表达。'),
    ids.length
      ? el('div', { style: 'overflow:auto' }, el('table', { class: 'oob-table' }, el('tbody', {}, [head].concat(rows))))
      : el('div', { class: 'muted small' }, '（未覆盖任何兵种：全部使用剧本/标准库定义）'),
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
  const tp = (v && v.type_points) || {};
  const priceRows = Object.keys(tp).map((id) => {
    const base = types().find((t) => t.id === id) || {};
    const up = tp[id] > (base.points || 0);
    return el('div', { class: 'price' + (up ? ' up' : '') }, [
      el('span', {}, base.label || id),
      el('span', { class: 'mono' }, (base.points != null ? base.points : '-') + ' → ' + tp[id] + ' 点'),
    ]);
  });
  return el('div', {}, [
    el('div', { class: 'preview-box' }, [el('h3', {}, '点数预算'), budgetRow('red'), budgetRow('blue')]),
    priceRows.length ? el('div', { class: 'preview-box' }, [el('h3', {}, '兵种成交价（服务端自动计价）'), priceRows]) : null,
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

// ─────────────────── 战场数据（六角格） ───────────────────

const GIS_MODES = [['existing', '使用已有数据包'], ['generate', '按范围现场生成']];
const LAYER_CN = {
  elevation: '高程', landcover: '土地覆盖', roads: '道路', hydrology: '水系', settlements: '聚落',
};

function pkgOf(name) { return gisPackages().find((p) => p.name === name) || null; }

function gisBlock(st, ctx) {
  const g = st.gis;
  if (!g.dom) {
    const nb = (key) => el('input', {
      type: 'number', step: '0.01', value: g.bbox[key],
      oninput: (e) => { g.bbox[key] = e.target.value; schedulePlan(st, ctx); },
    });
    g.dom = {
      w: nb('w'), s: nb('s'), e: nb('e'), n: nb('n'),
      cell: el('input', {
        type: 'number', step: '0.1', min: '0.5', value: g.cell_km, placeholder: '留空 = 用推荐',
        oninput: (e) => { g.cell_km = e.target.value; schedulePlan(st, ctx); },
      }),
      name: el('input', { type: 'text', value: '', placeholder: '包名（留空自动加时间戳）', oninput: (e) => { g.name = e.target.value; } }),
      plan: el('div', {}),
      prog: el('div', {}),
    };
  }
  const modeRow = el('div', { class: 'row' }, GIS_MODES.map(([v, t]) => el('button', {
    class: 'btn btn-sm' + (g.mode === v ? ' btn-1' : ''), onclick: () => { g.mode = v; if (v === 'generate') loadCaps(g); ctx.render(); },
  }, t)));

  const head = el('div', { class: 'gis-box' }, [
    el('h3', {}, '战场数据与六角格网络'),
    el('p', { class: 'muted small' }, '六角格由真实栅格（本地全球 DEM + ESA WorldCover）按范围现场生成，也可复用已生成的数据包。'),
    modeRow,
  ]);
  head.appendChild(g.mode === 'existing' ? existingPanel(st, ctx) : generatePanel(st, ctx));
  return head;
}

function existingPanel(st, ctx) {
  const g = st.gis;
  const sc = scenarios().find((s) => (s.id || s.file) === st.scenarioId) || {};
  const defPkg = sc.gis_package || '';
  const sel = el('select', { onchange: (e) => { g.pkg = e.target.value; ctx.doValidate(); ctx.render(); } },
    [optionFor('', defPkg ? ('（继承剧本默认：' + defPkg + '）') : '（无：用程序化地形）', g.pkg)]
      .concat(gisPackages().map((p) => optionFor(p.name, pkgLabel(p), g.pkg))));
  const box = el('div', { style: 'margin-top:10px' }, [
    el('div', { class: 'row' }, [el('label', {}, [el('span', {}, '数据包'), sel])]),
  ]);
  const meta = pkgOf(g.pkg) || pkgOf(defPkg);
  box.appendChild(meta ? pkgCard(meta) : el('p', { class: 'muted small' }, '未选数据包 ⇒ 引擎按「程序化地形参数」造格网（不来自真实地形）。'));
  return box;
}

function pkgLabel(p) {
  return p.name + ' · 对边距 ' + fmtKm(p.cell_km) + ' · ' + (p.width || '?') + '×' + (p.height || '?') +
    ' 格' + (p.cells ? '（' + p.cells + '）' : '') + (p.legacy ? ' · 旧包' : '');
}

function fmtKm(v) { return v == null ? '?' : (Math.round(v * 100) / 100) + ' km'; }

function pkgCard(p) {
  const layers = Object.entries(p.layers || {}).map(([k, v]) =>
    el('li', {}, (LAYER_CN[k] || k) + '：' + (v || '未标注')));
  const bb = p.bbox;
  return el('div', { class: 'card pkg-card' }, [
    el('div', { class: 'row' }, [
      kv('来源', p.source || '?'),
      kv('对边距', fmtKm(p.cell_km) + '（实地）' + (p.mercator_scale ? ' · 墨卡托 ×' + (Math.round(p.mercator_scale * 1000) / 1000) : '')),
      kv('格网', (p.width || '?') + '×' + (p.height || '?') + ' = ' + (p.cells || '?') + ' 格'),
    ]),
    el('div', { class: 'row' }, [
      kv('实地范围', p.ground_km && p.ground_km[0] ? (Math.round(p.ground_km[0]) + '×' + Math.round(p.ground_km[1]) + ' km') : '?'),
      kv('四至', bb ? bb.map((v) => (Math.round(v * 100) / 100) + '°').join(' / ') : '?'),
      kv('生成时间', p.generated_at || '?'),
    ]),
    layers.length ? el('ul', { class: 'muted small', style: 'margin:6px 0 0 16px' }, layers) : null,
    p.legacy ? el('p', { class: 'warn small' }, '旧数据包未标注墨卡托放大，中纬度的实地格距会偏大；建议现场重新生成一份。') : null,
  ]);
}

function kv(k, v) {
  return el('label', { class: 'kv' }, [el('span', {}, k), el('b', {}, String(v))]);
}

function generatePanel(st, ctx) {
  const g = st.gis;
  const d = g.dom;
  const running = g.job && (g.job.status === 'running' || g.job.status === 'queued');
  const box = el('div', { style: 'margin-top:10px' }, [
    el('div', { class: 'row' }, [
      numField('西经', g.bbox.w, (v) => { g.bbox.w = v; schedulePlan(st, ctx); }),
      numField('南纬', g.bbox.s, (v) => { g.bbox.s = v; schedulePlan(st, ctx); }),
      numField('东经', g.bbox.e, (v) => { g.bbox.e = v; schedulePlan(st, ctx); }),
      numField('北纬', g.bbox.n, (v) => { g.bbox.n = v; schedulePlan(st, ctx); }),
    ]),
    el('p', { class: 'muted small' }, '四至指六角格「格心」范围（WGS84）。单边不超过 8°，超过 3 万格会被拒绝。'),
    el('div', { class: 'row' }, [
      el('label', {}, [el('span', {}, '六角格对边距（实地 km）'), d.cell]),
      el('button', {
        class: 'btn btn-sm',
        onclick: () => {
          // 面板是增量重绘的，不能靠 disabled 属性反映方案是否就绪
          if (!g.plan) return toast(g.planErr || '正在计算格网方案，稍候', 'warn');
          g.cell_km = String(g.plan.recommended.cell_km);
          d.cell.value = g.cell_km;
          schedulePlan(st, ctx);
        },
      }, '用推荐对边距'),
    ]),
    d.plan,
    el('h3', { style: 'margin-top:12px' }, '数据层'),
    el('div', { class: 'row' }, [
      el('label', {}, [el('span', {}, '高程'), el('select', { onchange: (e) => { g.dem = e.target.value; }, },
        [['auto', '自动（本地真实 DEM 优先）'], ['local', '仅本地 DEM 栅格'], ['synthetic', '合成（离线演示）']]
          .map(([v, t]) => optionFor(v, t, g.dem)))]),
      el('label', {}, [el('span', {}, '土地利用/覆盖'), el('select', { onchange: (e) => { g.landuse = e.target.value; }, },
        [['auto', '真实 WorldCover 10 m'], ['proxy', '噪声代理（无网络）'], ['off', '不参与判类']]
          .map(([v, t]) => optionFor(v, t, g.landuse)))]),
      el('label', { class: 'sw' }, [
        el('input', { type: 'checkbox', checked: g.roads, onchange: (e) => { g.roads = e.target.checked; } }),
        el('span', {}, '道路走廊（地形派生：无公开真实道路矢量可用）'),
      ]),
      el('label', { class: 'sw' }, [
        el('input', { type: 'checkbox', checked: g.water, onchange: (e) => { g.water = e.target.checked; } }),
        el('span', {}, '水系（真实矢量：Natural Earth 10m 河流/湖泊，叠加于 DEM 派生河带之上）'),
      ]),
    ]),
    g.caps && !g.caps.available ? el('p', { class: 'warn small' }, '本机没有可用的 Python，无法现场生成：' + (g.caps.reason || '未知原因')) : null,
    g.caps && g.caps.available && !g.caps.rasterio ? el('p', { class: 'warn small' }, 'Python 缺 rasterio/numpy ⇒ 只能生成合成地形；真实栅格请 pip 安装后重试。') : null,
    g.caps && g.caps.available && g.caps.rasterio && !g.caps.dem_present ? el('p', { class: 'warn small' }, '未发现本地 DEM（' + (g.caps.dem_tif || '?') + '）⇒ 高程将退回合成。') : null,
    g.caps && g.caps.available && g.water && g.caps.water_vector_ok === false ? el('p', { class: 'warn small' }, '真实水系不可用（' + (g.caps.water_reason || '缺 GeoPackage 或缺 geopandas') + '）⇒ 水文退回 DEM 派生河带。') : null,
    el('div', { class: 'row', style: 'margin-top:12px' }, [
      el('label', {}, [el('span', {}, '数据包名'), d.name]),
      el('button', {
        class: 'btn btn-primary', disabled: running,
        onclick: () => startGen(st, ctx),
      }, running ? '生成中…' : '生成六角格网格'),
      g.job && g.job.status === 'done' ? el('button', {
        class: 'btn', onclick: () => { g.mode = 'existing'; g.pkg = g.job.name; ctx.render(); },
      }, '改用新生成的包') : null,
    ]),
    d.prog,
  ]);
  paintPlan(st);
  paintProgress(st);
  // 进入即算一次方案，别让推荐按钮空着等用户敲键盘
  if (!g.plan && !g.planBusy && gisBbox(g)) schedulePlan(st, ctx);
  return box;
}

function loadCaps(g) {
  if (g.caps) return;
  api.gisCapabilities().then((c) => { g.caps = c; }).catch(() => {});
}

let planTimer = 0;
function schedulePlan(st, ctx) {
  const g = st.gis;
  clearTimeout(planTimer);
  const bb = gisBbox(g);
  if (!bb) { g.plan = null; g.planErr = '范围框需满足 西<东、南<北'; if (g.dom) paintPlan(st); return; }
  g.planBusy = true;
  planTimer = setTimeout(() => {
    api.gisPlan(bb, num(g.cell_km) || undefined).then((res) => {
      g.planBusy = false;
      if (res.error) { g.plan = null; g.planErr = res.error; } else { g.plan = res; g.planErr = ''; }
      if (st.gis.mode === 'generate' && document.body.contains(g.dom.plan)) paintPlan(st);
    }).catch((e) => { g.planBusy = false; g.planErr = '方案计算失败：' + e; if (g.dom) paintPlan(st); });
  }, 420);
}

function paintPlan(st) {
  const g = st.gis;
  const host = g.dom && g.dom.plan;
  if (!host) return;
  if (g.planErr && !g.plan) { host.replaceChildren(el('p', { class: 'warn small' }, g.planErr)); return; }
  const p = g.plan;
  if (!p) {
    host.replaceChildren(el('p', { class: 'muted small' }, g.planBusy ? '正在计算格网方案…' : '填好范围框后自动给出推荐对边距与行列数。'));
    return;
  }
  const rec = p.recommended.cell_km;
  const activeKm = num(g.cell_km) || rec;
  const cur = (p.ladder || []).find((c) => c.cell_km === activeKm);
  const chips = (p.ladder || []).map((c) => el('button', {
    class: 'btn btn-sm chip' + (String(c.cell_km) === String(g.cell_km) ? ' btn-1' : '') + (c.cell_km === rec ? ' rec' : ''),
    title: (c.label === 'recommended' ? '推荐' : c.label === 'heavy' ? '格数偏多' : c.label === 'coarse' ? '格数偏少' : ''),
    onclick: () => { g.cell_km = String(c.cell_km); g.dom.cell.value = g.cell_km; schedulePlan(st); },
  }, fmtKm(c.cell_km) + ' · ' + c.width + '×' + c.height + ' · ' + c.cells + '格'));
  host.replaceChildren(el('div', { class: 'card plan-card' }, [
    el('div', { class: 'row' }, [
      kv('实地范围', Math.round(p.ground_km[0]) + '×' + Math.round(p.ground_km[1]) + ' km'),
      kv('墨卡托放大', '×' + (Math.round(p.mercator_scale * 1000) / 1000)),
      kv('推荐对边距', fmtKm(rec)),
      kv('当前格网', cur ? (fmtKm(activeKm) + ' · ' + cur.width + '×' + cur.height + ' = ' + cur.cells + ' 格'
        + (num(g.cell_km) ? '' : '（用推荐）')) : '未定'),
    ]),
    el('div', { class: 'chips' }, chips),
    el('ul', { class: 'muted small why' }, (p.recommended.reasons || []).map((r) => el('li', {}, r))),
  ]));
}

function paintProgress(st) {
  const g = st.gis;
  const host = g.dom && g.dom.prog;
  if (!host) return;
  const j = g.job;
  if (!j) { host.replaceChildren(el('p', { class: 'muted small' }, '尚未生成。真实栅格生成约需十几秒到几分钟。')); return; }
  const tail = (j.log || []).slice(-9);
  host.replaceChildren(el('div', { class: 'card job-card' }, [
    el('div', { class: 'row' }, [
      kv('作业', j.id),
      kv('状态', ({ queued: '排队中', running: '生成中', done: '已完成', failed: '失败' })[j.status] || j.status),
      kv('产物', 'data/gis/' + j.name),
    ]),
    el('pre', { class: 'mono job-log' }, tail.join('\n') || '…'),
    j.error ? el('p', { class: 'warn small' }, j.error) : null,
    j.status === 'done' && j.meta ? el('p', { class: 'small' },
      '自校验 ' + ((j.meta.fails || 0) === 0 ? '全部通过' : (j.meta.fails + ' 项未通过')) +
      ' · 地形构成 ' + JSON.stringify((j.meta.terrain_counts) || {})) : null,
  ]));
}

async function startGen(st, ctx) {
  const g = st.gis;
  if (g.job && (g.job.status === 'running' || g.job.status === 'queued')) return;
  const bb = gisBbox(g);
  if (!bb) return toast('范围框需满足 西<东、南<北', 'err');
  const cell = num(g.cell_km) || (g.plan && g.plan.recommended && g.plan.recommended.cell_km);
  if (!cell) return toast('请先给出对边距', 'err');
  const spec = {
    bbox: bb, cell_km: cell, name: (g.name || '').trim(),
    dem: g.dem, landuse: g.landuse, roads: g.roads, water: g.water,
  };
  g.jobErr = '';
  const res = await api.gisStartJob(spec);
  if (res.error) { g.jobErr = res.error; return toast(res.error, 'err'); }
  g.job = res;
  ctx.render(); // 重绘一次：让「生成中…」与禁用态真实生效
  pollJob(st, ctx);
}

function pollJob(st, ctx) {
  const g = st.gis;
  if (g.timer) clearInterval(g.timer);
  g.timer = setInterval(async () => {
    const j = await api.gisJob(g.job.id).catch(() => null);
    if (!j || j.error) { clearInterval(g.timer); g.timer = null; g.job = null; paintProgress(st); return toast((j && j.error) || '作业丢失', 'err'); }
    g.job = j;
    paintProgress(st);
    if (j.status === 'done' || j.status === 'failed') {
      clearInterval(g.timer); g.timer = null;
      if (j.status === 'done') {
        META = await getMeta(true);   // 新数据包进入列表
        toast('六角格数据包已生成：' + j.name);
      } else {
        toast('生成失败：' + (j.error || '见日志'), 'err');
      }
      if (g.mode === 'generate' && st.step === 1) ctx.render();
    }
  }, 1500);
}
