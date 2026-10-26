/**
 * 天气计划 E2E 冒烟：
 * 1) POST /api/instances 带 weather.schedule 覆盖 → 开局应装载计划但未启动
 * 2) 非导演部发 weather_schedule_start → 必须被拒
 * 3) 导演部 start → running，回合一落在区间内立即切天气
 * 4) resolve 一回合 → 计划按区间切换并广播 weather_schedule 事件
 * 5) pause → 冻结；set 整组替换；delete → 清空回到原模式
 * 用法: node scripts/wxsched-smoke.mjs [baseUrl]
 */
import WebSocket from 'ws'

const base = process.argv[2] || 'http://localhost:5018'
const code = 'wsched_' + Math.random().toString(36).slice(2, 8)
let pass = 0, fail = 0
const ok = (cond, name) => { cond ? pass++ : fail++; console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}`) }

const res = await fetch(`${base}/api/instances`, {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({
    scenario: 'rhine_corridor',
    name: '天气计划冒烟',
    seed: 7,
    overrides: {
      weather: {
        initial: 'clear', mode: 'fixed',
        schedule: [
          { from: 2, to: 3, weather: 'rain' },
          { from: 4, to: 6, weather: 'storm' },
        ],
      },
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
  for (let i = 0; i < 40; i++) {
    const m = await recv()
    if (pred(m)) return m
  }
  throw new Error('until: 未等到目标消息')
}
const stateOf = m => m.type === 'state' ? m.view : null

await recv(4000) // hello
const st0 = await until(stateOf)
const sched0 = stateOf(st0)?.weather_schedule
ok(sched0?.state === 'off' && sched0?.phases?.length === 2,
  `开局装载计划但未启动（state=${sched0?.state}, phases=${sched0?.phases?.length}）`)

// 非导演部必须被拒（连上后先收 hello 再收 error，须按 type 过滤）
const wsBlue = new WebSocket(`ws://${new URL(base).host}/ws?room=${created.code}&role=blue`)
wsBlue.on('message', () => {})
await new Promise(r => wsBlue.once('open', r))
const reply = await new Promise(resolve => {
  const onMsg = d => {
    const m = JSON.parse(d.toString())
    if (m.type === 'error') { wsBlue.off('message', onMsg); resolve(m) }
  }
  wsBlue.on('message', onMsg)
  wsBlue.send(JSON.stringify({ type: 'director_cmd', action: 'weather_schedule_start' }))
})
ok(reply.message?.includes('导演部'), `蓝方发 weather_schedule_start 被拒（${reply.message}）`)
wsBlue.close()

// 导演部启动 → running，当前回合（1）不在区间内，天气保持 clear
send({ type: 'director_cmd', action: 'weather_schedule_start' })
const st1 = await until(m => stateOf(m)?.weather_schedule?.state === 'running')
ok(st1.view.weather.id === 'clear', `启动即生效但回合1不在区间，天气保持（${st1.view.weather.id}）`)

// 结算一回合：Turn 1 → 2 进入 2-3 降雨区间
send({ type: 'resolve' })
const ev = await until(m => m.type === 'events' && m.events?.some(e => e.code === 'weather_schedule'))
ok(ev.events.some(e => e.code === 'weather_schedule' && e.text.includes('降雨')), `计划切换广播事件（${ev.events.find(e => e.code === 'weather_schedule')?.text}）`)
const st2 = await until(m => stateOf(m)?.turn === 2)
ok(st2.view.weather.id === 'rain', `第 2 回合天气=rain（实际 ${st2.view.weather.id}）`)

// 暂停 → 第 3 回合（仍在区间）不再切换，冻结在 rain
send({ type: 'director_cmd', action: 'weather_schedule_pause' })
await until(m => stateOf(m)?.weather_schedule?.state === 'paused')
send({ type: 'resolve' })
const st3 = await until(m => stateOf(m)?.turn === 3)
ok(st3.view.weather.id === 'rain', `暂停后第 3 回合冻结 rain（实际 ${st3.view.weather.id}）`)

// 整组替换：改成 4-5 雪；删除 → 清空
send({ type: 'director_cmd', action: 'weather_schedule_set', phases: [{ from: 4, to: 5, weather: 'snow' }] })
const st4 = await until(m => stateOf(m)?.weather_schedule?.phases?.length === 1)
ok(st4.view.weather_schedule.phases[0].weather === 'snow', `整组替换生效（T4-5 snow）`)
send({ type: 'director_cmd', action: 'weather_schedule_delete' })
const st5 = await until(m => stateOf(m)?.weather_schedule?.state === 'off' && m.view.weather_schedule.phases.length === 0)
ok(true, '删除计划回到未启动')

// 非法参数被拒
send({ type: 'director_cmd', action: 'weather_schedule_set', phases: [{ from: 5, to: 2, weather: 'rain' }] })
const err = await until(m => m.type === 'error')
ok(!!err.message, `非法区间被拒（${err.message}）`)

ws.close()
console.log(`\n${pass} PASS / ${fail} FAIL`)
process.exit(fail ? 1 : 0)
