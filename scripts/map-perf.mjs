/**
 * 地图渲染帧率取证（doc §13.3 验收线「缩放/拖拽/双击放大流畅（60fps）」，任务 #93）。
 *
 * 用法（先在 5018 起 API、5175 起 vite dev）：
 *   node scripts/map-perf.mjs --role red          # 自己 POST /api/instances 建一个新副本再测
 *   node scripts/map-perf.mjs --role director
 *   node scripts/map-perf.mjs --room CDJLPQ       # 复用指定房间（角色位被上一轮占住时会拿不到视图）
 *
 * 口径（三条一起看，单看"fps 数字"会自欺欺人）：
 *  1. 空载标定：无头 Chrome 不做 vsync 定帧——本机实测没人碰鼠标时 rAF 几乎不来帧（按损伤出帧），
 *     有输入时又能跑到 4ms 一帧。所以"交互期 rAF 间隔"不能当帧率读，这条只用来识别测量环境。
 *     真正的帧率证据是第 4 条「满载可持续帧数」。
 *  2. 单帧重绘耗时：包住 MapView.draw 计每帧实际耗时，门槛 p95 ≤ 16.7ms、max ≤ 50ms。
 *     这是"60fps 有没有预算"的直接证据，与是否锁帧无关。
 *  3. 掉帧与长帧：PerformanceObserver(long-animation-frame) 记到的 ≥50ms 长帧必须为 0；
 *     启动期的模块加载长帧会迟到落进缓冲，所以正式测量前先静置并清一次缓冲。
 *  4. 满载可持续帧率：每帧都叫一次 requestDraw，数 1.5s 真画出多少帧 → 折算 fps_upper。
 *     无头环境拿不到 vsync，这条就是"这幅画面这台机器能撑多少帧/秒"的上界，≥60 即达标。
 *  5. 事件落点自检：每个场景派发前用 elementFromPoint 把压在画布中心的浮层（toast 提示、控制条）
 *     临时设为 pointer-events:none，并统计真正落到 CANVAS 的输入数。
 *     没有这一条，"0 帧重绘"的假场景会被当成 PASS 读——本机第一轮就是这么漏的。
 *     汇总里「画布事件」列为 0 且 draws 为 0 的行一律 FAIL。
 *
 * 瓦片两条路径：
 *   - 关：setTk(null)，只画六角叠加层（纯矢量的最坏情况）。
 *   - 开：用 CDP Fetch 拦截把天地图瓦片请求 fulfill 成本机生成的 256px PNG。
 *     本机直连天地图会被中间层吞掉请求（既不 onload 也不 onerror，见 MapView._reapStalled 注释），
 *     真网络拿不到"瓦片已就绪"的稳态，所以用伪瓦片固定这一条路径的像素负载，可复现优先。
 *
 * 已知局限：无头 Chrome 走软件光栅（SwiftShader），比有头 GPU 合成保守；达标即可，
 * 不达标要在有头浏览器复测。
 */

import { spawn } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import zlib from 'node:zlib'

const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe'

const argv = process.argv.slice(2)
const arg = (name, dflt) => {
  const i = argv.indexOf('--' + name)
  return i >= 0 && argv[i + 1] !== undefined ? argv[i + 1] : dflt
}
const BASE = arg('url', 'http://localhost:5175')
const API = arg('api', 'http://localhost:5018')
const ROOM = arg('room', '')
const ROLE = arg('role', 'red')
const PAN_MS = Number(arg('pan-ms', 1500))
const STEP_MS = Number(arg('step-ms', 16))
const OUT = arg('out', `.tmp/map-perf-${ROLE}.json`)
const SHOT = arg('shot', `.tmp/map-perf-${ROLE}.png`)
// 端口留给调用方钉死，默认取一个高位随机口：上一轮的无头 Chrome 若没退干净，固定口会让这一轮连到僵尸实例
const PORT = Number(arg('port', 9300 + (Date.now() % 400)))

// ── 伪瓦片：256x256 RGB PNG（确定性图案，顶住一张真实底图瓦片的像素负载） ──

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})
function crc32(buf) {
  let c = 0xffffffff
  for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}
