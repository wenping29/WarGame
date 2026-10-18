// /ws protocol smoke test
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const BASE = 'ws://localhost:5018/ws';
const log = (...a) => console.log(...a);
const fail = (m) => { console.error('FAIL:', m); process.exit(1); };

function connect(qs) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(BASE + qs);
    const msgs = [];
    const perTypeCount = {};
    const waits = [];
    const pump = () => {
      for (const w of [...waits]) {
        if (perTypeCount[w.type] >= w.need) {
          waits.splice(waits.indexOf(w), 1);
          clearTimeout(w.timer);
          w.resolve(perTypeCount && msgs.filter(m => m.type === w.type).slice(-w.count));
        }
      }
    };
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      msgs.push(m);
      perTypeCount[m.type] = (perTypeCount[m.type] || 0) + 1;
      pump();
    };
    ws.onopen = () => resolve({
      ws, msgs,
      send: (o) => ws.send(JSON.stringify(o)),
      waitFor: (type, count = 1, ms = 8000) => new Promise((res, rej) => {
        const have = perTypeCount[type] || 0;
        if (have >= count) {
          res(msgs.filter(m => m.type === type).slice(-count));
          return;
        }
        const w = { type, count, need: count, resolve: res };
        w.timer = setTimeout(() => { waits.splice(waits.indexOf(w), 1); rej(new Error(`timeout waiting ${type} x${count}`)); }, ms);
        waits.push(w);
      }),
    });
    ws.onerror = (e) => reject(new Error('ws error ' + (e.message || '')));
  });
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const API = 'http://localhost:5018';

// 房间必须由 REST 创建：未知房间码连 /ws 一律 404（不再静默建局）
const noSuchRoom = await new Promise((resolve) => {
  const ws = new WebSocket(BASE + '?room=NOSUCH0&role=director');
  ws.onopen = () => { ws.close(); resolve('opened'); };
  ws.onerror = () => resolve('rejected');
});
if (noSuchRoom !== 'rejected') fail('unknown room code should not open a game, got: ' + noSuchRoom);
log('0. 未知房间码被拒（不再静默建局）');

const inst = await (await fetch(API + '/api/instances', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ scenario: 'rhine_corridor', name: 'ws-smoke', seed: 424242 }),
})).json();
if (!inst.code) fail('create instance failed: ' + JSON.stringify(inst));
if (inst.seed !== 424242) fail('seed 未被采纳: ' + JSON.stringify(inst.seed));
const room = inst.code;
log('0. 副本创建 code=' + room, 'instance=' + inst.instance, 'seed=' + inst.seed);

// 1. director joins
const dir = await connect(`?room=${room}&role=director`);
const [w1] = await dir.waitFor('welcome');
log('1. director welcome ok, token len', w1.token.length);
const [dirState0] = await dir.waitFor('state');
const turn0 = dirState0.turn;
log('   director units:', (dirState0.view.units || []).length, 'starting turn', turn0);

// 2. red joins and submits a rest order (web format)
const red = await connect(`?room=${room}&role=red&token=&name=redterm`);
log('2. red joined');
const [redState] = await red.waitFor('state');
const own = redState.view.own || [];
if (redState.view.kind !== 'side' || own.length === 0) fail('red side view invalid: ' + redState.view.kind + ' units=' + own.length);
log('   red side view: turn', redState.turn, 'own units', own.length);
red.send({ type: 'orders', replace: true, commands: [{ op: 'rest', unit: own[0].id }] });
await sleep(400);
const pend = red.msgs.filter(m => m.type === 'state').pop();
if (pend.pending.red !== 1) fail('pending.red expected 1 got ' + pend.pending.red);
log('3. order accepted, pending=', JSON.stringify(pend.pending), 'draft len', pend.draft.length);

// 3. blue joins with React-format orders
const blue = await connect(`?room=${room}&role=blue`);
const [blueState] = await blue.waitFor('state');
const bOwn = blueState.view.own || [];
blue.send({ type: 'orders', replace: true, commands: [{ op: 'rest', unitId: bOwn[0].id, path: [] }] });
await sleep(300);
log('4. blue side view units', bOwn.length, 'react-format order sent');

