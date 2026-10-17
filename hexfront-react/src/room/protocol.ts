/**
 * 与 Dart room.dart / C# RoomService 完全一致的原生 /ws 线上格式。
 * 服务端权威视图：side={own,contacts,draft,errors,ready,ai_managed}，
 * director={units,orders,last_errors(字符串),ai,power,...}，spectator={contacts,summary}。
 */

export interface WireEquipment { id: string; label: string; count: number }

export interface WireUnit {
  id: string
  side: string
  type: string
  type_label: string
  cls: string
  q: number
  r: number
  personnel: number
  morale: number
  fatigue: number
  training: number
  experience: number
  ammo: number
  fuel: number
  food: number
  status: string
  fort: number
  supply: number
  hq: boolean
  airborne: boolean
  mission: string
  zone: [number, number] | null
  commander: string | null
  equipment: WireEquipment[]
  mobility: number
  firepower: number
  recon: number
}

export interface WireContact {
  id: string
  side: string
  q: number
  r: number
  level: number
  known_turns_ago: number
  stale: boolean
  strength_band?: string
  type?: string
  type_label?: string
  cls?: string
  fort?: number
  status?: string
  morale_band?: string
  fatigue_band?: string
  personnel?: number
  ammo?: number
  fuel?: number
  food?: number
  training?: number
  experience?: number
  equipment?: WireEquipment[]
  commander?: { name: string; quality: number }
}

export interface WireCell {
  q: number
  r: number
  terrain: string
  elev: number
  slope: number
  road: boolean
  river: boolean
  bridge: boolean
  city: number
  railway?: boolean
  rail_station?: boolean
  tunnel?: boolean
  powerline?: boolean
  power_plant?: boolean
  substation?: boolean
  airport?: number
}

export interface WireGeo {
  ox: number
  oy: number
  radius: number
  cell_ground_km?: number
  mercator_scale?: number
}

export interface WireWeather { id: string; label: string; mobility: number; recon: number; combat: number; attrition: number; supply: number; visibility: number }
export interface WireDay { id: string; label: string; recon: number; combat: number }
export interface WireNode { key: string; q: number; r: number; kind: string; label: string }
export interface WireVictory { kind: string; side: string; target: string | null; amount: number; turns: number }

export interface WireCommand {
  op: string
  unit: string
  path?: [number, number][]
  target?: string | null
  zone?: [number, number] | null
  mission?: string
}

export interface WireEvent { turn: number; code: string; text: string; sides: string[] }
export interface WireCommandError { unit: string; reason: string; message: string }

export interface WireCommon {
  instance: string
  scenario: string
  name: string
  turn: number
  max_turns: number
  hour: number
  day: WireDay
  season: string
  weather: WireWeather
  flags: Record<string, unknown>
  geo: WireGeo
  width: number
  height: number
  cells: WireCell[]
  victory: WireVictory[]
  nodes: WireNode[]
  over: boolean
  winner: string | null
  fog: boolean
}

export interface WireSideView extends WireCommon {
  kind: 'side'
  side: string
  own: WireUnit[]
  contacts: WireContact[]
  node_holders: Record<string, string>
  draft: WireCommand[]
  ready: boolean
  errors: WireCommandError[]
  ai_managed: boolean
}

export interface WireDirectorView extends WireCommon {
  kind: 'director'
  units: WireUnit[]
  orders: { red: WireCommand[]; blue: WireCommand[] }
  ready: { red: boolean; blue: boolean }
  last_errors: { red: string[]; blue: string[] }
  ai: { red: boolean; blue: boolean }
  spectator_omniscient: boolean
  node_holders: Record<string, string>
  power: { red: number; blue: number }
  air_control: Array<{ q: number; r: number; v: number }>
}

export interface WireSpectatorView extends WireCommon {
  kind: 'spectator'
  contacts: WireContact[]
  summary: { red_units: number; blue_units: number; red_personnel: number; blue_personnel: number }
}

export type AnyView = WireSideView | WireDirectorView | WireSpectatorView

export interface StateMsg {
  type: 'state'
  view: AnyView
  turn: number
  over: boolean
  winner: string | null
  scenario: string
  events: WireEvent[]
  pending: { red: number; blue: number }
  ready: { red: boolean; blue: boolean }
  draft?: WireCommand[]
}

export interface ServerMsg {
  type: string
  [k: string]: unknown
}

/** CF 战力链逐项贡献（doc §7），与后端 CombatFactors.ToMap() 同序。 */
export interface WireFactors {
  base: number
  terrain: number
  fort: number
  strength: number
  supply: number
  morale: number
  personnel: number
  commander: number
  weather: number
  air: number
  counter: number
  ammo: number
  cf: number
}

/** 一回合内的一次交战裁决；因子链让战报能解释「为什么打不动」。 */
export interface WireBattle {
  turn: number
  kind: string
  atk: string
  def: string
  atk_loss: number
  def_loss: number
  atk_cf: number
  def_cf: number
  factors_atk: WireFactors
  factors_def: WireFactors
  routed: boolean
}

export interface Frame {
  turn: number
  hour: number
  weather: string
  director: WireDirectorView
  events: WireEvent[]
  battles: WireBattle[]
}

export interface SavedMsg {
  type: 'saved'
  instance: string
  turn: number
  meta: Record<string, unknown>
}
