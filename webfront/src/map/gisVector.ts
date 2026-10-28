/**
 * 真实 GIS 矢量叠层的取数与几何准备（后端 GET /api/gis/vector）。
 *
 * 走独立 REST 而不是六角格网络包：包里只有格级布尔量（road/river/bridge…），
 * 画出来是格心连成的人造折线；真实河形与路网形状不同源，也不该挤进 /ws 协议。
 *
 * 必须缓存 + 量化视口：战场地图每帧都在 pan/zoom，逐帧请求会把后端 rtree 查询和
 * 几千条要素反复砸过来。视口外扩 25% 后量化到 0.05°，小幅平移即命中缓存不再取数。
 * 后端 bbox 单边限 8°（GisService.BboxFromQuery），超出就不发请求，由调用方提示。
 */

import { lngToMx, latToMy, mxToLng, myToLat } from '../components/GeoMapView'
import { api, type GeoJsonGeometry, type GisVectorResponse } from '../services/api'

export type VecKind = 'rivers' | 'lakes' | 'roads' | 'airports'

/** 后端单边上限：超过它请求会拿到 {"error":…}，所以宁可不取数 */
export const MAX_SPAN_DEG = 8

/** 一条线/面要素：parts 为墨卡托米扁平点列（MultiLineString 每部分一项、面只取外环） */
export interface VecLine {
  parts: number[][]
  /** 外包络（米）：逐帧视口裁剪用，省掉每帧重算所有顶点 */
  env: [number, number, number, number]
  /** 道路等级样式键；水系/湖泊固定 */
  cls: string
  name?: string
}

export interface VecPoint {
  x: number
  y: number
  name?: string
  iata?: string
}

export interface VecGroup {
  cls: string
  lines: VecLine[]
}

export interface VecLayer {
  lines: VecLine[]
  /** 逐帧绘制按样式分组：一次 beginPath/stroke 画完一类，省掉几百次状态切换 */
  groups: VecGroup[]
  points: VecPoint[]
  available: boolean
  matched: number
  /** 数据文件缺失 / 不在裁剪幅面内 / 被要素上限截断，都写在这里而不是静默画空 */
  note?: string
  truncated?: boolean
}

export interface VectorData {
  key: string
  layers: Partial<Record<VecKind, VecLayer>>
}

// ==================== 取数 ====================

const CACHE_MAX = 12
const LAYERS = 'rivers,lakes,roads,airports'
const cache = new Map<string, VectorData>()
const inflight = new Map<string, Promise<VectorData | null>>()

/** 视口（米）→ 量化请求框；返回 null 表示跨度超过后端单边上限，不该发请求 */
export function viewportQueryBox(vw: [number, number, number, number]): [number, number, number, number] | null {
  let w = mxToLng(vw[0]), e = mxToLng(vw[2])
  let s = myToLat(vw[1]), n = myToLat(vw[3])
  if (!Number.isFinite(w + e + s + n) || e <= w || n <= s) return null
  const pad = 0.25
  const cx = (w + e) / 2, cy = (s + n) / 2
  let hw = ((e - w) * (1 + pad)) / 2, hh = ((n - s) * (1 + pad)) / 2
  // 超上限时收缩到上限（居中）：仍然请求能看到的那一块，而不是整片不画
  if (hw * 2 > MAX_SPAN_DEG || hh * 2 > MAX_SPAN_DEG) {
    const k = Math.min(MAX_SPAN_DEG / (hw * 2), MAX_SPAN_DEG / (hh * 2))
    hw *= k; hh *= k
  }
  const q = (v: number) => Math.round(v / 0.05) * 0.05
  w = q(cx - hw); e = q(cx + hw); s = q(cy - hh); n = q(cy + hh)
  // 量化可能把 8.000 推成 8.05 触发后端 400，再兜一次
  if (e - w > MAX_SPAN_DEG || n - s > MAX_SPAN_DEG) return null
  return [w, s, e, n]
}

export function cacheKey(box: [number, number, number, number]): string {
  return box.map((v) => v.toFixed(3)).join(',')
}

/** 视口（米）→ 缓存键；null 表示米框非法（极小/极大缩放） */
export function viewportKey(vw: [number, number, number, number]): string | null {
  const box = viewportQueryBox(vw)
  return box ? cacheKey(box) : null
}