// 4. resolve via director
const statesBefore = dir.msgs.filter(m => m.type === 'state').length;
const eventsBefore = dir.msgs.filter(m => m.type === 'events').length;
dir.send({ type: 'resolve' });
const [ev] = await dir.waitFor('events', 1, 15000);
// resolve broadcasts events then a fresh state; wait for the new state (post-resolve)
const sArr = await dir.waitFor('state', statesBefore + 1, 8000);
const turnNow = sArr[sArr.length - 1].turn;
log('5. resolved: events', ev.events.length, 'rejected', ev.rejected.length, 'new turn', turnNow);
if (turnNow !== turn0 + 1) fail(`expected turn ${turn0} -> ${turn0 + 1}, got ${turnNow}`);

// 5. frames：回放帧含导演部全知视图，对抗方不得取用
const redErrBefore = red.msgs.filter(m => m.type === 'error').length;
red.send({ type: 'frames' });
const [ferr] = await red.waitFor('error', redErrBefore + 1, 5000);
log('6. red frames rejected:', ferr.message);
dir.send({ type: 'frames' });
log('   director frames count:', (await dir.waitFor('frames'))[0].frames.length);

// 6. save
const [saved] = await (async () => { dir.send({ type: 'save', name: 'ws-smoke' }); return dir.waitFor('saved'); })();
if (saved.instance !== inst.instance) fail('手动存档必须落在同一副本上: ' + saved.instance + ' vs ' + inst.instance);
log('7. saved instance', saved.instance, 'last_turn', saved.meta.last_turn, 'seed', saved.meta.seed);

// 7b. 存档落盘 + 载入往返：快照 + 重放尾部必须回到房间里那个指纹（P1 确定性）
const saveDir = join(import.meta.dirname, '..', '..', 'data', 'saves', inst.instance);
for (const f of ['meta.json', 'init.json', 'turns.jsonl', 'snapshot_t1.json'])
  if (!existsSync(join(saveDir, f))) fail('存档目录缺 ' + f + '（' + saveDir + '）');
const turnLines = readFileSync(join(saveDir, 'turns.jsonl'), 'utf8').trim().split('\n');
const rec = JSON.parse(turnLines[0]);
if (!rec.rng_at_resolve && rec.rng_at_resolve !== 0) fail('回合记录没记随机源起点: ' + turnLines[0]);
log('   turns.jsonl 第 1 行 op 形状:', JSON.stringify((rec.red[0] || {}).op), 'rng_at_resolve', rec.rng_at_resolve);

const liveFp = (await (await fetch(API + '/api/room/' + room)).json()).fingerprint;
const loaded = await (await fetch(API + '/api/saves/load', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ instance: inst.instance }),
})).json();
if (!loaded.code) fail('载入存档失败: ' + JSON.stringify(loaded));
if ((loaded.replay_warnings || []).length) fail('重放指纹不符: ' + loaded.replay_warnings[0]);
if (loaded.resumed_turn !== turnNow) fail(`resumed_turn 期望 ${turnNow} got ${loaded.resumed_turn}`);
if (loaded.fingerprint !== liveFp) fail(`载入指纹 ${loaded.fingerprint} ≠ 房间指纹 ${liveFp}`);
log('8. 存档往返指纹一致', loaded.fingerprint, '重放回合', JSON.stringify(loaded.replayed_turns));

// 7c. 尾部重放：补令不抽随机数，但结算要落随机源起点 —— 重放只跑指令，rng_at_resolve 拨不回原位就会从第一次抖动开始分叉
let liveTurn = turnNow;
dir.send({ type: 'director_cmd', action: 'ai', side: 'blue', on: true });
await sleep(300);
for (let i = 0; i < 3; i++) {
  const before = dir.msgs.filter(m => m.type === 'state').length;
  dir.send({ type: 'resolve' });
  const arr = await dir.waitFor('state', before + 1, 15000);
  liveTurn = arr[arr.length - 1].turn;
}
log('   AI 代管蓝方后连进 3 回合，当前回合', liveTurn);
for (const f of ['snapshot_t3.json'])
  if (!existsSync(join(saveDir, f))) fail('每 3 回合应自动落一份快照，缺 ' + f);
const lines = readFileSync(join(saveDir, 'turns.jsonl'), 'utf8').trim().split('\n');
if (lines.length !== 4) fail('turns.jsonl 期望 4 行（回合 1-4），实际 ' + lines.length);
if (lines.some(l => !JSON.parse(l).rng_at_resolve && JSON.parse(l).rng_at_resolve !== 0))
  fail('有回合记录没记随机源起点');

