// /ws protocol smoke test
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
const room = 'SMOKE' + Date.now().toString(36);

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

// 5. frames
red.send({ type: 'frames' });
log('6. frames count:', (await red.waitFor('frames'))[0].frames.length);

// 6. save
const [saved] = await (async () => { dir.send({ type: 'save', name: 'ws-smoke' }); return dir.waitFor('saved'); })();
log('7. saved id', saved.meta.id, 'turn', saved.meta.turn, 'size', saved.meta.file_size);

// 7. takeover: second red terminal with force=1 kicks the first
const red2 = await connect(`?room=${room}&role=red&force=1`);
try {
  await red.waitFor('kicked', 1, 5000);
  log('8. first red kicked by takeover, ok');
} catch (e) { fail(e.message); }
// occupied without force should be rejected once slot open again
log('   red2 state kind:', (await red2.waitFor('state'))[0].view.kind);

// 8. director_cmd weather + fog
dir.send({ type: 'director_cmd', action: 'fog', value: false });
await sleep(300);
const dState = dir.msgs.filter(m => m.type === 'state').pop();
log('9. fog off, view fog field:', dState.view.fog ?? 'n/a');

// 9. non-director resolve rejected
blue.send({ type: 'resolve' });
const [err] = await blue.waitFor('error');
log('10. blue resolve rejected:', err.message);

dir.ws.close(); red2.ws.close(); blue.ws.close();
log('ALL PASS');
process.exit(0);
