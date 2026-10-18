/**
 * 地图渲染帧率取证（doc §13.3 验收线「缩放/拖拽/双击放大流畅（60fps）」，任务 #93）。
 *
 * 用法（需要先在 5018 起 API、5175 起 vite dev，并有一个房间）：
 *   node scripts/map-perf.mjs --room CDJLPQ --role red
 *   node scripts/map-perf.mjs --room CDJLPQ --role director
 *
 * 口径（三条都要一起看，单看"fps 数字"会自欺欺人）：
 *  1. 空载标定：先测一段没人碰鼠标的 rAF 间隔。无头 Chrome 的合成器若不锁 60Hz，
 *     这条数字会明显小于 16.7ms，此时"交互期 60fps"必须由第 2/3 条支撑，不能直接宣称。
 *  2. 单帧重绘耗时：包住 MapView.draw 计每帧实际耗时，门槛 p95 ≤ 16.7ms、max ≤ 50ms。
 *     这是"60fps 有没有预算"的直接证据，与是否锁帧无关。
 *  3. 掉帧与长帧：交互期 rAF 间隔 p95 不得超过空载 p95 的 1.5 倍；
 *     PerformanceObserver(long-animation-frame) 记到的 ≥50ms 长帧必须为 0。
 *
 * 瓦片两条路径：
 *   - 关：setTk(null)，只画六角叠加层（最坏情况的纯矢量路径）。
 *   - 开：用 CDP Fetch 拦截把天地图瓦片请求 fulfill 成本机生成的 256px PNG。
 *     本机直连天地图会被中间层吞掉请求（既不 onload 也不 onerror，见 MapView._reapStalled 注释），
 *     真网络拿不到"瓦片已就绪"的稳态，所以这里用伪瓦片固定这一条路径的像素负载，可复现优先。
 *
 * 已知局限：无头 Chrome 走软件光栅（SwiftShader），比有头 GPU 合成保守；测出来达标即可，
 * 不达标时要在有头浏览器复测。
 */

import { spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import zlib from 'node:zlib'

const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe'

const argv = process.argv.slice(2)
const arg = (name, dflt) => {
  const i = argv.indexOf('--' + name)
  return i >= 0 && argv[i + 1] !== undefined ? argv[i + 1] : dflt
}
const BASE = arg('url', 'http://localhost:5175')
const ROOM = arg('room', '')
const ROLE = arg('role', 'red')
const PAN_MS = Number(arg('pan-ms', 1500))
const OUT = arg('out', '')
const PORT = Number(arg('port', 9333))

if (!ROOM) {
  console.error('必须给 --room CODE（先用 POST /api/instances 建一个房间）')
  process.exit(2)
}

// ── 伪瓦片：256x256 RGB PNG（确定性图案，模拟一张真实底图瓦片的像素负载） ──

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

// ── 页内地基：rAF 采样器 + LoAF 观察器（在文档创建前注入） ──

const BOOTSTRAP = `
window.__perf = { frames: [], draws: [], loaf: [], started: performance.now() };
(function tick() { window.__perf.frames.push(performance.now()); requestAnimationFrame(tick) })();
try {
  new PerformanceObserver((l) => {
    for (const e of l.getEntries()) window.__perf.loaf.push({ t: e.startTime, d: e.duration, b: e.blockingDuration })
  }).observe({ type: 'long-animation-frame', buffered: true })
} catch (err) { window.__perf.loafUnsupported = String(err) }
`

// ── 页内：包住 MapView.draw 计每帧耗时 ──

const PATCH_DRAW = `
(() => {
  const map = window.__hexMap
  if (!map) return 'no __hexMap'
  if (map.__wrapped) return 'already wrapped'
  const proto = Object.getPrototypeOf(map)
  const orig = proto.draw
  proto.draw = function () {
    const t0 = performance.now()
    try { return orig.call(this) } finally { window.__perf.draws.push([t0, performance.now() - t0]) }
  }
  map.__wrapped = true
  return 'wrapped'
})()
`

// ── 页内：把 __perf 收回来并清空 ──

const DRAIN = `
(() => {
  const p = window.__perf
  const out = { frames: p.frames, draws: p.draws, loaf: p.loaf, loafUnsupported: p.loafUnsupported || null }
  p.frames = []; p.draws = []; p.loaf = []
  return out
})()
`

// ── 页内：当前瓦片状态（判断"瓦片开"这条路径是否真的在画瓦片） ──

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
    drawsTiles: !!(m.tk && m.tiles.size && tilePx >= 64 && tilePx <= 1024),
    scale: m.scale, cells: (m.view && m.view.cells ? m.view.cells.length : 0),
    units: m.allUnits ? m.allUnits.length : 0,
    canvas: m.canvas.width + 'x' + m.canvas.height,
  }
})()
`

// ── 页内：读回密钥原值，关-开之间要能精确还原 ──

const READ_TK = `(() => window.__hexMap.tk)()`

const stats = (a) => {
  if (!a.length) return { n: 0 }
  const s = [...a].sort((x, y) => x - y)
  const q = (p) => s[Math.min(s.length - 1, Math.floor(p * (s.length - 1)))]
  return { n: s.length, min: q(0), p50: q(0.5), p95: q(0.95), max: s[s.length - 1], mean: s.reduce((x, y) => x + y, 0) / s.length }
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
process.on('exit', () => {
  try { if (cdpWs) cdpWs.close() } catch {}
  try { if (chrome) chrome.kill('SIGKILL') } catch {}
})

async function main() {
  const prof = path.resolve(process.env.TEMP || os.tmpdir(), 'hf-perf-' + Date.now().toString(36))
  chrome = spawn(CHROME, [
    '--headless=new',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${prof}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--window-size=1600,1000',
    '--disable-features=Translate',
    'about:blank',
  ], { stdio: 'ignore' })
  chrome.on('error', (e) => console.error('chrome spawn failed:', e.message))

  // 等调试端口起来
  let targets = null
  for (let i = 0; i < 60; i++) {
    await sleep(300)
    try {
      targets = await fetchJson(`http://127.0.0.1:${PORT}/json/list`)
      break
    } catch {}
  }
  if (!targets) throw new Error('CDP 不可用：Chrome 调试端口没起来（profile=' + prof + '）')

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
    const r = await cdp.send('Runtime.evaluate', {
      expression: expr, returnByValue: true, awaitPromise: awaitIt,
    }, 60000)
    if (r.exceptionDetails) {
      throw new Error('页内异常: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text))
    }
    return r.result.value
  }

  const url = `${BASE}/room/${ROOM}/${ROLE}`
  console.log('打开 ' + url)
  await cdp.send('Page.navigate', { url })
  for (let i = 0; i < 60; i++) {
    const ready = await evalJs(`!!(window.__hexMap && window.__hexMap.view)`)
    if (ready) break
    await sleep(500)
  }
  if (!(await evalJs(`!!(window.__hexMap && window.__hexMap.view)`))) {
    throw new Error('地图没拿到视图（__hexMap.view 仍为空）：房间号/角色或 5018/5175 服务有问题')
  }
  console.log('draw 包装：' + (await evalJs(PATCH_DRAW)))
  await sleep(600)

  const tk = await evalJs(READ_TK)

  // 先把相机摆到"瓦片会画"的缩放（fit 出来的全局视角 tilePx 常在 64px 以下直接跳过瓦片层）
  const settleZoomForTiles = async () => {
    await evalJs(`(() => { const m = window.__hexMap; m.zoomBy(2.2); m.requestDraw(); return true })()`, true)
  }

  const results = []
  let tilesOn = false

  for (const pass of ['notiles', 'tiles']) {
    tilesOn = pass === 'tiles'
    if (tilesOn) {
      await cdp.send('Fetch.enable', {
        patterns: [{ urlPattern: '*tianditu*', requestStage: 'Request' }],
      })
      const png = tilePng().toString('base64')
      cdp.on('Fetch.requestPaused', async (p) => {
        try {
          await cdp.send('Fetch.fulfillRequest', {
            requestId: p.requestId, responseCode: 200,
            responseHeaders: [{ name: 'Content-Type', value: 'image/png' }],
            body: png,
          }, 15000)
        } catch {}
      })
      await evalJs(`window.__hexMap.setTk(${JSON.stringify(tk || '')})`)
      await settleZoomForTiles()
      await sleep(1500) // 让首批瓦片解码进缓存
    } else {
      await evalJs(`window.__hexMap.setTk(null)`)
      await evalJs(`(() => { window.__hexMap.fit(); window.__hexMap.requestDraw(); return true })()`, true)
      await sleep(600)
    }

    const state = await evalJs(TILE_STATE)
    console.log(`\n[${pass}] 场景：canvas=${state.canvas} 格=${state.cells} 单位=${state.units} `
      + `瓦片=${state.tiles}(ok ${state.ok}) zoom=${state.zoom} tilePx=${state.tilePx} 画瓦片=${state.drawsTiles}`)
    if (tilesOn && (!state.drawsTiles || state.ok === 0)) {
      console.log(`[${pass}] !! 瓦片没真在画，这条路径的数字无效`)
    }

    // 1) 空载标定
    results.push(await scenario(cdp, evalJs, pass, 'idle', 1200))
    // 2) 拖拽平移
    results.push(await scenario(cdp, evalJs, pass, 'pan', PAN_MS))
    // 3) 滚轮放大 / 缩小
    results.push(await scenario(cdp, evalJs, pass, 'wheel-in', 1200))
    results.push(await scenario(cdp, evalJs, pass, 'wheel-out', 1200))
    // 4) 双击放大
    results.push(await scenario(cdp, evalJs, pass, 'dblclick', 1000))
    // 5) 缩放按钮 + 复位
    results.push(await scenario(cdp, evalJs, pass, 'buttons', 1000))
    // 6) 复位后整盘平移（最远视距，格子最多）
    await evalJs(`(() => { window.__hexMap.fit(); return true })()`)
    results.push(await scenario(cdp, evalJs, pass, 'pan-fit', PAN_MS))

    if (tilesOn) await cdp.send('Fetch.disable')
  }

  // ── 汇总 ──
  console.log('\n================ 帧率汇总 ================')
  console.log('pass      scenario   frames  fps(p50)  间隔p50/p95/max   draw n  draw p50/p95/max  长帧≥50ms  掉帧>25ms')
  const idleByPass = {}
  for (const r of results) if (r.scenario === 'idle') idleByPass[r.pass] = r

  let failures = 0
  for (const r of results) {
    const idle = idleByPass[r.pass]
    const cadenceRatio = idle && idle.gap.p50 ? r.gap.p50 / idle.gap.p50 : null
    const gates = []
    if (r.draw.p95 !== undefined && r.draw.p95 > 16.7) gates.push('draw p95>16.7ms')
    if (r.draw.max !== undefined && r.draw.max > 50) gates.push('draw max>50ms')
    if (r.longFrames > 0) gates.push('长帧')
    if (cadenceRatio !== null && cadenceRatio > 1.5) gates.push('帧间隔退化>1.5x')
    if (gates.length) failures++
    r.gates = gates
    console.log(
      `${r.pass.padEnd(8)} ${r.scenario.padEnd(9)} ${String(r.draw.n).padStart(5)}  `
      + `${(r.fps || 0).toFixed(1).padStart(7)}  `
      + `${r.gap.p50.toFixed(1)}/${r.gap.p95.toFixed(1)}/${r.gap.max.toFixed(1)}`.padStart(18) + '  '
      + `${String(r.draw.n).padStart(5)}  `
      + `${r.draw.p50.toFixed(2)}/${r.draw.p95.toFixed(2)}/${r.draw.max.toFixed(2)}`.padStart(18) + '  '
      + `${String(r.longFrames).padStart(6)}  ${String(r.slowGaps).padStart(6)}`
      + (gates.length ? '   FAIL: ' + gates.join('+') : ''),
    )
  }

  const verdict = failures === 0 ? 'PASS' : 'FAIL'
  console.log(`\n结论：${verdict}（${failures}/${results.length} 条场景越线）`)
  console.log('空载标定见 idle 行：帧间隔 p50≈16.7ms 说明合成器锁 60Hz，此时 fps 数字可直接读；'
    + '明显小于 16.7ms 说明无头不锁帧，60fps 的判断以 draw 耗时与长帧为准。')

  if (!OUT) {
    const dest = `.tmp/map-perf-${ROLE}.json`
    fs.writeFileSync(dest, JSON.stringify({ url, role: ROLE, generatedAt: new Date().toISOString(), results }, null, 2))
    console.log('明细已写 ' + dest)
  } else {
    fs.writeFileSync(OUT, JSON.stringify({ url, role: ROLE, generatedAt: new Date().toISOString(), results }, null, 2))
    console.log('明细已写 ' + OUT)
  }

  await cdp.send('Page.captureScreenshot', { format: 'png' })
    .then((r) => fs.writeFileSync(`.tmp/map-perf-${ROLE}.png`, Buffer.from(r.data, 'base64')))
    .catch(() => {})

  cdpWs.close()
  chrome.kill('SIGKILL')
  try { fs.rmSync(prof, { recursive: true, force: true }) } catch {}
  process.exitCode = failures === 0 ? 0 : 1
}

