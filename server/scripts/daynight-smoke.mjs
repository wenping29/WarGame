/**
 * 日夜交替 E2E 冒烟：自定义夜幕窗口（21–6）+ 每回合 5 小时。
 * 开局 9 时昼间 → 每结算一回合 +5h：14 昼 / 19 黄昏 / 0 夜 / 5 夜 / 10 昼 …
 * 断言各回合 state 的 hour 与 day.label 与窗口推算一致。
 * 用法: node daynight-smoke.mjs [baseUrl]
 */
import WebSocket from 'ws'

const base = process.argv[2] || 'http://localhost:5018'
let pass = 0, fail = 0
const ok = (cond, name) => { cond ? pass++ : fail++; console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}`) }

const res = await fetch(`${base}/api/instances`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    scenario: 'rhine_corridor',
    name: '日夜交替冒烟',
    seed: 7,
    overrides: {
      clock: { start_hour: 9, night_from: 21, night_to: 6 },
      rules: { turn_hours: 5 },
    },
  }),
})
const created = await res.json()
ok(res.ok && created.code, `创建副本（code=${created.code || created.error}）`)

const ws = new WebSocket(`ws://${new URL(base).host}/ws?room=${created.code}&role=director`)
const queue = []
let waiter = null
ws.on('message', d => {
  const msg = JSON.parse(d.toString())
  if (waiter && queue.length === 0) { const w = waiter; waiter = null; w(msg) }
  else queue.push(msg)
})
const recv = (ms = 8000) => new Promise((resolve, reject) => {
  if (queue.length) return resolve(queue.shift())
  const t = setTimeout(() => { waiter = null; reject(new Error('recv timeout')) }, ms)
  waiter = msg => { clearTimeout(t); resolve(msg) }
})
const send = o => ws.send(JSON.stringify(o))
const until = async pred => {
  for (let i = 0; i < 60; i++) {
    const m = await recv()
    if (pred(m)) return m
  }
  throw new Error('until: 未等到目标消息')
}
const stateOf = m => m.type === 'state' ? m.view : null

await recv(4000) // hello
const st0 = await until(m => stateOf(m)?.turn === 1)
ok(st0.view.hour === 9 && st0.view.day?.id === 'day', `开局 9 时昼间（实际 ${st0.view.hour} 时 ${st0.view.day?.id}）`)

// 期望时间线：结算后小时 → 相位（夜幕 21–6：拂晓 6–9 / 昼 9–18 / 黄昏 18–21 / 夜 21–6）
const expect = [
  [2, 14, 'day'], [3, 19, 'dusk'], [4, 0, 'night'], [5, 5, 'night'], [6, 10, 'day'],
]
for (const [turn, hour, phase] of expect) {
  send({ type: 'resolve' })
  const st = await until(m => stateOf(m)?.turn === turn)
  ok(st.view.hour === hour && st.view.day?.id === phase,
    `T${turn} → ${hour} 时 ${phase}（实际 ${st.view.hour} 时 ${st.view.day?.id}）`)
}

// 非法窗口被 POST /api/validate 拒绝
const bad = await fetch(`${base}/api/validate`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    scenario: 'rhine_corridor',
    overrides: { clock: { night_from: 21, night_to: 21 } },
  }),
})
const badBody = await bad.json()
const errs = JSON.stringify(badBody)
ok(errs.includes('夜幕起止相同'), `起止相同的夜幕被校验拒绝`)

ws.close()
console.log(`\n${pass} PASS / ${fail} FAIL`)
process.exit(fail ? 1 : 0)
