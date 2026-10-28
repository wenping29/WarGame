/**
 * 历史战役剧本批量生成器：解放战争 / 抗美援朝战争 / 抗日战争。
 * 产出 data/scenarios/*.json，与 rhine_corridor.json 同构。
 * 兵种点值来自 LibraryService.UNIT_TYPES（rifle10 light_inf8 rifle_auto13 mec_inf19
 * armor14 tank26 gun13 arty20 engineer9 signals7 aa12 recy8 hq6 radar9 fighter22 recon_air24）。
 * 运行：node tools/scenarios/gen_historical_scenarios.mjs
 */

import { writeFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'data', 'scenarios')

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v))
const px = (fx, W) => clamp(Math.round(fx * W), 1, W - 2)
const py = (fy, H) => clamp(Math.round(fy * H), 1, H - 2)

// ── 各时代编制模板 ─────────────────────────────────────────────
// 攻势方（部署在西侧 low-x）；守势方（部署在东侧 high-x）

function attackOOB(era, W, H) {
  const mid = py(0.5, H)
  if (era === '抗美援朝战争')
    return [ // 志愿军：轻步兵夜行军近战，无空军，炮兵薄弱
      { type: 'hq', qty: 1, at: [px(0.08, W), mid], commander: { name: '志愿军前指', quality: 0.78 }, training: 62, experience: 70, morale: 85 },
      { type: 'light_inf', qty: 6, at: [px(0.24, W), py(0.38, H)], training: 60, experience: 68, morale: 88 },
      { type: 'light_inf', qty: 4, at: [px(0.24, W), py(0.62, H)], training: 55, experience: 62, morale: 82 },
      { type: 'rifle', qty: 3, at: [px(0.3, W), mid], training: 55, experience: 58, morale: 80 },
      { type: 'arty', qty: 1, at: [px(0.16, W), py(0.45, H)], training: 50, experience: 45 },
      { type: 'engineer', qty: 1, at: [px(0.2, W), mid] },
      { type: 'signals', qty: 1, at: [px(0.12, W), py(0.55, H)] },
      { type: 'recy', qty: 2, at: [px(0.05, W), mid] },
    ]
  if (era === '解放战争')
    return [ // 解放军：步兵为主、炮兵初成、机动靠双腿加缴获
      { type: 'hq', qty: 1, at: [px(0.08, W), mid], commander: { name: '野司前指', quality: 0.75 }, training: 60, experience: 62, morale: 85 },
      { type: 'rifle', qty: 5, at: [px(0.24, W), py(0.4, H)], training: 58, experience: 60, morale: 85 },
      { type: 'light_inf', qty: 4, at: [px(0.24, W), py(0.62, H)], training: 55, experience: 58, morale: 82 },
      { type: 'rifle_auto', qty: 2, at: [px(0.3, W), mid], training: 52, experience: 50 },
      { type: 'arty', qty: 2, at: [px(0.16, W), py(0.45, H)], training: 55, experience: 48 },
      { type: 'gun', qty: 1, at: [px(0.2, W), py(0.58, H)] },
      { type: 'engineer', qty: 1, at: [px(0.2, W), mid] },
      { type: 'signals', qty: 1, at: [px(0.12, W), py(0.55, H)] },
      { type: 'recy', qty: 2, at: [px(0.05, W), mid] },
      { type: 'aa', qty: 1, at: [px(0.18, W), py(0.35, H)] },
    ]
  // 抗日战争（中国军队攻势场合少见，此模板多用于守方，攻方亦可用）
  return [
    { type: 'hq', qty: 1, at: [px(0.08, W), mid], commander: { name: '战区前指', quality: 0.62 }, training: 48, experience: 45, morale: 72 },
    { type: 'light_inf', qty: 5, at: [px(0.24, W), py(0.4, H)], training: 45, experience: 42, morale: 72 },
    { type: 'rifle', qty: 4, at: [px(0.24, W), py(0.62, H)], training: 42, experience: 40, morale: 70 },
    { type: 'gun', qty: 1, at: [px(0.2, W), py(0.55, H)] },
    { type: 'arty', qty: 1, at: [px(0.15, W), py(0.45, H)] },
    { type: 'engineer', qty: 1, at: [px(0.18, W), mid] },
    { type: 'signals', qty: 1, at: [px(0.1, W), py(0.55, H)] },
    { type: 'recy', qty: 1, at: [px(0.04, W), mid] },
  ]
}

