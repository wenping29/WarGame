// GIS 契约冒烟：打后端 /api/gis/*，核对线上形状与 odd-r 对齐不变量。
// 用法：node scripts/gis-contract-smoke.mjs [--base http://127.0.0.1:5018] [--keep]
import { rmSync } from 'node:fs'
import { resolve } from 'node:path'

const argv = process.argv.slice(2)
const arg = (name, dft) => {
  const i = argv.indexOf(`--${name}`)
  return i >= 0 ? argv[i + 1] : dft
}
const BASE = arg('base', 'http://127.0.0.1:5018')
const KEEP = argv.includes('--keep')
const API = `${BASE}/api/gis`

const results = []
const check = (ok, name, detail) => {
  results.push({ ok, name, detail })
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${name.padEnd(30)} ${detail ?? ''}`)
}
const get = async (p) => {
  const r = await fetch(API + p)
  return { status: r.status, body: await r.json().catch(() => null) }
}
const post = async (p, body) => {
  const r = await fetch(API + p, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  return { status: r.status, body: await r.json().catch(() => null) }
}

// 范围框给一小块（莱茵河走廊东缘），格数少、跑得快
const BBOX = [7.60, 51.30, 7.90, 51.55]
const pkgName = `smoke_${Date.now()}.json`

// 1. capabilities：键名 snake_case，引擎如实标注
const caps = await get('/capabilities')
check(caps.status === 200 && caps.body?.available === true, 'GET capabilities',
  `engine=${caps.body?.engine} rasterio=${caps.body?.rasterio} dem=${caps.body?.dem_present}`)
check(caps.body && 'python_version' in caps.body && 'dem_tif' in caps.body,
  'capabilities 键名 snake_case', Object.keys(caps.body ?? {}).slice(0, 5).join(','))

// 2. plan：不带 cell_km 只给推荐与阶梯；带了才给 current
const planGet = await get(`/plan?w=${BBOX[0]}&s=${BBOX[1]}&e=${BBOX[2]}&n=${BBOX[3]}`)
const pg = planGet.body
check(planGet.status === 200 && pg?.ok === true, 'GET plan',
  `推荐 ${pg?.recommended?.cell_km} km · ${(pg?.ladder ?? []).length} 档`)
check(Array.isArray(pg?.ladder) && pg.ladder.length >= 3 && pg.ladder.every(l => l.label),
  'plan ladder 完整', (pg?.ladder ?? []).map(l => `${l.cell_km}:${l.label}`).join(' '))
check(pg?.current == null, 'plan 未带 cell_km 时无 current', JSON.stringify(pg?.current ?? null))
const rec = pg?.recommended?.cell_km
const planKm = await get(`/plan?w=${BBOX[0]}&s=${BBOX[1]}&e=${BBOX[2]}&n=${BBOX[3]}&cell_km=${rec}`)
check(planKm.body?.current?.cell_km === rec && planKm.body.current.width > 0,
  'plan 带 cell_km 时有 current', JSON.stringify(planKm.body?.current))
const planPost = await post('/plan', { bbox: BBOX, cell_km: rec })
check(planPost.status === 200 && planPost.body?.recommended?.cell_km === rec
  && planPost.body?.current?.width === planKm.body?.current?.width,
  'POST plan 与 GET 同解', `宽 ${planPost.body?.current?.width}`)

// 3. 错误契约：非法范围框 400 + {"error":…}；未知作业 404
const badBox = await get('/plan?w=8&s=51&e=7&n=51')
check(badBox.status === 400 && typeof badBox.body?.error === 'string',
  '非法 bbox ⇒ 400 error', badBox.body?.error)
const badJob = await get('/jobs/nope')
check(badJob.status === 404 && typeof badJob.body?.error === 'string', '未知作业 ⇒ 404 error', badJob.body?.error)

// 4. 作业：GET 列表是 {jobs:[…]}，POST 起作业后轮询到 done
const jobsBefore = await get('/jobs')
check(jobsBefore.status === 200 && Array.isArray(jobsBefore.body?.jobs), 'GET jobs ⇒ {jobs:[…]}',
  `${jobsBefore.body?.jobs?.length} 条历史作业`)
const started = await post('/jobs', {
  bbox: BBOX, cell_km: rec, name: pkgName.replace('.json', ''),
  dem: 'auto', landuse: 'auto', roads: true, water: true, network: true, features: true,
})
check(started.status === 200 && started.body?.id && ['queued', 'running'].includes(started.body?.status),
  'POST jobs 起作业', `id=${started.body?.id} status=${started.body?.status}`)
check(started.body?.spec?.bbox?.[0] === BBOX[0] && started.body?.spec?.cell_km === rec,
  '作业回显 spec', JSON.stringify(started.body?.spec?.bbox))

let job = started.body
const deadline = Date.now() + 180_000
while (job.status === 'queued' || job.status === 'running') {
  if (Date.now() > deadline) break
  await new Promise(r => setTimeout(r, 1500))
  job = (await get(`/jobs/${job.id}`)).body
}
check(job.status === 'done', '作业跑完', `${job.log?.length ?? 0} 行日志 · ${job.error ?? '无错误'}`)
check(job.meta?.fails === 0, '作业自校验 0 FAIL', `fails=${job.meta?.fails} warns=${job.meta?.warns}`)

// 5. 对齐不变量：棋盘经度中点 == 请求中心（odd-r 的 ox 公式在这里兜底）
const m = job.meta ?? {}
const midLon = (m.bbox?.[0] + m.bbox?.[2]) / 2
const wantLon = (BBOX[0] + BBOX[2]) / 2
check(Number.isFinite(midLon) && Math.abs(midLon - wantLon) < 1e-4,
  '棋盘中心对齐请求范围', `Δlon=${Math.abs(midLon - wantLon).toExponential(2)}（期望中心 ${wantLon}）`)

// 6. 包列表 / meta / 独立校验端点（服务端给产物名加了时间戳后缀，取作业回传的 name）
const produced = m.name ?? pkgName
check(produced.endsWith('.json'), '作业产物包名', `${started.body?.spec?.name} → ${produced}`)
const pkgs = (await get('/packages')).body?.packages ?? []
check(pkgs.some(p => p.name === produced), '新包进入 packages', `${pkgs.length} 个包`)
const oneMeta = await get(`/packages/${produced}/meta`)
check(oneMeta.status === 200 && oneMeta.body?.width === m.width, 'packages/{name}/meta',
  `${oneMeta.body?.width}×${oneMeta.body?.height}`)
const val = await post('/validate', { package_name: produced })
check(val.status === 200 && val.body?.fails === 0, 'POST validate',
  `total=${val.body?.total} fails=${val.body?.fails} warns=${val.body?.warns}`)
const centre = (val.body?.results ?? []).find(r => r.check === 'ox/oy 中心对齐')
check(centre?.level === 'OK', '校验器中心对齐项', centre?.detail)

if (!KEEP) {
  rmSync(resolve('data/gis', produced), { force: true })
  console.log(`\n[cleanup] 已删除冒烟产物 ${produced}（--keep 可保留）`)
}

const fails = results.filter(r => !r.ok)
console.log(`\n合计 ${results.length} 项：FAIL ${fails.length}`)
process.exit(fails.length ? 1 : 0)