const liveFp2 = (await (await fetch(API + '/api/room/' + room)).json()).fingerprint;
const loaded2 = await (await fetch(API + '/api/saves/load', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ instance: inst.instance }),
})).json();
if (!loaded2.code) fail('尾部重放载入失败: ' + JSON.stringify(loaded2));
if ((loaded2.replay_warnings || []).length) fail('尾部重放指纹不符: ' + loaded2.replay_warnings[0]);
if (loaded2.snapshot_turn !== 3) fail('期望从快照 t3 续跑，got ' + loaded2.snapshot_turn);
if (JSON.stringify(loaded2.replayed_turns) !== '[4]')
  fail('期望只重放快照之后的 [4]，got ' + JSON.stringify(loaded2.replayed_turns));
if (loaded2.resumed_turn !== liveTurn) fail(`resumed_turn 期望 ${liveTurn} got ${loaded2.resumed_turn}`);
if (loaded2.fingerprint !== liveFp2) fail(`尾部重放指纹 ${loaded2.fingerprint} ≠ 房间指纹 ${liveFp2}`);
log('8b. 尾部重放 ok：快照 t' + loaded2.snapshot_turn + ' 重放', JSON.stringify(loaded2.replayed_turns), '指纹', loaded2.fingerprint);

// 8c. 战报因子链（doc M6「系数逐项随战报下发」）：每场交战要带满 12 个系数 + cf 乘积
// 剧本两军初始相距 7+ 格，等 AI 自己走进射程要占掉好几个回合、哪一回合开打也不固定 ——
// 这里改由红方终端下合法指令把格距压到 ≤2 再突击，顺带把人工指令链路也覆盖上。
const FACTOR_KEYS = ['base', 'terrain', 'fort', 'strength', 'supply', 'morale',
  'personnel', 'commander', 'weather', 'air', 'counter', 'ammo'];
const IMPASSABLE = new Set(['water', 'void']);

// odd-r 偏移坐标：奇数行右移半格，邻居表按行奇偶分两套（与 Hex.cs 同序）
const neighborsOf = ([q, r]) => ((r & 1) === 0
  ? [[1, 0], [0, -1], [-1, -1], [-1, 0], [-1, 1], [0, 1]]
  : [[1, 0], [1, -1], [0, -1], [-1, 0], [0, 1], [1, 1]]
).map(([dq, dr]) => [q + dq, r + dr]);

const hexDist = (a, b) => {
  const aq = (q, r) => q - (r - (r & 1)) / 2;
  const [x1, x2] = [aq(a[0], a[1]), aq(b[0], b[1])];
  return Math.max(Math.abs(x1 - x2), Math.abs(a[1] - b[1]), Math.abs(x1 + a[1] - x2 - b[1]));
};

// 机动预算裁剪与服务端同源：LibraryService.STANDARD_TERRAIN 的 Cost + TurnResolver.StepCost 修正。
// 未知兵种落 2.0（对应引擎 GetValueOrDefault）；剧本若覆盖地形定义，这里只会保守地少走几步，不会误判成可走。
const TERRAIN_COST = {
  clear: { foot: 1.0, horse: 0.8, motor: 1.4, mech: 1.2 },
  rough: { foot: 1.3, horse: 1.6, motor: 2.4, mech: 1.8 },
  woods: { foot: 1.5, horse: 2.0, motor: 2.6, mech: 2.2 },
  hills: { foot: 1.6, horse: 1.8, motor: 2.8, mech: 2.2 },
  mountain: { foot: 2.2, horse: 2.6, motor: 4.0, mech: 3.2 },
  city: { foot: 1.4, horse: 1.8, motor: 2.0, mech: 1.9 },
  swamp: { foot: 2.0, horse: 2.6, motor: 3.6, mech: 3.0 },
  river: { foot: 2.6, horse: 3.0, motor: 5.0, mech: 4.2 },
  water: { foot: 9.0, horse: 9.0, motor: 9.0, mech: 9.0 },
  void: { foot: 1.0, horse: 1.0, motor: 1.0, mech: 1.0 },
};
const stepCost = (cell, cls) => {
  const heavy = cls === 'motor' || cls === 'mech';
  let step = (TERRAIN_COST[cell.terrain] ?? {})[cls] ?? 2.0;
  if (cell.road) step *= 0.65;
  if (cell.railway && heavy) step *= 0.8;
  if (cell.tunnel) step *= heavy ? 1.5 : 1.2;
  if (cell.river && !cell.bridge) step += 2.0;
  return step;
};
// 指挥系数×载重系数的下限是 0.6×0.6；按 0.6 折估算是「保底能走完」的规划预算
const PLAN_FACTOR = 0.6;