function defendOOB(era, W, H) {
  const mid = py(0.5, H)
  if (era === '抗美援朝战争')
    return [ // 联合国军：机械化、火力与空情优势，昼间作战
      { type: 'hq', qty: 1, at: [px(0.9, W), mid], commander: { name: '联合国军指挥所', quality: 0.68 }, training: 75, experience: 70 },
      { type: 'mec_inf', qty: 4, at: [px(0.7, W), py(0.42, H)], fort: 2, training: 78, experience: 72, morale: 70 },
      { type: 'mec_inf', qty: 2, at: [px(0.7, W), py(0.62, H)], fort: 1, training: 75, experience: 70 },
      { type: 'tank', qty: 2, at: [px(0.78, W), py(0.5, H)], training: 78, experience: 72 },
      { type: 'gun', qty: 2, at: [px(0.82, W), py(0.58, H)] },
      { type: 'arty', qty: 2, at: [px(0.88, W), py(0.48, H)], training: 75, experience: 70 },
      { type: 'aa', qty: 1, at: [px(0.8, W), py(0.38, H)] },
      { type: 'radar', qty: 1, at: [px(0.86, W), py(0.7, H)] },
      { type: 'recy', qty: 2, at: [px(0.95, W), mid] },
      { type: 'fighter', qty: 1, at: [px(0.92, W), py(0.8, H)] },
      { type: 'recon_air', qty: 1, at: [px(0.92, W), py(0.15, H)] },
    ]
  if (era === '解放战争')
    return [ // 国民党军：美械/半美械，依托铁路与据点工事
      { type: 'hq', qty: 1, at: [px(0.9, W), mid], commander: { name: '剿总指挥所', quality: 0.58 }, training: 58, experience: 55 },
      { type: 'rifle', qty: 4, at: [px(0.7, W), py(0.42, H)], fort: 2, training: 55, experience: 50, morale: 55 },
      { type: 'rifle', qty: 3, at: [px(0.7, W), py(0.62, H)], fort: 1, training: 50, experience: 45, morale: 50 },
      { type: 'rifle_auto', qty: 2, at: [px(0.78, W), mid], training: 55, experience: 50 },
      { type: 'tank', qty: 1, at: [px(0.82, W), py(0.42, H)] },
      { type: 'gun', qty: 2, at: [px(0.82, W), py(0.58, H)] },
      { type: 'arty', qty: 2, at: [px(0.88, W), py(0.48, H)], training: 55, experience: 50 },
      { type: 'aa', qty: 1, at: [px(0.8, W), py(0.38, H)] },
      { type: 'engineer', qty: 1, at: [px(0.72, W), mid] },
      { type: 'recy', qty: 2, at: [px(0.95, W), mid] },
      { type: 'fighter', qty: 1, at: [px(0.92, W), py(0.82, H)] },
    ]
  // 抗日战争（日军）：训练与火力占优、据点攻坚
  return [
    { type: 'hq', qty: 1, at: [px(0.9, W), mid], commander: { name: '日军支队指挥所', quality: 0.66 }, training: 72, experience: 65 },
    { type: 'rifle', qty: 5, at: [px(0.7, W), py(0.42, H)], fort: 1, training: 75, experience: 68, morale: 72 },
    { type: 'light_inf', qty: 3, at: [px(0.7, W), py(0.62, H)], training: 70, experience: 62, morale: 70 },
    { type: 'tank', qty: 1, at: [px(0.8, W), py(0.5, H)] },
    { type: 'gun', qty: 2, at: [px(0.82, W), py(0.58, H)] },
    { type: 'arty', qty: 2, at: [px(0.88, W), py(0.48, H)], training: 72, experience: 65 },
    { type: 'engineer', qty: 1, at: [px(0.74, W), mid] },
    { type: 'aa', qty: 1, at: [px(0.8, W), py(0.38, H)] },
    { type: 'recy', qty: 2, at: [px(0.95, W), mid] },
    { type: 'fighter', qty: 1, at: [px(0.92, W), py(0.82, H)] },
    { type: 'recon_air', qty: 1, at: [px(0.92, W), py(0.15, H)] },
  ]
}

