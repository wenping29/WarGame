/**
 * 交互提示音：WebAudio 现场合成短音，不依赖任何音频资产。
 * 浏览器自动播放策略要求先有用户手势才能出声，故模块加载时挂一次性手势解锁；
 * 顶栏可静音，开关状态存 localStorage。
 */

export type Cue = 'tap' | 'ok' | 'warn' | 'err' | 'send' | 'resolve' | 'contact' | 'kick'

/// [频率 Hz, 相对起点秒, 时长秒]
type Note = [number, number, number]

const CUES: Record<Cue, { notes: Note[]; gain: number; wave: OscillatorType }> = {
  tap:     { notes: [[760, 0, 0.05]], gain: 0.045, wave: 'sine' },
  ok:      { notes: [[620, 0, 0.06], [880, 0.07, 0.08]], gain: 0.055, wave: 'sine' },
  warn:    { notes: [[520, 0, 0.05], [520, 0.09, 0.09]], gain: 0.075, wave: 'triangle' },
  err:     { notes: [[400, 0, 0.09], [255, 0.11, 0.17]], gain: 0.085, wave: 'square' },
  send:    { notes: [[540, 0, 0.05], [730, 0.06, 0.05], [920, 0.12, 0.09]], gain: 0.055, wave: 'sine' },
  resolve: { notes: [[523, 0, 0.09], [659, 0.1, 0.09], [784, 0.2, 0.17]], gain: 0.065, wave: 'sine' },
  contact: { notes: [[1050, 0, 0.05], [1330, 0.08, 0.05], [1050, 0.16, 0.06]], gain: 0.07, wave: 'triangle' },
  kick:    { notes: [[300, 0, 0.14], [175, 0.16, 0.22]], gain: 0.09, wave: 'sawtooth' },
}

const KEY = 'hf.sound'
const REPEAT_MS = 150

let ctx: AudioContext | null = null
let muted = false
let lastCue: Cue | null = null
let lastAt = 0

try { muted = localStorage.getItem(KEY) === 'off' } catch { /* 无存储则默认开 */ }

function audio(): AudioContext | null {
  if (muted) return null
  if (!ctx) {
    const AC = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext
    if (!AC) return null
    ctx = new AC()
  }
  if (ctx.state === 'suspended') void ctx.resume()
  return ctx
}

/// 首次用户手势里创建/唤醒上下文，之后的 WS 事件才有声音
export function primeAudio() { void audio() }

export function isMuted() { return muted }

export function setMuted(value: boolean) {
  muted = value
  try { localStorage.setItem(KEY, value ? 'off' : 'on') } catch { /* 忽略 */ }
  if (!value) primeAudio()
}

export function play(cue: Cue) {
  const ac = audio()
  if (!ac) return
  const now = performance.now()
  if (cue === lastCue && now - lastAt < REPEAT_MS) return
  lastCue = cue
  lastAt = now

  const def = CUES[cue]
  const master = ac.createGain()
  master.gain.value = def.gain
  master.connect(ac.destination)

  let tail = 0
  for (const [freq, at, dur] of def.notes) {
    const osc = ac.createOscillator()
    const env = ac.createGain()
    osc.type = def.wave
    osc.frequency.value = freq
    const s = ac.currentTime + at
    env.gain.setValueAtTime(0.0001, s)
    env.gain.exponentialRampToValueAtTime(1, s + 0.012)
    env.gain.exponentialRampToValueAtTime(0.0001, s + dur)
    osc.connect(env)
    env.connect(master)
    osc.start(s)
    osc.stop(s + dur + 0.02)
    tail = Math.max(tail, at + dur)
  }
  window.setTimeout(() => master.disconnect(), (tail + 0.15) * 1000)
}

document.addEventListener('pointerdown', primeAudio, { once: true })
document.addEventListener('keydown', primeAudio, { once: true })
