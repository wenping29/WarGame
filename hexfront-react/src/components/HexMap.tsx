/**
 * 地图组件：只负责挂载命令式 MapView、把服务端视图喂进去、随容器尺寸重算相机。
 * 相机、图层与 LOD 全在 src/map/MapView.ts；React 不做逐帧 diff，也不重建 canvas，
 * 否则每包 state 都会把瓦片缓存与缩放位置一起丢掉。
 */

import { useEffect, useRef, useState } from 'react'
import { BASEMAPS, MapView } from '../map/MapView'
import { useMeta } from '../room/useUnitFull'
import type { AnyView } from '../room/protocol'

interface HexMapProps {
  view: AnyView | null
  selection?: { q: number; r: number } | null
  draftPath?: [number, number][] | null
  rangeCenter?: [number, number] | null
  rangeRadius?: number
  onPick?: (p: { q: number; r: number }) => void
}

export default function HexMap({
  view, selection = null, draftPath = null, rangeCenter = null, rangeRadius = 0, onPick,
}: HexMapProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const mapRef = useRef<MapView | null>(null)
  const pickRef = useRef(onPick)
  pickRef.current = onPick
  const meta = useMeta()

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const map = new MapView(canvas)
    mapRef.current = map
    map.onPick((p) => pickRef.current?.(p))
    // 后台标签页里 RAF 不触发，E2E 验证需要能直接叫一次 draw()；只在 dev 构建暴露
    const w = globalThis as typeof globalThis & { __hexMap?: MapView }
    if (import.meta.env.DEV) w.__hexMap = map
    // 观察容器而非 canvas：canvas 尺寸由 CSS 撑开，自身不会触发 ResizeObserver
    const wrap = canvas.parentElement
    const ro = new ResizeObserver(() => map.resize())
    if (wrap) ro.observe(wrap)
    map.resize()
    return () => {
      ro.disconnect()
      map.dispose()
      mapRef.current = null
      if (w.__hexMap === map) delete w.__hexMap
    }
  }, [])

  useEffect(() => { mapRef.current?.setView(view) }, [view])

  // 底图密钥经 /api/scenarios 下发（服务端读 data/tianditu.json），前端不硬编码
  const tk = (meta?.tiles as { tk?: string } | undefined)?.tk ?? null
  useEffect(() => { mapRef.current?.setTk(tk) }, [tk])

  const [basemap, setBasemap] = useState('vec')
  useEffect(() => { mapRef.current?.setBasemap(basemap) }, [basemap])

  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    map.selection = selection
    map.draftPath = draftPath
    map.rangeCenter = rangeCenter
    map.rangeRadius = rangeRadius
    map.requestDraw()
  }, [selection, draftPath, rangeCenter, rangeRadius])

  const act = (fn: (m: MapView) => void) => () => {
    const map = mapRef.current
    if (map) fn(map)
  }

  return (
    <>
      <canvas ref={canvasRef} />
      <div className="mapzoom">
        <button onClick={act(m => m.zoomBy(1.3))} title="放大">+</button>
        <button onClick={act(m => m.zoomBy(1 / 1.3))} title="缩小">−</button>
        <button onClick={act(m => { m.fit(); m.requestDraw() })} title="复位">⤢</button>
        <select
          className="basemap-sel"
          value={basemap}
          disabled={!tk}
          title={tk ? '在线底图（天地图）' : '未配置天地图密钥'}
          onChange={(e) => setBasemap(e.target.value)}
        >
          {Object.entries(BASEMAPS).map(([k, v]) => (
            <option key={k} value={k}>{v.label}</option>
          ))}
        </select>
      </div>
    </>
  )
}