// 中国守军（抗日战争防御方向专用）：轻装步兵、工事加强、缺重炮与空情
function chinaDefendOOB(W, H) {
  const mid = py(0.5, H)
  return [
    { type: 'hq', qty: 1, at: [px(0.08, W), mid], commander: { name: '守军指挥所', quality: 0.6 }, training: 48, experience: 45, morale: 75 },
    { type: 'light_inf', qty: 5, at: [px(0.28, W), py(0.4, H)], fort: 2, training: 45, experience: 42, morale: 78 },
    { type: 'rifle', qty: 4, at: [px(0.28, W), py(0.62, H)], fort: 2, training: 42, experience: 40, morale: 75 },
    { type: 'gun', qty: 1, at: [px(0.18, W), py(0.55, H)] },
    { type: 'arty', qty: 1, at: [px(0.12, W), py(0.45, H)] },
    { type: 'engineer', qty: 1, at: [px(0.2, W), mid] },
    { type: 'signals', qty: 1, at: [px(0.1, W), py(0.55, H)] },
    { type: 'recy', qty: 1, at: [px(0.04, W), mid] },
    { type: 'aa', qty: 1, at: [px(0.16, W), py(0.35, H)] },
  ]
}

// ── 剧本表 ────────────────────────────────────────────────────
// attacker: 'red' | 'blue' —— 攻势方；守势方自动对侧。
// env: [width, height, cell_km, lon, lat, seed]