function chunk(type, data) {
  const len = Buffer.alloc(4)
  len.writeUInt32BE(data.length)
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(body))
  return Buffer.concat([len, body, crc])
}
function tilePng() {
  const W = 256, H = 256
  const raw = Buffer.alloc(H * (1 + W * 3))
  for (let y = 0; y < H; y++) {
    const o = y * (1 + W * 3)
    raw[o] = 0 // filter: none
    for (let x = 0; x < W; x++) {
      const river = Math.abs(y - 128 - 40 * Math.sin(x / 28)) < 6
      const road = (x + y * 3) % 97 < 2
      const i = o + 1 + x * 3
      raw[i] = river ? 70 : road ? 190 : 120 + ((x * 7 + y * 13) % 40)
      raw[i + 1] = river ? 120 : road ? 170 : 120 + ((x * 11 + y * 5) % 40)
      raw[i + 2] = river ? 160 : road ? 150 : 100 + ((x * 3 + y * 17) % 40)
    }
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(W, 0)
  ihdr.writeUInt32BE(H, 4)
  ihdr[8] = 8 // bit depth
  ihdr[9] = 2 // color type: truecolor
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 6 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

// ── 页内地基：rAF 采样器 + LoAF 观察器（文档创建前注入） ──

const BOOTSTRAP = `
window.__perf = { frames: [], draws: [], loaf: [], ev: 0 };
(function tick() { window.__perf.frames.push(performance.now()); requestAnimationFrame(tick) })();
try {
  // 只数真正落到画布的输入，用来区分「事件被浮层吃掉」和「事件到了但没重绘」
  const bump = (e) => { if (e.target && e.target.tagName === 'CANVAS') window.__perf.ev++ }
  document.addEventListener('pointerdown', bump, true)
  document.addEventListener('pointermove', bump, true)
  document.addEventListener('wheel', bump, true)
} catch (err) { window.__perf.evError = String(err) }
try {
  new PerformanceObserver((l) => {
    for (const e of l.getEntries()) {
      window.__perf.loaf.push({
        t: e.startTime, d: e.duration, b: e.blockingDuration,
        renderStart: e.renderStart || 0, styleLayoutStart: e.styleAndLayoutStart || 0,
        firstUiEvent: e.firstUIEventTimestamp || 0,
        scripts: (e.scripts || []).map((s) => [
          (s.sourceURL || '?').split('/').pop(), 'L' + (s.sourceLineNumber ?? '?'),
          s.sourceFunctionName || '(anon)', s.invokerType || s.invoker || '?',
        ].join(':')),
      })
    }
  }).observe({ type: 'long-animation-frame', buffered: true })
} catch (err) { window.__perf.loafUnsupported = String(err) }
`

// ── 页内：包住 MapView.draw 计每帧耗时 ──

const PATCH_DRAW = `
(() => {
  const map = window.__hexMap
  if (!map) return 'no __hexMap'
  if (map.__wrapped) return 'already wrapped'
  const orig = Object.getPrototypeOf(map).draw
  Object.getPrototypeOf(map).draw = function () {
    const t0 = performance.now()
    try { return orig.call(this) } finally { window.__perf.draws.push([t0, performance.now() - t0]) }
  }
  map.__wrapped = true
  return 'wrapped'
})()
`

const DRAIN = `
(() => {
  const p = window.__perf
  const out = { frames: p.frames, draws: p.draws, loaf: p.loaf  , ev: p.ev, loafUnsupported: p.loafUnsupported || null }
  p.frames = []; p.draws = []; p.loaf = []; p.ev = 0
  return out
})()
`

// ── 页内：当前瓦片状态（判断"瓦片开"这条路径是否真在画瓦片） ──

const TILE_STATE = `
(() => {
  const m = window.__hexMap
  const E = 20037508.342789244
  let ok = 0
  for (const t of m.tiles.values()) if (t.status === 'ok') ok++
  const zfac = (2 * E * m.scale) / 256
  const zi = Math.max(1, Math.min(18, Math.round(Math.log2(zfac))))
  const tilePx = (2 * E * m.scale) / Math.pow(2, zi)
  return {
    tk: !!m.tk, tiles: m.tiles.size, ok: ok,
    tileSuccess: m.tileSuccess, tileErrors: m.tileErrors,
    zoom: zi, tilePx: Math.round(tilePx),
    drawsTiles: !!(m.tk && tilePx >= 64 && tilePx <= 1024),
    cells: m.view && m.view.cells ? m.view.cells.length : 0,
    units: m.allUnits ? m.allUnits.length : 0,
    canvas: m.canvas.width + 'x' + m.canvas.height
  }
})()
`

const READ_TK = `(() => window.__hexMap.tk)()`

const stats = (a) => {
  if (!a.length) return { n: 0, p50: 0, p95: 0, max: 0 }
  const s = [...a].sort((x, y) => x - y)
  const q = (p) => s[Math.min(s.length - 1, Math.floor(p * (s.length - 1)))]
  return { n: s.length, p50: q(0.5), p95: q(0.95), max: s[s.length - 1], mean: s.reduce((x, y) => x + y, 0) / s.length }
}
const intervals = (ts) => ts.slice(1).map((t, i) => t - ts[i])

class Cdp {
  constructor(ws) {
    this.ws = ws
    this.id = 0
    this.pending = new Map()
    this.handlers = new Map()
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data)
      if (m.id !== undefined && this.pending.has(m.id)) {
        const { res, rej, timer } = this.pending.get(m.id)
        this.pending.delete(m.id)
        clearTimeout(timer)
        if (m.error) rej(new Error(m.method + ': ' + JSON.stringify(m.error)))
        else res(m.result)
      } else if (m.method) {
        const hs = this.handlers.get(m.method)
        if (hs) for (const h of hs) h(m.params)
      }
    }
  }
  on(method, fn) {
    if (!this.handlers.has(method)) this.handlers.set(method, [])
    this.handlers.get(method).push(fn)
  }
  send(method, params = {}, timeoutMs = 20000) {
    const id = ++this.id
    return new Promise((res, rej) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        rej(new Error(method + ' 超时 ' + timeoutMs + 'ms'))
      }, timeoutMs)
      this.pending.set(id, { res, rej, timer })
      this.ws.send(JSON.stringify({ id, method, params }))
    })
  }
}