// 明牌：红方终端拿得到蓝方坐标才谈得上定向机动；关掉 AI 代管，改由人（脚本）下令
dir.send({ type: 'director_cmd', action: 'fog', on: false });
for (const s of ['red', 'blue']) dir.send({ type: 'director_cmd', action: 'ai', side: s, on: false });
await sleep(400);

const nearestBlue = (a, blues) => blues
  .map(b => ({ b, d: hexDist([a.q, a.r], [b.q, b.r]) }))
  .sort((x, y) => x.d - y.d || x.b.id.localeCompare(y.b.id))[0];

// 贪心步步逼近：每步取一个能缩短格距、可通行、付得起的邻居；终点格留给目标敌人本身
const planPath = (a, tgt, cellOf, us, budget, board) => {
  const cls = a.cls === 'air' ? 'mech' : a.cls;
  const path = [[a.q, a.r]];
  let spent = 0;
  for (;;) {
    const cur = path[path.length - 1];
    const nxt = neighborsOf(cur)
      .filter(h => h[0] >= 0 && h[1] >= 0 && h[0] < board.w && h[1] < board.h)
      .map(h => ({ h, cell: cellOf.get(`${h[0]},${h[1]}`) }))
      .filter(x => x.cell && !IMPASSABLE.has(x.cell.terrain))
      // 敌方地面格不进路径：校验按「下令方看得见谁」判非法，接触交给 d≤2 的突击指令
      .filter(x => !us.some(o => o.status !== 'destroyed' && o.side !== 'red'
        && o.q === x.h[0] && o.r === x.h[1]))
      .map(x => ({ ...x, cost: stepCost(x.cell, cls) }))
      .filter(x => spent + x.cost <= budget)
      .sort((x, y) => hexDist(x.h, tgt) - hexDist(y.h, tgt)
        || x.cost - y.cost || `${x.h}`.localeCompare(`${y.h}`))[0];
    if (!nxt || hexDist(nxt.h, tgt) >= hexDist(cur, tgt)) break;
    spent += nxt.cost;
    path.push(nxt.h);
  }
  return path;
};