const SCENARIOS = [
  // ===== 解放战争 =====
  {
    id: 'huaihai_1948', name: '淮海战役 1948', era: '解放战争', attacker: 'red',
    desc: '1948年11月至1949年1月，以徐州为中心的战略决战。红方多路突击围歼据守要点的蓝方重兵集团，蓝方依托铁路交通线与据点工事固守待援。',
    env: [26, 20, 5.0, 117.5, 34.3, 4811], clock: { start_hour: 6, season: '冬' }, weather: 'snow', maxTurns: 16,
    redName: '华东野战军 · 攻击集团', blueName: '徐州剿总 · 守备集群',
  },
  {
    id: 'liaoshen_1948', name: '辽沈战役 1948', era: '解放战争', attacker: 'red',
    desc: '1948年9月至11月的辽西走廊决战，红方先取要点锦州封闭蓝方撤关内通道，蓝方凭城防与援兵走廊顽抗。',
    env: [26, 18, 5.0, 121.1, 41.1, 4812], clock: { start_hour: 6, season: '秋' }, weather: 'clear', maxTurns: 16,
    redName: '东北野战军 · 攻城集团', blueName: '东北剿总 · 城防集群',
  },
  {
    id: 'dujiang_1949', name: '渡江战役 1949', era: '解放战争', attacker: 'red',
    desc: '1949年4月，百万大军在西起湖口东至江阴的千里战线上强渡长江。红方自北岸多点突破，蓝方凭江防工事与舰艇封锁拦截。',
    env: [26, 20, 5.0, 119.3, 31.9, 4813], clock: { start_hour: 5, season: '春' }, weather: 'rain', maxTurns: 14,
    redName: '渡江东集团', blueName: '京沪杭警备 · 江防集群',
  },
  {
    id: 'pingjin_1949', name: '平津战役 1949', era: '解放战争', attacker: 'red',
    desc: '1949年1月，红方对坚固设防的华北最大工商业城市发起总攻，蓝方凭护城河与核心工事分区固守。',
    env: [24, 18, 2.5, 117.2, 39.1, 4814], clock: { start_hour: 7, season: '冬' }, weather: 'snow', maxTurns: 12,
    redName: '东北野战军 · 攻津集团', blueName: '天津警备司令部 · 城防集群',
  },
  {
    id: 'yudong_1948', name: '豫东战役 1948', era: '解放战争', attacker: 'red',
    desc: '1948年6月至7月中原战场的大规模攻城打援。红方先夺开封再转兵围歼来援之敌，蓝方据城死守并督率援军多路反扑。',
    env: [24, 20, 5.0, 114.3, 34.8, 4815], clock: { start_hour: 6, season: '夏' }, weather: 'clear', maxTurns: 14,
    redName: '华野西线兵团', blueName: '豫东绥靖区 · 城防与援军',
  },
  {
    id: 'menglianggu_1947', name: '孟良崮战役 1947', era: '解放战争', attacker: 'red',
    desc: '1947年5月，山东沂蒙山区「百万军中取上将首级」。红方以险峻山地合围蓝方精锐整编师于岩石山地，蓝方就地转入山地环形防御待援。',
    env: [22, 18, 2.5, 118.3, 35.6, 4816], clock: { start_hour: 5, season: '春' }, weather: 'clear', maxTurns: 12,
    redName: '华东野战军 · 围攻集群', blueName: '整编第七十四师 · 山地固守',
  },

  // ===== 抗美援朝战争 =====
  {
    id: 'korea_c1_1950', name: '抗美援朝第一次战役 1950', era: '抗美援朝战争', attacker: 'red',
    desc: '1950年10月底至11月初，志愿军入朝后在云山一带首次与联合国军交手，以夜战近战突然发起攻击，蓝方骄进冒突遭迎头痛击。',
    env: [26, 20, 5.0, 125.8, 40.0, 5101], clock: { start_hour: 20, season: '秋' }, weather: 'clear', maxTurns: 14, night: true,
    redName: '志愿军 · 突击集群', blueName: '联合国军 · 北进集团',
  },
  {
    id: 'korea_c2_1950', name: '抗美援朝第二次战役 1950', era: '抗美援朝战争', attacker: 'red',
    desc: '1950年11月至12月，清川江与长津湖两线反击。红方以战役迂回穿插断敌退路，蓝方在严寒中全线南撤，此役扭转朝鲜战局。',
    env: [26, 20, 5.0, 127.2, 40.5, 5102], clock: { start_hour: 6, season: '冬' }, weather: 'snow', maxTurns: 16,
    redName: '志愿军 · 东西两线', blueName: '联合国军 · 圣诞攻势',
  },
  {
    id: 'korea_c3_1950', name: '抗美援朝第三次战役 1950', era: '抗美援朝战争', attacker: 'red',
    desc: '1950年12月底，志愿军突破临津江防线向汉城方向推进。红方江河突破后持续进攻，蓝方且战且退依托纵深阻滞。',
    env: [26, 20, 5.0, 126.9, 37.9, 5103], clock: { start_hour: 19, season: '冬' }, weather: 'snow', maxTurns: 14, night: true,
    redName: '志愿军 · 突破集团', blueName: '联合国军 · 临津江防线',
  },
  {
    id: 'korea_c4_1951', name: '抗美援朝第四次战役 1951', era: '抗美援朝战争', attacker: 'red',
    desc: '1951年1月至2月，蓝方发起全线反扑，红方在横城方向实施反击并在运动防御中逐次抵抗，空间换时间的经典样式。',
    env: [26, 20, 5.0, 127.5, 37.5, 5104], clock: { start_hour: 6, season: '冬' }, weather: 'snow', maxTurns: 16,
    redName: '志愿军 · 横城反击与阻击', blueName: '联合国军 · 反扑集团',
  },
  {
    id: 'korea_c5_1951', name: '抗美援朝第五次战役 1951', era: '抗美援朝战争', attacker: 'red',
    desc: '1951年4月至6月，双方在铁原、金化以南展开大规模攻防。红方多路突击，蓝方以机动防御与火力绞杀应对，战线自此趋于稳定。',
    env: [26, 20, 5.0, 127.2, 38.2, 5105], clock: { start_hour: 6, season: '春' }, weather: 'rain', maxTurns: 16,
    redName: '志愿军 · 攻击集团', blueName: '联合国军 · 机动防御',
  },
  {
    id: 'tieyuan_1951', name: '铁原阻击战 1951', era: '抗美援朝战争', attacker: 'blue',
    desc: '1951年5月底至6月，铁原是志愿军全线的后勤枢纽。红方以不足之众在丘陵地带构筑多重阻击线死守十二个昼夜，蓝方机械化集群轮番突击。',
    env: [24, 18, 2.5, 127.1, 38.3, 5106], clock: { start_hour: 6, season: '夏' }, weather: 'rain', maxTurns: 14,
    redName: '志愿军第63军 · 阻击集群', blueName: '联合国军 · 突击集群',
  },
  {
    id: 'hanjiang_1951', name: '汉江阻击战 1951', era: '抗美援朝战争', attacker: 'blue',
    desc: '1951年1月至3月，汉江南岸的背水防御。红方在严寒中依托江河屏障逐河逐岭抵抗，蓝方以火力优势昼夜压迫。',
    env: [24, 18, 5.0, 126.8, 37.6, 5107], clock: { start_hour: 7, season: '冬' }, weather: 'snow', maxTurns: 14,
    redName: '志愿军 · 江南阻击集团', blueName: '联合国军 · 渡江反扑',
  },
  {
    id: 'shangganling_1952', name: '上甘岭战役 1952', era: '抗美援朝战争', attacker: 'blue',
    desc: '1952年10月至11月，五圣山前沿两个连支撑点的反复争夺。红方依托坑道体系昼失夜反，蓝方以持续火力与逐次进攻消耗。',
    env: [22, 18, 2.5, 127.7, 38.4, 5108], clock: { start_hour: 6, season: '秋' }, weather: 'clear', maxTurns: 16,
    redName: '志愿军 · 坑道防御集群', blueName: '联合国军 · 攻势集群',
  },

  // ===== 抗日战争 =====
  {
    id: 'mukden_1931',
    hexgrid: 'hg_0b2ca24d15', name: '九一八事变 1931', era: '抗日战争', attacker: 'blue',
    desc: '1931年9月18日夜间，沈阳北大营。蓝方自爆铁路后突袭北大营，红方驻地部队在「不抵抗」命令下仓促应对。剧本以夜战条件还原当晚交火。',
    env: [22, 18, 2.5, 123.4, 41.8, 9181], clock: { start_hour: 22, season: '秋' }, weather: 'clear', maxTurns: 12, night: true,
    redName: '北大营驻军', blueName: '日军独立守备队',
  },
  {
    id: 'lugouqiao_1937', name: '七七事变 1937', era: '抗日战争', attacker: 'blue',
    desc: '1937年7月7日夜，北平西南卢沟桥。蓝方以演习失踪为借口进逼宛平城，红方第29军据桥死守，全面抗战由此爆发。',
    env: [22, 18, 2.5, 116.1, 39.85, 7707], clock: { start_hour: 21, season: '夏' }, weather: 'clear', maxTurns: 12, night: true,
    redName: '第29军 · 卢沟桥守军', blueName: '日军 · 丰台驻屯集群',
  },
  {
    id: 'songhu_1937', name: '淞沪会战 1937', era: '抗日战争', attacker: 'blue',
    desc: '1937年8月至11月，上海及近郊水网地带的大规模会战。红方逐次投入兵力在市街与江南水网间节节抵抗，蓝方凭舰炮与陆海协同正面强推。',
    env: [26, 20, 2.5, 121.4, 31.2, 3708], clock: { start_hour: 6, season: '夏' }, weather: 'overcast', maxTurns: 16,
    redName: '中国军队 · 淞沪围攻军', blueName: '日军 · 上海派遣军',
  },
  {
    id: 'nanjing_1937',
    hexgrid: 'hg_7ccce53546', name: '南京保卫战 1937', era: '抗日战争', attacker: 'blue',
    desc: '1937年12月，首都南京的外围与复廓阵地防御。红方以残破之师依托既设工事与城墙防守，蓝方多路合围。此役以红方撑满回合、迟滞蓝方为目标。',
    env: [24, 20, 2.5, 118.8, 32.0, 3712], clock: { start_hour: 7, season: '冬' }, weather: 'overcast', maxTurns: 12,
    redName: '南京卫戍军', blueName: '日军 · 攻城集团',
  },
  {
    id: 'xuzhou_1938', name: '徐州会战 1938', era: '抗日战争', attacker: 'blue',
    desc: '1938年1月至5月，以徐州为核心的津浦线大会战。红方在鲁南苏北平原多线设防，蓝方南北对进实施大纵深合围。',
    env: [26, 20, 5.0, 117.2, 34.3, 3805], clock: { start_hour: 6, season: '春' }, weather: 'clear', maxTurns: 16,
    redName: '第五战区 · 徐州守军', blueName: '日军 · 南北对进集群',
  },
  {
    id: 'taierzhuang_1938', name: '台儿庄战役 1938', era: '抗日战争', attacker: 'blue',
    desc: '1938年3月至4月，鲁南运河畔的城镇攻防。红方以内侧防御配合外线反击夹击突进之敌，蓝方孤军深入强攻砖城。',
    env: [22, 18, 2.5, 117.7, 34.6, 3804], clock: { start_hour: 6, season: '春' }, weather: 'clear', maxTurns: 12,
    redName: '第5战区 · 内线反击集群', blueName: '日军 · 突进支队',
  },
  {
    id: 'wuhan_1938', name: '武汉会战 1938', era: '抗日战争', attacker: 'blue',
    desc: '1938年6月至10月，武汉外围沿长江与大别山的纵深防御会战。红方层层设防逐次抵抗，蓝方溯江仰攻多路突贯。',
    env: [26, 20, 5.0, 114.3, 30.6, 3810], clock: { start_hour: 6, season: '夏' }, weather: 'clear', maxTurns: 16,
    redName: '第九战区 · 环武汉防御', blueName: '日军 · 溯江进攻集群',
  },
  {
    id: 'changsha_1941',
    hexgrid: 'hg_32ceb98c2d', name: '第三次长沙会战 1941', era: '抗日战争', attacker: 'blue',
    desc: '1941年12月至1942年1月，长沙的「天炉战法」。红方以城防吸住突进之敌、外线侧翼钳击断其退路，蓝方孤军攻坚陷入合围。',
    env: [24, 18, 2.5, 112.9, 28.2, 4112], clock: { start_hour: 7, season: '冬' }, weather: 'overcast', maxTurns: 14,
    redName: '第九战区 · 天炉战法', blueName: '日军 · 攻长集群',
  },
  {
    id: 'zhongtiaoshan_1941', name: '中条山战役 1941', era: '抗日战争', attacker: 'blue',
    desc: '1941年5月，晋南中条山根据地的山地防御。红方补给困难、山地分散设防，蓝方以穿插合围与空中侦察协助的钳形攻势突入。',
    env: [24, 20, 2.5, 111.5, 35.3, 4105], clock: { start_hour: 6, season: '春' }, weather: 'clear', maxTurns: 12,
    redName: '第一战区 · 中条山守军', blueName: '日军 · 合围集群',
  },
  {
    id: 'shipai_1943', name: '石牌保卫战 1943', era: '抗日战争', attacker: 'blue',
    desc: '1943年5月，鄂西长江三峡口的石牌要塞。红方凭峡江天险与山地工事死守陪都门户，蓝方沿江西进仰攻要塞。',
    env: [22, 18, 2.5, 111.3, 30.8, 4305], clock: { start_hour: 6, season: '春' }, weather: 'clear', maxTurns: 12,
    redName: '第十八军 · 石牌守军', blueName: '日军 · 溯江突击集群',
  },
]