/**
 * 跑一个交互场景：CDP 派发受信任的合成事件（真实 pointerdown/wheel 走浏览器事件链），
 * 场景前后各留一点余量，取窗口内的 rAF 间隔与 draw 耗时。
 */
async function scenario(cdp, evalJs, pass, kind, ms) {
  await evalJs(DRAIN).then(() => {}) // 清空缓冲
  const canvasBox = await evalJs(`(() => { const b = window.__hexMap.canvas.getBoundingClientRect(); return {x:b.x,y:b.y,w:b.width,h:b.height} })()`)
  const cx = Math.round(canvasBox.x + canvasBox.w / 2)
  const cy = Math.round(canvasBox.y + canvasBox.h / 2)

  const move = (type, x, y, extra = {}) =>
    cdp.send('Input.dispatchMouseEvent', { type, x, y, buttons: 0, clickCount: 0, ...extra }, 15000)

  const t0 = Date.now()
  const stepMs = 16

  if (kind === 'idle') {
    await sleep(ms)
  } else if (kind === 'pan' || kind === 'pan-fit') {
    await move('mousePressed', cx, cy, { button: 'left', buttons: 1, clickCount: 1 })
    let i = 0
    while (Date.now() - t0 < ms) {
      const dx = Math.round(6 * Math.sin(i / 9)), dy = Math.round(4 * Math.cos(i / 11))
      await move('mouseMoved', cx + dx * (i % 40), cy + dy * (i % 30), { button: 'left', buttons: 1 })
      await sleep(stepMs)
      i++
    }
    await move('mouseReleased', cx, cy, { button: 'left', clickCount: 1 })
  } else if (kind === 'wheel-in' || kind === 'wheel-out') {
    const deltaY = kind === 'wheel-in' ? -100 : 100
    while (Date.now() - t0 < ms) {
      await move('mouseWheel', cx, cy, { deltaX: 0, deltaY })
      await sleep(stepMs)
    }
  } else if (kind === 'dblclick') {
    while (Date.now() - t0 < ms) {
      await move('mousePressed', cx, cy, { button: 'left', buttons: 1, clickCount: 2 })
      await move('mouseReleased', cx, cy, { button: 'left', clickCount: 2 })
      await sleep(120)
    }
  } else if (kind === 'buttons') {
    while (Date.now() - t0 < ms) {
      await evalJs(`(() => { const bs = document.querySelectorAll('.mapzoom button'); bs[0].click(); bs[1].click(); return true })()`)
      await sleep(40)
    }
    await evalJs(`(() => { const bs = document.querySelectorAll('.mapzoom button'); bs[2].click(); return true })()`)
  }

  await sleep(120)
  const raw = await evalJs(DRAIN)
  const wall = Date.now() - t0
  const gaps = intervals(raw.frames)
  const draws = raw.draws.map((d) => d[1])
  const gap = stats(gaps)
  const draw = stats(draws)
  const loaf = raw.loaf || []
  return {
    pass, scenario: kind, wallMs: wall, windowMs: ms,
    frames: raw.frames.length,
    gap: { p50: gap.p50 || 0, p95: gap.p95 || 0, max: gap.max || 0 },
    fps: gap.p50 ? 1000 / gap.p50 : 0,
    draw: { n: draw.n || 0, p50: draw.p50 || 0, p95: draw.p95 || 0, max: draw.max || 0, mean: draw.mean || 0 },
    longFrames: loaf.filter((e) => e.d >= 50).length,
    loafMax: loaf.length ? Math.max(...loaf.map((e) => e.d)) : 0,
    slowGaps: gaps.filter((g) => g > 25).length,
    loafUnsupported: raw.loafUnsupported,
  }
}

main().catch((e) => {
  console.error('FAILED:', e.message)
  process.exitCode = 1
})
