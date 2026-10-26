/**
 * GIS 数据包 × 六角格映射：把服务端工具链（tools/gis/hexgen.py，经 /api/gis/jobs 生成）
 * 产出的数据包落到本地六角格：自动地形（人工涂装优先）、含路/含轨/含河格标记，
 * 并按行/列连通段推导道路/铁路/河流折线（渲染用）。
 * 包网格与本地图同约定：pointy-top + odd-r，q=列、r=行、行主序（hex_order: r*width+q）。
 * 几何量以墨卡托米计，size = 外接圆半径。
 */

import { lngToMx, latToMy, mxToLng, myToLat } from '../components/GeoMapView'

const SQRT3 = Math.sqrt(3)

export interface GridGeom {
  west: number
  south: number
  rows: number
  cols: number
  /** 外接圆半径（墨卡托米） */
  size: number
}

/** 压缩折线：p = [lng, lat, lng, lat, ...] */
export interface GisLine {
  cls?: string
  p: number[]
}

export interface GisData {
  at: number
  /** 自动地形（人工涂装优先，不覆盖） */
  terrains: Record<string, string>
  /** 含道路格子 → 道路等级列表 */
  roads: Record<string, string[]>
  /** 含铁路 / 河流格子 */
  rails: string[]
  rivers: string[]
  /** 折线要素（渲染用）：r=道路 l=铁路 w=河流 */
  ways: { r: GisLine[]; l: GisLine[]; w: GisLine[] }
  /** 数据包元信息摘要 */
  pkg?: { name: string; source: string; width: number; height: number; terrainCounts?: Record<string, number> }
}

/** /api/gis/jobs 产出的数据包（snake_case 原样字段，只取用到的） */
export interface PackageHex {
  q: number
  r: number
  terrain: string
  road?: boolean
  river?: boolean
  railway?: boolean
}
export interface PackageJson {
  hexes: PackageHex[]
  meta: {
    name?: string
    source?: string
    width: number
    height: number
    bbox: number[]
    stats?: { terrain_counts?: Record<string, number> }
  }
}

/** 数据包地形 → 本地五种涂装 */
function mapTerrain(t: string): string | null {
  switch (t) {
    case 'clear':
      return 'plain'
    case 'woods':
      return 'forest'
    case 'city':
      return 'city'
    case 'water':
    case 'river':
    case 'swamp':
      return 'water'
    case 'hills':
    case 'rough':
      return 'mountain'
    default:
      return null
  }
}

export function gisFromPackage(pkg: PackageJson, geom: GridGeom, holes: string[], manual: Record<string, string>): GisData {
  const holeSet = new Set(holes)
  const pw = Math.max(1, pkg.meta.width)
  const ph = Math.max(1, pkg.meta.height)
  const [bw, bs, be, bn] = pkg.meta.bbox
  const cw = (be - bw) / pw
  const ch = (bn - bs) / ph

  /* ---------- 包格中心 → 本地格键（邻域定位；服务端网格行列与本地图不同构，按地理落点对齐） ---------- */
  const westM = lngToMx(geom.west)
  const southM = latToMy(geom.south)
  const lim = geom.size * geom.size
  const hitKey = (lng: number, lat: number): string | null => {
    const mx = lngToMx(lng)
    const my = latToMy(lat)
    const row0 = Math.floor((my - southM) / (1.5 * geom.size))
    const col0 = Math.floor((mx - westM) / (SQRT3 * geom.size))
    let best: string | null = null
    let bestD = Infinity
    for (let row = row0 - 1; row <= row0 + 2; row++) {
      if (row < 0 || row >= geom.rows) continue
      for (let col = col0 - 2; col <= col0 + 1; col++) {
        if (col < 0 || col >= geom.cols) continue
        const gx = westM + SQRT3 * geom.size * (col + 0.5 * (row & 1))
        const gy = southM + 1.5 * geom.size * row
        const d = (mx - gx) ** 2 + (my - gy) ** 2
        if (d < bestD && d <= lim) {
          bestD = d
          best = `${col},${row}`
        }
      }
    }
    return best
  }

  /* ---------- 逐格映射：地形 + 布尔标记 ---------- */
  const roadM: boolean[][] = Array.from({ length: geom.rows }, () => new Array(geom.cols).fill(false))
  const railM: boolean[][] = Array.from({ length: geom.rows }, () => new Array(geom.cols).fill(false))
  const riverM: boolean[][] = Array.from({ length: geom.rows }, () => new Array(geom.cols).fill(false))
  const terr: Record<string, string> = {}

  for (const h of pkg.hexes) {
    // 包格中心经纬度（奇数行右移四分之一格宽近似 odd-r 错位）
    const lng = bw + (h.q + 0.5 + 0.25 * (h.r & 1)) * cw
    const lat = bs + (h.r + 0.5) * ch
    const k = hitKey(lng, lat)
    if (!k || holeSet.has(k)) continue
    const [col, row] = k.split(',').map(Number)
    if (h.road) roadM[row][col] = true
    if (h.railway) railM[row][col] = true
    if (h.river) riverM[row][col] = true
    const t = mapTerrain(h.terrain)
    if (t && !(k in manual)) terr[k] = t
  }

  /* ---------- 布尔标记 → 格键集合 ---------- */
  const cols = geom.cols
  const rows = geom.rows
  const roadKeys: Record<string, string[]> = {}
  const rails: string[] = []
  const rivers: string[] = []
  for (let r = 0; r < rows; r++) {
    for (let q = 0; q < cols; q++) {
      if (roadM[r][q]) roadKeys[`${q},${r}`] = ['road']
      if (railM[r][q]) rails.push(`${q},${r}`)
      if (riverM[r][q]) rivers.push(`${q},${r}`)
    }
  }

  /* ---------- 连通段 → 折线：同行/同列连续格连成线（格中心） ---------- */
  const center = (q: number, r: number): [number, number] => {
    const mx = lngToMx(geom.west) + SQRT3 * geom.size * (q + 0.5 * (r & 1))
    const my = latToMy(geom.south) + 1.5 * geom.size * r
    return [mxToLng(mx), myToLat(my)]
  }
  const runsLine = (m: boolean[][], cls: string | undefined): GisLine[] => {
    const out: GisLine[] = []
    // 行方向
    for (let r = 0; r < rows; r++) {
      let q = 0
      while (q < cols) {
        if (!m[r][q]) {
          q++
          continue
        }
        let e = q
        while (e + 1 < cols && m[r][e + 1]) e++
        if (e > q) {
          const [x0, y0] = center(q, r)
          const [x1, y1] = center(e, r)
          out.push({ cls, p: [x0, y0, x1, y1] })
        }
        q = e + 1
      }
    }
    // 列方向
    for (let q = 0; q < cols; q++) {
      let r = 0
      while (r < rows) {
        if (!m[r][q]) {
          r++
          continue
        }
        let e = r
        while (e + 1 < rows && m[e + 1][q]) e++
        if (e > r) {
          const [x0, y0] = center(q, r)
          const [x1, y1] = center(q, e)
          out.push({ cls, p: [x0, y0, x1, y1] })
        }
        r = e + 1
      }
    }
    return out
  }

  return {
    at: Date.now(),
    terrains: terr,
    roads: roadKeys,
    rails,
    rivers,
    ways: { r: runsLine(roadM, 'road'), l: runsLine(railM, 'rail'), w: runsLine(riverM, 'river') },
    pkg: {
      name: pkg.meta.name ?? '',
      source: pkg.meta.source ?? '',
      width: pkg.meta.width,
      height: pkg.meta.height,
      terrainCounts: pkg.meta.stats?.terrain_counts,
    },
  }
}