let battles = [];
let rejected = [];
const stalled = new Set(); // 上一回合零位移的单位换掉：同一格没桥的河连续撞 10 次只会耗尽 max_turns
for (let i = 0; i < 10 && !battles.length; i++) {
  const st = dir.msgs.filter(m => m.type === 'state').pop();
  const us = st.view.units || [];
  const alive = (u) => u.status !== 'destroyed';
  const cellOf = new Map((st.view.cells || []).map(c => [`${c.q},${c.r}`, c]));
  const blues = us.filter(u => u.side === 'blue' && alive(u));
  const budgetOf = (u) => Math.round(u.mobility * PLAN_FACTOR
    * (st.view.weather?.mobility ?? 1) * (st.view.day?.recon ?? 1));
  const reds = us.filter(u => u.side === 'red' && alive(u) && u.mobility > 0 && u.firepower > 0);
  if (!reds.length || !blues.length) fail('没有可机动的红方单位或蓝方目标，无法制造交战');

  // 卡过一轮的单位换掉：同一格没桥的河连续撞 10 次只会耗尽 max_turns。全卡住时再给一次机会
  const fresh = reds.filter(u => !stalled.has(u.id));
  const cands = (fresh.length ? fresh : reds)
    .map(a => ({ a, ...nearestBlue(a, blues) }))
    .sort((x, y) => x.d - y.d || x.a.id.localeCompare(y.a.id))
    .slice(0, 6);
  if (!cands.length) fail('红方没有可机动的单位，无法制造交战');

  const planned = new Map();
  const gambles = [];
  const cmds = [];
  for (const { a, b, d } of cands) {
    if (d <= 2) { cmds.push({ op: 'attack', unit: a.id, target: b.id }); continue; }
    const path = planPath(a, [b.q, b.r], cellOf, us, budgetOf(a),
      { w: st.view.width, h: st.view.height });
    if (path.length >= 2) {
      planned.set(a.id, path);
      cmds.push({ op: 'move', unit: a.id, path });
      continue;
    }
    // 保底预算一步都付不起（多半是没桥的河）：改按服务端真实预算赌一次，走不动下回合换单位
    const lazy = planPath(a, [b.q, b.r], cellOf, us, Infinity,
      { w: st.view.width, h: st.view.height });
    if (lazy.length >= 2) {
      planned.set(a.id, lazy);
      gambles.push(a.id);
      cmds.push({ op: 'move', unit: a.id, path: lazy });
    }
  }
  if (!cmds.length) fail(`${cands.map(x => x.a.id).join('/')} 一步都规划不出来，无法制造交战`);

  const posBefore = new Map(us.map(u => [u.id, `${u.q},${u.r}`]));
  red.send({ type: 'orders', replace: true, commands: cmds });
  await sleep(250);
  const eventsBefore = dir.msgs.filter(m => m.type === 'events').length;
  const before = dir.msgs.filter(m => m.type === 'state').length;
  dir.send({ type: 'resolve' });
  const arr = await dir.waitFor('state', before + 1, 15000);
  const evArr = await dir.waitFor('events', eventsBefore + 1, 5000);
  rejected = evArr[evArr.length - 1].rejected || [];
  const framesBefore = dir.msgs.filter(m => m.type === 'frames').length;
  dir.send({ type: 'frames' });
  const fArr = await dir.waitFor('frames', framesBefore + 1, 8000);
  battles = (fArr[fArr.length - 1].frames || []).flatMap(f => f.battles || []);

  const now = arr[arr.length - 1].view.units;
  const at = (list, id) => { const u = list.find(x => x.id === id); return u && `${u.q},${u.r}`; };
  const moved = [...planned.keys()].filter(id => at(now, id) && at(now, id) !== posBefore.get(id));
  for (const id of planned.keys()) if (!moved.includes(id)) stalled.add(id);
  const nowBlues = now.filter(x => x.side === 'blue' && alive(x));
  const gap = Math.min(...now.filter(x => x.side === 'red' && alive(x))
    .map(x => nearestBlue(x, nowBlues)?.d ?? 99));
  log(`   交战尝试 ${i + 1}：${cmds.length} 令（含赌注 ${gambles.length}，位移 ${moved.length}：${moved.join('/') || '无'}），` +
    `最近格距 ${gap}，被拒 ${rejected.length}，累计战报 ${battles.length}`);
  if (!battles.length && stalled.size)
    log(`     原地未动：${[...stalled].join('/')} —— 首格算不进机动预算（多为无桥河）`);
  if (!battles.length && arr[arr.length - 1].over) fail('对局提前结束仍无交战');
}
if (!battles.length)
  fail(`红方人工机动 + 突击 10 回合仍没有交战，最后一次被拒：${JSON.stringify(rejected)}`);
for (const b of battles) {
  for (const key of ['factors_atk', 'factors_def']) {
    const f = b[key];
    if (!f) fail(`战报缺 ${key}（回合 ${b.turn} ${b.atk}→${b.def}）`);
    for (const k of FACTOR_KEYS)
      if (typeof f[k] !== 'number') fail(`${key}.${k} 不是数值（回合 ${b.turn}）`);
    const product = FACTOR_KEYS.reduce((acc, k) => acc * f[k], 1);
    if (Math.abs(product - f.cf) > Math.max(0.01, 0.02 * f.cf))
      fail(`${key}.cf=${f.cf} 与因子乘积 ${product.toFixed(3)} 对不上（回合 ${b.turn}）`);
  }
  for (const k of ['atk_loss', 'def_loss', 'routed'])
    if (b[k] === undefined) fail('战报缺字段 ' + k);
}
log('8c. 战报因子链 ok：', battles.length, '场交战，种类',
  [...new Set(battles.map(b => b.kind))].join('/'), '示例 CF',
  battles[0].atk_cf.toFixed(1), ':', battles[0].def_cf.toFixed(1));