// ── 组装 ──────────────────────────────────────────────────────

function buildOob(era, role, W, H) {
  if (role === 'attack') return attackOOB(era, W, H)
  if (era === '抗日战争') return chinaDefendOOB(W, H)
  return defendOOB(era, W, H)
}

const POINTS = { rifle: 10, light_inf: 8, rifle_auto: 13, mec_inf: 19, armor: 14, tank: 26, gun: 13, arty: 20, engineer: 9, signals: 7, aa: 12, recy: 8, hq: 6, radar: 9, fighter: 22, recon_air: 24 }

function oobPoints(oob) {
  return Math.ceil(oob.reduce((s, e) => s + (POINTS[e.type] ?? 0) * e.qty, 0) / 5) * 5
}

let written = 0
for (const s of SCENARIOS) {
  const [W, H, cellKm, lon, lat, seed] = s.env
  const redOOB = buildOob(s.era, s.attacker === 'red' ? 'attack' : 'defend', W, H)
  const blueOOB = buildOob(s.era, s.attacker === 'blue' ? 'attack' : 'defend', W, H)
  const target = s.attacker === 'red' ? `${W - 8},${py(0.5, H)}` : `5,${py(0.5, H)}`

  const doc = {
    id: s.id,
    name: s.name,
    era: s.era,
    scale: '战役级',
    description: s.desc,
    environment: { width: W, height: H, cell_km: cellKm, center_lonlat: [lon, lat], height_seed: seed },
    // 绑了网络包的剧本：棋盘来自包，at/target 仍按 environment 小棋盘排版（oob_space 让服务端等比铺满实际棋盘）
    ...(s.hexgrid ? { hexgrid: s.hexgrid, oob_space: 'environment' } : {}),
    flags: { fog: true, air_superiority: !s.night, strict_supply: s.era === '抗美援朝战争', night: !!s.night },
    clock: s.clock,
    weather: { initial: s.weather, mode: 'stochastic' },
    max_turns: s.maxTurns,
    sides: {
      red: { name: s.redName, points_budget: oobPoints(redOOB), oob: redOOB },
      blue: { name: s.blueName, points_budget: oobPoints(blueOOB), oob: blueOOB },
    },
    victory: [
      { kind: 'capture', side: s.attacker, target, amount: 0, turns: 0 },
      { kind: 'survive', side: s.attacker === 'red' ? 'blue' : 'red', target: null, amount: 0, turns: s.maxTurns },
    ],
  }
  const file = join(OUT, `${s.id}.json`)
  writeFileSync(file, JSON.stringify(doc, null, 2) + '\n', 'utf8')
  written++
  console.log(`ok ${s.id}  red=${doc.sides.red.points_budget}pt blue=${doc.sides.blue.points_budget}pt`)
}
console.log(`\n${written} scenarios written to ${OUT}`)
