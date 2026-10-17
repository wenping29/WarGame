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

// 7c. 尾部重放：AI 代管方在结算时也要抽随机数，重放必须把随机源拨回 rng_at_resolve 才能对上指纹
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
// 剧本两军初始相隔 7+ 格，而 AI 一断供就全员休整（补给曲线是 P3 #64 的活），
// 等 AI 自己走进射程不可靠 —— 这里由红方终端下合法指令把格距压到 ≤2 再突击。
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

// 明牌：红方终端拿得到蓝方坐标才谈得上定向机动；关掉 AI 代管，改由人（脚本）下令
dir.send({ type: 'director_cmd', action: 'fog', on: false });
for (const s of ['red', 'blue']) dir.send({ type: 'director_cmd', action: 'ai', side: s, on: false });
await sleep(400);

let battles = [];
let rejected = [];
for (let i = 0; i < 10 && !battles.length; i++) {
  const st = dir.msgs.filter(m => m.type === 'state').pop();
  const us = st.view.units || [];
  const alive = (u) => u.status !== 'destroyed';
  const cellOf = new Map((st.view.cells || []).map(c => [`${c.q},${c.r}`, c]));
  const reds = us.filter(u => u.side === 'red' && alive(u) && u.mobility > 0 && u.firepower > 0);
  const blues = us.filter(u => u.side === 'blue' && alive(u));
  if (!reds.length || !blues.length) fail('没有可机动的红方单位或蓝方目标，无法制造交战');

  let pair = null;
  for (const a of reds) for (const b of blues) {
    const d = hexDist([a.q, a.r], [b.q, b.r]);
    if (!pair || d < pair.d) pair = { a, b, d };
  }

  const tgt = [pair.b.q, pair.b.r];
  const cmds = [];
  if (pair.d <= 2) {
    cmds.push({ op: 'attack', unit: pair.a.id, target: pair.b.id });
  } else {
    // 贪心步步逼近：每步取一个能缩短格距、可通行、且不被敌军占据的邻居
    const path = [[pair.a.q, pair.a.r]];
    for (let s = 0; s < 6; s++) {
      const cur = path[path.length - 1];
      const nxt = neighborsOf(cur)
        .filter(h => h[0] >= 0 && h[1] >= 0 && h[0] < st.view.width && h[1] < st.view.height)
        .filter(h => { const c = cellOf.get(`${h[0]},${h[1]}`); return c && !IMPASSABLE.has(c.terrain); })
        .filter(h => !us.some(o => alive(o) && o.side !== 'red' && o.q === h[0] && o.r === h[1]))
        .sort((x, y) => hexDist(x, tgt) - hexDist(y, tgt))[0];
      if (!nxt || hexDist(nxt, tgt) >= hexDist(cur, tgt)) break;
      path.push(nxt);
    }
    if (path.length < 2) fail(`${pair.a.id} 朝 ${pair.b.id} 找不到合法移动方向`);
    cmds.push({ op: 'move', unit: pair.a.id, path });
  }

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
  log(`   交战尝试 ${i + 1}：${cmds[0].op} ${pair.a.id}(${pair.d}格) → ${pair.b.id}，` +
    `被拒 ${rejected.length}，累计战报 ${battles.length}`);
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
