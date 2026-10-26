// 浏览器 E2E 配套脚本：以红方身份经 vite /ws 代理进出房间，验证草案/就绪/屏障自动结算。
// 用法: node red-barrier.mjs <ROOM_CODE> [stage]
//   stage=draft  只建草案并回执，不 ready
//   stage=ready  建草案 + ready（对端在线或 AI 托管时触发自动结算）
//   stage=force  建草案前先把蓝方切到 AI 代管（导演指令），再 ready 触发屏障自动结算
const code = process.argv[2]
const stage = process.argv[3] || 'ready'
if (!code) { console.log('usage: node red-barrier.mjs CODE [draft|ready|force]'); process.exit(1) }

const ws = new WebSocket(`ws://localhost:5173/ws?room=${code}&role=red&name=RedNode`)
let states = []
let msgs = []
ws.onmessage = ev => { const m = JSON.parse(ev.data); msgs.push(m); if (m.type === 'state') states.push(m) }
const wait = () => new Promise(r => setTimeout(r, 800))
async function waitForState(pred, ms = 6000) {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    const s = states.find(pred)
    if (s) return s
    await wait()
  }
  return null
}
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
await wait()

const side = () => states[states.length - 1]?.view
const s0 = side()
if (s0?.kind !== 'side') { console.log('FAIL: not side view', s0?.kind); process.exit(1) }
console.log('side ok: own=', s0.own.length, 'turn=', s0.turn, 'ai_managed=', s0.ai_managed)

const u = s0.own.find(x => x.status !== 'destroyed' && !x.hq)
// 找一个相邻可走格：odd-r 偏移邻接表（与服务端 Hex.DirectionsFor 一致）
const PASSABLE = new Set(['clear', 'rough', 'woods', 'hills', 'city'])
const occupied = new Set(s0.own.map(x => `${x.q},${x.r}`))
const DIRS_EVEN = [[1, 0], [0, -1], [-1, -1], [-1, 0], [-1, 1], [0, 1]]
const DIRS_ODD = [[1, 0], [1, -1], [0, -1], [-1, 0], [0, 1], [1, 1]]
const nbr = (u.r & 1 ? DIRS_ODD : DIRS_EVEN)
  .map(([dq, dr]) => [u.q + dq, u.r + dr])
  .find(([q, r]) => {
    const c = s0.cells.find(cc => cc.q === q && cc.r === r)
    return c && PASSABLE.has(c.terrain) && !c.river && !occupied.has(`${q},${r}`)
  })
if (!nbr) { console.log('FAIL: no adjacent cell'); process.exit(1) }
console.log(`move target: unit ${u.id} ${u.q},${u.r} -> ${nbr[0]},${nbr[1]} (row parity ${u.r & 1})`)

const cmd = { op: 'move', unit: u.id, path: [[u.q, u.r], nbr] }
ws.send(JSON.stringify({ type: 'orders', replace: true, commands: [cmd] }))
const echo = await waitForState(m => m.view?.kind === 'side' && m.view?.draft?.some(d => d.unit === u.id))
if (!echo) { console.log('FAIL: draft not echoed'); process.exit(1) }
console.log('draft echo ok:', JSON.stringify(echo.view.draft))

if (stage === 'draft') { console.log('OK (draft only)'); process.exit(0) }

const turnBefore = echo.view.turn
ws.send(JSON.stringify({ type: 'ready', value: true }))
if (stage === 'force') {
  // force=1 接管导演部（浏览器若占着该位会被顶下线，属预期），再强制结算
  await wait()
  const dws = new WebSocket(`ws://localhost:5173/ws?room=${code}&role=director&name=ForceNode&force=1`)
  dws.onopen = () => dws.send(JSON.stringify({ type: 'resolve' }))
}
const advanced = await waitForState(m => m.view?.kind === 'side' && m.view?.turn > turnBefore, 15000)
if (advanced) {
  const after = advanced.view.own.find(x => x.id === u.id)
  const landed = `${after?.q},${after?.r}`
  const want = `${nbr[0]},${nbr[1]}`
  if (landed !== want) {
    console.log(`FAIL: turn advanced but unit stayed: at ${landed}, expected ${want}`,
      'errors=', JSON.stringify(advanced.view.errors || []))
    process.exit(1)
  }
  console.log('resolve ok: turn', turnBefore, '->', advanced.view.turn, 'unit moved to', landed)
  process.exit(0)
}
const errs = side()?.errors || []
console.log('FAIL: turn not advanced. ready=', side()?.ready, 'errors=', JSON.stringify(errs))
process.exit(1)