// 8d. 全 AI 自动对抗（doc「AI 副官…可代管缺席阵营或全 AI 自动对抗」）：
//     两边都托管后必须由服务端自己打完，且同种子两局要落到同一个终局指纹 ——
//     这一条同时锁 P1 确定性、P3 七阶段结算与 P3-d 副官（任何一侧偷偷读敌方真值都会让两局分叉）。
async function autoBattle(tag) {
  const created = await (await fetch(API + '/api/instances', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ scenario: 'rhine_corridor', name: 'ws-smoke-ai-' + tag, seed: 731001 }),
  })).json();
  if (!created.code) fail('自动对抗副本创建失败: ' + JSON.stringify(created));
  const d = await connect(`?room=${created.code}&role=director`);
  await d.waitFor('welcome');
  const s0 = (await d.waitFor('state'))[0];
  const maxTurns = s0.view.max_turns || 14;

  for (const side of ['red', 'blue']) d.send({ type: 'director_cmd', action: 'ai', side, on: true });
  await sleep(400);

  const t0 = Date.now();
  let state = s0;
  for (let i = 0; i < maxTurns + 2 && !state.over; i++) {
    const before = d.msgs.filter(m => m.type === 'state').length;
    d.send({ type: 'resolve' });
    const arr = await d.waitFor('state', before + 1, 20000);
    state = arr[arr.length - 1];
  }
  const ms = Date.now() - t0;
  const info = await (await fetch(API + '/api/room/' + created.code)).json();
  d.ws.close();
  return { ms, turn: state.turn, over: state.over === true, winner: state.winner, fp: info.fingerprint };
}

const runA = await autoBattle('a');
const runB = await autoBattle('b');
if (!runA.over) fail(`双 AI 打完 ${runA.turn} 回合仍未分胜负（max_turns 14），副官僵住了`);
if (runA.fp !== runB.fp) fail(`同种子双 AI 终局指纹不一致：${runA.fp} vs ${runB.fp}`);
if (runA.ms > 5000) fail(`14 回合双 AI 自动对抗耗时 ${runA.ms}ms，超过 5s 门槛`);
log('8d. 全 AI 自动对抗 ok：', runA.turn, '回合，胜方', runA.winner || '平',
  '耗时', runA.ms + 'ms', '两局终局指纹一致', runA.fp);

// 7. takeover: second red terminal with force=1 kicks the first
const red2 = await connect(`?room=${room}&role=red&force=1`);
try {
  await red.waitFor('kicked', 1, 5000);
  log('9. first red kicked by takeover, ok');
} catch (e) { fail(e.message); }
// occupied without force should be rejected once slot open again
log('   red2 state kind:', (await red2.waitFor('state'))[0].view.kind);

// 8. director_cmd weather + fog
dir.send({ type: 'director_cmd', action: 'fog', on: false });
await sleep(400);
const dState = dir.msgs.filter(m => m.type === 'state').pop();
log('10. fog off, view fog field:', dState.view.fog ?? 'n/a');

// 8b. 明牌不留档案（§11.9）：关雾时红方看得见全部敌人，重开雾后不能留下这份永久档案
// 只数存活敌人 —— 导演部视图连打光的单位也带，SideView 的 contacts 不会给他们建档
const enemyCount = (dState.view.units || []).filter(
  u => u.side !== 'red' && u.side !== 'neutral' && u.status !== 'destroyed').length;
const offState = red2.msgs.filter(m => m.type === 'state').pop();
log('   明牌红方 contacts:', (offState.view.contacts || []).length, '/ 敌方单位', enemyCount);
if ((offState.view.contacts || []).length !== enemyCount)
  fail('fog off should show every enemy in the side view');
dir.send({ type: 'director_cmd', action: 'fog', on: true });
const redStatesBefore = red2.msgs.filter(m => m.type === 'state').length;
const onArr = await red2.waitFor('state', redStatesBefore + 1, 5000);
const onState = onArr[onArr.length - 1];
const back = (onState.view.contacts || []).length;
log('   重开雾红方 contacts:', back, 'fog field:', onState.view.fog);
log('   重开雾红方 contacts:', back);
if (back >= enemyCount) fail('fog re-enabled but the omniscient archive survived (' + back + '/' + enemyCount + ')');

// 9. non-director resolve rejected
blue.send({ type: 'resolve' });
const [err] = await blue.waitFor('error');
log('11. blue resolve rejected:', err.message);

dir.ws.close(); red2.ws.close(); blue.ws.close();
log('ALL PASS');
process.exit(0);