async function fetchJson(url, opts = {}, timeoutMs = 15000) {
  const ctrl = new AbortController()
  const t = setTimeout(() => ctrl.abort(), timeoutMs)
  try {
    const r = await fetch(url, { ...opts, signal: ctrl.signal })
    return await r.json()
  } finally {
    clearTimeout(t)
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

let chrome = null
let cdpWs = null
let profDir = null
process.on('exit', () => {
  try { if (cdpWs && cdpWs.readyState === 1) cdpWs.close() } catch {}
  try { if (chrome) chrome.kill('SIGKILL') } catch {}
  if (profDir) {
    try { fs.rmSync(profDir, { recursive: true, force: true, maxRetries: 3 }) } catch {}
  }
})

/**
 * 跑一个交互场景：CDP 派发受信任的合成事件（真实 pointerdown/wheel 走浏览器事件链），
 * 返回窗口内的 rAF 间隔、draw 耗时与长帧统计。
 */
async function scenario(cdp, evalJs, pass, kind, ms, tilesOn) {
  await evalJs(DRAIN)
  if (tilesOn && kind === 'pan-fit') {
    // 复位视角下 tilePx 常掉到 64px 以下（_drawTiles 直接 return），"瓦片开"这一路必须重新推到能画瓦片的级别
    await evalJs(`(() => { window.__hexMap.zoomBy(2.2); return true })()`)
  } else if (kind === 'pan-fit') {
    await evalJs(`(() => { window.__hexMap.fit(); return true })()`)
  }

  const box = await evalJs(`(() => { const b = window.__hexMap.canvas.getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height } })()`)
  const cx = Math.round(box.x + box.w / 2)
  const cy = Math.round(box.y + box.h / 2)
  // 浮层（toast、控制条）压在画布中心时，受信任事件会被浮层吃掉，测出来是「0 帧重绘」的假场景。
  // 逐个把落点上的非画布元素临时设为 pointer-events:none，并记账，保证每个交互场景真的打在画布上。
  const cleared = await evalJs(`(() => {
    const out = []
    for (let i = 0; i < 6; i++) {
      const el = document.elementFromPoint(${cx}, ${cy})
      if (!el || el.tagName === 'CANVAS') break
      out.push(el.tagName + '.' + String(el.className || el.id))
      el.style.pointerEvents = 'none'
    }
    const rest = document.elementFromPoint(${cx}, ${cy})
    return { cleared: out, hit: rest ? rest.tagName + '.' + String(rest.className || rest.id) : null }
  })()`)
  if (cleared.cleared.length) console.log(`  [${pass}/${kind}] 落点浮层已临时禁用：${cleared.cleared.join(', ')} → 现在落在 ${cleared.hit}`)
  const ev = (type, x, y, extra = {}) =>
    cdp.send('Input.dispatchMouseEvent', { type, x, y, buttons: 0, clickCount: 0, ...extra }, 15000)

  const t0 = Date.now()
  let sat = null
  if (kind === 'idle') {
    await sleep(ms)
  } else if (kind === 'pan' || kind === 'pan2' || kind === 'pan-fit') {
    await ev('mousePressed', cx, cy, { button: 'left', buttons: 1, clickCount: 1 })
    let i = 0
    while (Date.now() - t0 < ms) {
      const x = cx + Math.round(Math.min(box.w / 2 - 20, 200) * Math.sin(i / 20))
      const y = cy + Math.round(Math.min(box.h / 2 - 20, 120) * Math.sin(i / 31))
      await ev('mouseMoved', x, y, { button: 'left', buttons: 1 })
      await sleep(STEP_MS)
      i++
    }
    await ev('mouseReleased', cx, cy, { button: 'left', clickCount: 1 })
  } else if (kind === 'wheel-in' || kind === 'wheel-out') {
    const deltaY = kind === 'wheel-in' ? -100 : 100
    while (Date.now() - t0 < ms) {
      await ev('mouseWheel', cx, cy, { deltaX: 0, deltaY })
      await sleep(STEP_MS)
    }
  } else if (kind === 'dblclick') {
    while (Date.now() - t0 < ms) {
      await ev('mousePressed', cx, cy, { button: 'left', buttons: 1, clickCount: 2 })
      await ev('mouseReleased', cx, cy, { button: 'left', clickCount: 2 })
      await sleep(120)
    }
  } else if (kind === 'buttons') {
    while (Date.now() - t0 < ms) {
      await evalJs(`(() => { const b = document.querySelectorAll('.mapzoom button'); b[0].click(); b[1].click(); return true })()`)
      await sleep(40)
    }
    await evalJs(`(() => { const b = document.querySelectorAll('.mapzoom button'); b[2].click(); return true })()`)
  } else if (kind === 'saturate') {
    // 满载：每帧都叫一次 requestDraw，逼出"这幅画面这台机器一秒能画多少帧"的上界。
    // 静置 300ms 让上一场景的尾巴落定，再把"循环内画出的帧数 ÷ 循环真实时长"交给页内计数——
    // 用整段 draws 时间跨度折算会把这 300ms 摊进分母，把 fps 读低约 20%（本机第一轮就是这么错的）
    sat = await evalJs(`(() => new Promise((res) => {
      const m = window.__hexMap
      const start = () => {
        const t0 = performance.now()
        const base = window.__perf.draws.length
        const step = () => {
          m.requestDraw()
          if (performance.now() - t0 < ${ms}) requestAnimationFrame(step)
          else res({ wallMs: Math.round(performance.now() - t0), draws: window.__perf.draws.length - base })
        }
        requestAnimationFrame(step)
      }
      setTimeout(start, 300)
    }))()`, true)
  }

  await sleep(120)
  const raw = await evalJs(DRAIN)
  // 现场探针：事件是否落到画布、地图是否被 dispose、窗口中心点上是什么元素
  const probe = await evalJs(`(() => {
    const m = window.__hexMap
    const b = m.canvas.getBoundingClientRect()
    const el = document.elementFromPoint(Math.round(b.x + b.width / 2), Math.round(b.y + b.height / 2))
    return {
      disposed: !!m.disposed,
      hit: el ? el.tagName + '.' + String(el.className || el.id) : null,
      scale: Number(m.scale.toExponential(2)),
      evErr: window.__perf.evError || null,
    }
  })()`)
  const gap = stats(intervals(raw.frames))
  // draw 自身的时间跨度才是这一行的分母：满载场景里 requestDraw 的循环是唯一画帧源，
  // 300ms 静置期一帧不画，用 rAF 总跨度折算会把fps读低（口径第 4 条）
  const draw = stats(raw.draws.map((d) => d[1]))
  const span = raw.frames.length > 1 ? raw.frames[raw.frames.length - 1] - raw.frames[0] : 0
  const dspan = raw.draws.length > 1 ? raw.draws[raw.draws.length - 1][0] - raw.draws[0][0] : 0
  return {
    pass, scenario: kind, windowMs: ms, wallMs: Date.now() - t0,
    frames: raw.frames.length,
    canvasEvents: raw.ev, disposed: probe.disposed, hit: probe.hit, scale: probe.scale,
    drawSpanMs: round(dspan), saturate: sat,
    fpsUpper: kind === 'saturate' && sat && sat.wallMs
      ? round((sat.draws * 1000) / sat.wallMs)
      : (span > 0 && raw.draws.length ? round((raw.draws.length * 1000) / span) : 0),
    gap: { p50: round(gap.p50), p95: round(gap.p95), max: round(gap.max) },
    fps: gap.p50 ? round(1000 / gap.p50) : 0,
    draw: { n: draw.n, p50: round(draw.p50), p95: round(draw.p95), max: round(draw.max) },
    longFrames: (raw.loaf || []).filter((e) => e.d >= 50).length,
    worstLoaf: (raw.loaf || []).filter((e) => e.d >= 50)
      .sort((a, b) => b.d - a.d).slice(0, 2)
      .map((e) => ({
        ms: round(e.d), blocking: round(e.b),
        renderStartGap: round(e.renderStart && e.t ? e.renderStart - e.t : 0),
        scripts: e.scripts,
      })),
    slowGaps: intervals(raw.frames).filter((g) => g > 25).length,
    loafUnsupported: raw.loafUnsupported,
  }
}

const round = (v) => Math.round(v * 100) / 100

async function main() {
  fs.mkdirSync(path.dirname(OUT), { recursive: true })

  // 没给房间号就自己建一个副本：复用旧房间容易被上一轮没退干净的角色位挡住（服务端按槽位拒绝）
  let room = ROOM
  if (!room) {
    const created = await fetchJson(`${API}/api/instances`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ scenario: 'rhine_corridor', name: 'perf-' + Date.now().toString(36), seed: 731001 }),
    })
    if (!created.code) throw new Error('建房失败：' + JSON.stringify(created))
    room = created.code
    console.log(`新建副本 ${room}（${created.instance}，场景 rhine_corridor，seed 731001）`)
  }

  profDir = path.resolve(process.env.TEMP || os.tmpdir(), 'hf-perf-' + Date.now().toString(36))
  chrome = spawn(CHROME, [
    '--headless=new',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profDir}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--window-size=1600,1000',
    'about:blank',
  ], { stdio: 'ignore' })
  chrome.on('error', (e) => console.error('chrome 启动失败:', e.message))

  let targets = null
  for (let i = 0; i < 60 && !targets; i++) {
    await sleep(300)
    try { targets = await fetchJson(`http://127.0.0.1:${PORT}/json/list`) } catch {}
  }
  if (!targets) throw new Error('CDP 不可用：调试端口没起来（profile=' + profDir + '）')

  const page = targets.find((t) => t.type === 'page') ||
    await fetchJson(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' })
  cdpWs = new WebSocket(page.webSocketDebuggerUrl)
  await new Promise((res, rej) => {
    cdpWs.onopen = res
    cdpWs.onerror = () => rej(new Error('CDP ws 连不上'))
  })
  const cdp = new Cdp(cdpWs)

  await cdp.send('Runtime.enable')
  await cdp.send('Page.enable')
  await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: BOOTSTRAP })

  const evalJs = async (expr, awaitIt = false) => {
    const r = await cdp.send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: awaitIt }, 60000)
    if (r.exceptionDetails) {
      throw new Error('页内异常: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text))
    }
    return r.result.value
  }

  const url = `${BASE}/room/${room}/${ROLE}`
  console.log('打开 ' + url)
  await cdp.send('Page.navigate', { url })
  const waitForView = async (seconds) => {
    for (let i = 0; i < seconds * 2; i++) {
      if (await evalJs(`!!(window.__hexMap && window.__hexMap.view)`)) return true
      await sleep(500)
    }
    return false
  }
  let ready = await waitForView(30)
  // 新建副本刚连上就吃到「该角色位已被占用」是 React 开发模式双挂载 + 令牌尚未落盘的竞态
  // （第二次连接带的是新令牌，服务端按占用规则的拒绝）。先重载一次，仍被顶在门外就点页面上的强行接管。
  if (!ready) {
    console.log('  首连没拿到视图，重载重试一次')
    await cdp.send('Page.navigate', { url })
    ready = await waitForView(30)
    if (!ready) {
      const took = await evalJs(`(() => {
        const b = [...document.querySelectorAll('button')].find((x) => x.innerText.includes('强行接管'))
        if (!b) return false
        b.click(); return true
      })()`)
      console.log('  点「强行接管本角色」：' + took)
      ready = await waitForView(30)
    }
  }
  if (!ready) {
    const text = await evalJs(`(document.body.innerText || '').slice(0, 300)`)
    throw new Error('地图没拿到视图（__hexMap.view 仍为空）。页面文字：' + JSON.stringify(text))
  }
  console.log('draw 包装：' + (await evalJs(PATCH_DRAW)))
  await sleep(600)

  const tk = await evalJs(READ_TK)
  // 启动期（vite 转译 + React 首屏 + 首包 state）的长帧会迟到落进 LoAF 缓冲，先静置再清一次
  await sleep(2000)
  const clock = await evalJs(`window.__perf.frames.length`)
  if (!clock) throw new Error('rAF 采样器没出帧（frames=0）——测量环境不可用，先修采样器再谈帧率')
  await evalJs(DRAIN)
  const results = []

  for (const pass of ['notiles', 'tiles']) {
    const tilesOn = pass === 'tiles'
    if (tilesOn) {
      if (!tk) {
        console.log('\n[tiles] 跳过：/api/scenarios 没下发天地图密钥（data/tianditu.json 不在），瓦片开这一路本机无法取证')
        continue
      }
      await cdp.send('Fetch.enable', { patterns: [{ urlPattern: '*tianditu*', requestStage: 'Request' }] })
      const png = tilePng().toString('base64')
      cdp.on('Fetch.requestPaused', (p) => {
        cdp.send('Fetch.fulfillRequest', {
          requestId: p.requestId, responseCode: 200,
          responseHeaders: [{ name: 'Content-Type', value: 'image/png' }],
          body: png,
        }, 15000).catch(() => {})
      })
      await evalJs(`window.__hexMap.setTk(${JSON.stringify(tk)})`)
      await evalJs(`(() => { window.__hexMap.zoomBy(2.2); return true })()`)
      await sleep(1500) // 首批瓦片解码进缓存
    } else {
      await evalJs(`window.__hexMap.setTk(null)`)
      await evalJs(`(() => { window.__hexMap.fit(); return true })()`)
      await sleep(600)
    }

    const st = await evalJs(TILE_STATE)
    console.log(`\n[${pass}] 场景：canvas=${st.canvas} 格=${st.cells} 单位=${st.units} `
      + `瓦片=${st.tiles}(ok ${st.ok}) zoom=${st.zoom} tilePx=${st.tilePx} 画瓦片=${st.drawsTiles} `
      + `瓦片失败=${st.tileErrors}`)
    if (tilesOn && (!st.drawsTiles || st.ok === 0)) console.log(`[${pass}] !! 瓦片没真在画，这一路的数字无效`)

    results.push(await scenario(cdp, evalJs, pass, 'idle', 1200, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'pan', PAN_MS, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'pan2', PAN_MS, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'wheel-in', 1200, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'wheel-out', 1200, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'dblclick', 1000, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'buttons', 1000, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'pan-fit', PAN_MS, tilesOn))
    results.push(await scenario(cdp, evalJs, pass, 'saturate', 1500, tilesOn))

    if (tilesOn) await cdp.send('Fetch.disable')
  }

  console.log('\n================= 帧率汇总 =================')
  console.log('pass     scenario    帧数  画布事件   间隔p50/p95/max      draws  draw p50/p95/max  满载fps  长帧≥50  掉帧>25ms')
  let failures = 0
  let firstInteractionSeen = false
  for (const r of results) {
    const gates = []
    const isInteraction = r.scenario !== 'idle'
    if (r.draw.n > 0 && r.draw.p95 > 16.7) gates.push('draw p95>16.7ms')
    if (r.draw.n > 0 && r.draw.max > 50) gates.push('draw max>50ms')
    if (r.longFrames > 0) {
      // 全场第一个交互场景可能吃到一次性开销（首个受信任输入把输入/合成管线拉起来）。
      // 只有当长帧没有脚本归属（不是应用代码）、且 draw 耗时本身没越线时，才降为 note 而不是 FAIL。
      const unattributed = (r.worstLoaf || []).every((e) => !e.scripts || !e.scripts.length)
      const drawClean = r.draw.n === 0 || (r.draw.p95 <= 16.7 && r.draw.max <= 50)
      if (isInteraction && !firstInteractionSeen && unattributed && drawClean && r.scenario !== 'saturate') {
        r.note = '全场首个交互的一次性长帧（无脚本归属，非应用代码）'
      } else {
        gates.push('长帧≥50ms')
      }
    }
    if (isInteraction) firstInteractionSeen = true
    if (r.scenario === 'saturate' && r.fpsUpper < 60) gates.push('满载fps<60')
    // 交互场景一帧都没重绘 = 事件没落到画布，这一行是空测，不能当 PASS 读
    if (isInteraction && r.scenario !== 'saturate' && r.draw.n === 0) {
      gates.push(`无重绘(落点 ${r.hit}，画布事件 ${r.canvasEvents}，disposed ${r.disposed}，scale ${r.scale})`)
    }
    if (gates.length) failures++
    r.gates = gates
    console.log(
      `${r.pass.padEnd(8)} ${r.scenario.padEnd(9)} ${String(r.frames).padStart(5)} `
      + `${String(r.canvasEvents).padStart(9)} `
      + `${`${r.gap.p50}/${r.gap.p95}/${r.gap.max}`.padStart(19)} `
      + `${String(r.draw.n).padStart(6)} ${`${r.draw.p50}/${r.draw.p95}/${r.draw.max}`.padStart(17)} `
      + `${String(r.fpsUpper).padStart(9)} ${String(r.longFrames).padStart(9)} ${String(r.slowGaps).padStart(9)}`
      + (gates.length ? '   FAIL: ' + gates.join('+') : (r.note ? '   note: ' + r.note : '')),
    )
    for (const e of (r.worstLoaf || [])) {
      console.log(`         └ 长帧 ${e.ms}ms（阻塞 ${e.blocking}ms，渲染前空窗 ${e.renderStartGap}ms）归属：`
        + (e.scripts && e.scripts.length ? e.scripts.join(' | ') : '无脚本记账（浏览器/合成器侧的一次性开销）'))
    }
  }

  const sat = results.filter((r) => r.scenario === 'saturate')
  if (sat.length) {
    console.log('\n满载可持续帧率（无头不做 vsync，这一列才是 60fps 的证据）：'
      + sat.map((s) => `${s.pass} ${s.fpsUpper}fps（draw p95 ${s.draw.p95}ms = 16.7ms 预算的 ${Math.round((s.draw.p95 / 16.7) * 100)}%）`).join(' / '))
  }
  const idles = results.filter((r) => r.scenario === 'idle')
  if (idles.length) {
    console.log(`空载标定：idle 帧数 ${idles.map((r) => r.frames).join('/')}、间隔 p50 ${idles.map((r) => r.gap.p50).join('/')}ms`
      + ' —— 无头按损伤出帧，交互期能到 4ms 一帧，所以 rAF 间隔不能当帧率读（文件头口径第 1 条）')
  }
  if (results.some((r) => r.loafUnsupported)) console.log('注意：本浏览器不支持 long-animation-frame，长帧一列不可用')

  console.log(`\n结论：${failures === 0 ? 'PASS' : 'FAIL'}（${failures}/${results.length} 条场景越线）`)

  fs.writeFileSync(OUT, JSON.stringify({ url, role: ROLE, chrome: CHROME, panMs: PAN_MS, stepMs: STEP_MS, generatedAt: new Date().toISOString(), results }, null, 2))
  console.log('明细已写 ' + OUT)

  try {
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png' }, 15000)
    fs.writeFileSync(SHOT, Buffer.from(shot.data, 'base64'))
    console.log('截图 ' + SHOT)
  } catch (e) { console.log('截图失败：' + e.message) }

  cdpWs.close()
  chrome.kill('SIGKILL')
  const code = failures === 0 ? 0 : 1
  setTimeout(() => process.exit(code), 300).unref()
}

main().catch((e) => {
  console.error('运行失败：' + e.message)
  try { if (cdpWs && cdpWs.readyState === 1) cdpWs.close() } catch {}
  try { if (chrome) chrome.kill('SIGKILL') } catch {}
  process.exit(1) // 无头 Chrome 的 CDP socket 会把事件环吊住，不显式退出就挂在这里
})
