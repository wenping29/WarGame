/**
 * /api/scenarios 元数据缓存（模块级单例）：
 * useMeta / useUnitTypes / useUnitFull / useWeatherList。
 */

import { useEffect, useState } from 'react'
import { api, type ScenariosMeta } from '../services/api'
import type { WireWeather } from './protocol'

export interface UnitTypeDef {
  id: string
  label?: string
  personnel?: number
  points?: number
  mobility?: number
  firepower?: number
  protection?: number
  recon?: number
  load?: number
  consumption?: number
}

let cache: Promise<ScenariosMeta> | null = null

export function loadMeta(force = false): Promise<ScenariosMeta> {
  if (!cache || force) {
    cache = api.scenarios().catch(e => {
      cache = null
      throw e
    })
  }
  return cache
}

/** 服务端偶尔把数组序列化成 {"0":..,"1":..}，统一转成数组 */
function asArray<T>(v: unknown): T[] {
  if (Array.isArray(v)) return v as T[]
  if (v && typeof v === 'object') return Object.values(v as Record<string, T>)
  return []
}

export function useMeta(): ScenariosMeta | null {
  const [meta, setMeta] = useState<ScenariosMeta | null>(null)
  useEffect(() => {
    let alive = true
    loadMeta()
      .then(m => {
        if (alive) setMeta(m)
      })
      .catch(() => {})
    return () => {
      alive = false
    }
  }, [])
  return meta
}

export function useUnitTypes(): Record<string, UnitTypeDef> {
  const meta = useMeta()
  if (!meta) return {}
  const map: Record<string, UnitTypeDef> = {}
  for (const t of asArray<UnitTypeDef>(meta.unit_types)) if (t.id) map[t.id] = t
  return map
}

export function useUnitFull(): (typeId: string) => number {
  const types = useUnitTypes()
  return (typeId: string) => types[typeId]?.personnel ?? 0
}

export function useWeatherList(): WireWeather[] {
  const meta = useMeta()
  return meta ? asArray<WireWeather>(meta.weather) : []
}