/** 按视口米框取数；命中缓存直接返回，未命中发一次请求（同 key 并发只发一次）。 */
export async function fetchVectors(vw: [number, number, number, number]): Promise<VectorData | null> {
  const box = viewportQueryBox(vw)
  if (!box) return null
  const key = cacheKey(box)
  const hit = cache.get(key)
  if (hit) return hit
  // pan 过程中同一个量化框会被多帧命中，去重掉，否则一次拖动发三四个请求
  const pending = inflight.get(key)
  if (pending) return pending
  const p = api.gisVector(box, LAYERS)
    .then((resp) => {
      const data = buildVectors(key, resp)
      if (cache.size >= CACHE_MAX) cache.clear()
      cache.set(key, data)
      return data
    })
    .finally(() => inflight.delete(key))
  inflight.set(key, p)
  return p
}

// ==================== GeoJSON → 墨卡托米 ====================

/** 线/面几何 → 墨卡托米点列。面只取外环：NE 10m 湖岛少，补内环要付出成倍顶点数。 */
function lineParts(g: GeoJsonGeometry, out: number[][]) {
  switch (g.type) {
    case 'LineString':
      out.push(flat(g.coordinates as number[][])); break
    case 'MultiLineString':
      for (const l of g.coordinates as number[][][]) out.push(flat(l)); break
    case 'Polygon':
      out.push(flat((g.coordinates as number[][][])[0])); break
    case 'MultiPolygon':
      for (const p of g.coordinates as number[][][][]) out.push(flat(p[0])); break
    case 'GeometryCollection':
      for (const sub of g.geometries || []) lineParts(sub, out); break
    default:
      break
  }
}

function flat(coords: number[][]): number[] {
  const a = new Array<number>(coords.length * 2)
  for (let i = 0; i < coords.length; i++) {
    a[i * 2] = lngToMx(coords[i][0])
    a[i * 2 + 1] = latToMy(coords[i][1])
  }
  return a
}

/** 道路等级：NE roads 的 TYPE/RANK 写法不统一，归到四类样式键 */
function roadClass(type?: string, rank?: number): string {
  const t = (type || '').toLowerCase()
  if (rank != null && rank <= 2) return 'motorway'
  if (/motorway|freeway|expressway|primary/.test(t)) return 'motorway'
  if (/trunk|secondary|first|national/.test(t)) return 'trunk'
  if (/tertiar|unclassified|minor|third|fourth|paved/.test(t)) return 'minor'
  return 'other'
}

/** 河流按 NE scalerank（1=世界大河 … 10=小支流）分三档线宽 */
function riverClass(rank?: number): string {
  if (rank == null || rank >= 8) return 'river3'
  if (rank <= 3) return 'river1'
  return 'river2'
}

function groupByClass(lines: VecLine[]): VecGroup[] {
  const map = new Map<string, VecLine[]>()
  for (const l of lines) {
    const arr = map.get(l.cls)
    if (arr) arr.push(l)
    else map.set(l.cls, [l])
  }
  return [...map].map(([cls, ls]) => ({ cls, lines: ls }))
}

function buildVectors(key: string, resp: GisVectorResponse): VectorData {
  const layers: Partial<Record<VecKind, VecLayer>> = {}
  for (const kind of ['rivers', 'lakes', 'roads', 'airports'] as VecKind[]) {
    const fc = resp.layers?.[kind]
    const m = resp.meta?.[kind] || {}
    const lines: VecLine[] = []
    const points: VecPoint[] = []
    for (const f of fc?.features || []) {
      const g = f.geometry
      if (!g) continue
      const props = f.properties || {}
      const name = typeof props.name === 'string' ? props.name : undefined
      if (g.type === 'Point') {
        const c = g.coordinates as number[]
        points.push({ x: lngToMx(c[0]), y: latToMy(c[1]), name, iata: typeof props.iata === 'string' ? props.iata : undefined })
        continue
      }
      const parts: number[][] = []
      lineParts(g, parts)
      if (!parts.length) continue
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity
      for (const p of parts)
        for (let i = 0; i < p.length; i += 2) {
          if (p[i] < x0) x0 = p[i]
          if (p[i] > x1) x1 = p[i]
          if (p[i + 1] < y0) y0 = p[i + 1]
          if (p[i + 1] > y1) y1 = p[i + 1]
        }
      lines.push({
        parts,
        env: [x0, y0, x1, y1],
        cls: kind === 'roads'
          ? roadClass(typeof props.type === 'string' ? props.type : undefined, num(props.rank))
          : kind === 'rivers' ? riverClass(num(props.scalerank)) : kind,
        name,
      })
    }
    layers[kind] = {
      lines,
      groups: groupByClass(lines),
      points,
      available: m.available !== false,
      matched: m.matched || 0,
      note: typeof m.note === 'string' ? m.note : undefined,
      truncated: m.truncated === true,
    }
  }
  return { key, layers }
}

const num = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined)
