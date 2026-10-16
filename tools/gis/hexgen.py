#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
hexgen.py —— 兵棋引擎「GIS 六角格地形数据包」生成器（零第三方依赖，Python 3.8+）

流程：
  1. 以 (center_lon, center_lat) 为中心，按 cell_km 间距构建尖顶（pointy-top）轴向六角格网；
  2. 把每格格心的 EPSG:3857 坐标反投影成 WGS84 经纬度，批量抓取 Open-Meteo 真实高程 DEM；
  3. 离线 / 接口失败 / 超时 ⇒ 自动降级为确定性合成 DEM（固定种值 value-noise + 南北向深切河谷
     + 东西两岸台地 + 东南砂岩脊），meta.source 标记为 "synthetic-fallback"；
  4. 由三轴中心差分求坡度，按 elev/slope/距河距离/噪声场分类地形，并合成河带、东西向道路走廊、
     桥梁（2–4 座）与聚落（2–4 处）；
  5. 自校验（格数完整 / terrain 白名单 / sqrt(3)*radius≈cell_km / bridge 只落在 river 格 …）；
  6. 校验通过后写出 data/gis/rhine_5km.json（UTF-8，确定性字节输出；时间戳字段除外）。

输出契约：
  meta:  source, source_note, bbox, center_lonlat, cell_km, width, height,
         mercator_ox, mercator_oy, radius_m, generated_at,
         stats{terrain_counts, elev_min, elev_max, ...}
  hexes: [{q,r,terrain,elev,slope,road,river,bridge,city}, ...]  顺序固定：r 外层、q 内层

格网几何（尖顶格，Red Blob axial 约定）：
  格心：cx = ox + W*(q + r/2)      cy = oy - 1.5*radius*r      W = sqrt(3)*radius = cell_km*1000
  邻居：(q±1,r)、(q,r±1)、(q+1,r-1)、(q-1,r+1)                行距（南北）= 1.5*radius
  ox/oy 反推自「格网几何中心 == bbox 中心 == center_lonlat」。

墨卡托换算（EPSG:3857 ↔ WGS84，严格互逆）：
  HALF_CIRC = 20037508.342789244（赤道半周长，米）, R = HALF_CIRC/pi = 6378137.0
  x = lon*HALF_CIRC/180 ；y = R*ln(tan((90+lat)*pi/360))  （等价于题面写法）
  lon = x*180/HALF_CIRC ；lat = 180/pi*(2*atan(exp(y_deg*pi/180)) - pi/2)，
    其中 y_deg = y*180/HALF_CIRC —— 题面公式里的 y 单位是「度」，必须先把米换算成度，
    与正变换保持同一单位（exp(y/R) 与 exp(y_deg*pi/180) 完全等价）。

用法：
  python hexgen.py --lon 6.95 --lat 51.42 --width 26 --height 20 --cell-km 5 \
                   --out ../../data/gis/rhine_5km.json
  python hexgen.py --offline                    # 无网/联调：强制合成 DEM
  python hexgen.py --only-validate              # 只校验已生成的数据包
"""

from __future__ import annotations

import argparse
import hashlib
import json
import locale
import math
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import deque
from heapq import heappop, heappush
from pathlib import Path

# =====================================================================================
# 1) 几何 / 数据源常量
# =====================================================================================

HALF_CIRCUMFERENCE_M = 20037508.342789244        # EPSG:3857 赤道半周长（米）
EARTH_RADIUS_M = HALF_CIRCUMFERENCE_M / math.pi  # 6378137.0

# --- 默认格网（莱茵河下游 / 杜塞尔多夫走廊） ---
DEFAULT_LON = 6.95
DEFAULT_LAT = 51.42
DEFAULT_WIDTH = 26
DEFAULT_HEIGHT = 20
DEFAULT_CELL_KM = 5.0
DEFAULT_OUT_REL = os.path.join("data", "gis", "rhine_5km.json")

# --- Open-Meteo 高程 API（免费、无 key） ---
ELEV_API_URL = "https://api.open-meteo.com/v1/elevation"
API_BATCH_SIZE = 100          # 每批点数（接口上限 100；520 点 ⇒ 6 批）
API_TIMEOUT_S = 12.0          # 单请求超时
API_RETRIES = 3               # 每批重试次数
API_RETRY_BASE_S = 0.6        # 指数退避基数：0.6 / 1.2 / 2.4 秒
API_SLEEP_S = 0.3             # 批间限速
API_USER_AGENT = "hexfront-hexgen/1.0 (+local-gis-tool; python-urllib)"

SOURCE_REAL = "open-meteo-elevation"
SOURCE_SYNTH = "synthetic-fallback"
SOURCE_LOCAL_DEM = "local-dem-geotiff"
LANDCOVER_PRODUCT = "esa-worldcover-v200-10m"

# --- 确定性种子（同 seed ⇒ 同输出；仅 generated_at 变化） ---
DEFAULT_SEED = 20261016

# =====================================================================================
# 2) 地形分类阈值常量（集中在此，便于核对 / 调参）
# =====================================================================================

# terrain 合法 id 白名单 —— 绝不发明新值
TERRAIN_IDS = ("clear", "rough", "woods", "hills", "mountain", "city", "swamp", "river", "water")
TERRAIN_SET = frozenset(TERRAIN_IDS)

# --- 坡度阈值（度）。基线 = 邻格心距离 = sqrt(3)*radius = cell_km ⇒ 这是「大尺度区域坡度」，
#     真实 90m DEM 在低地战区通常只有 0.2–2°，够不到题面的 6°/12°。生效阈值按
#     build_thresholds() 规则取：min(名义上限, max(全区分位, 退化下限, 中位*相对倍数))，
#     其中「退化下限」= min(绝对下限, 全区 97 分位) ⇒ 平原战区仍能分出 hills/mountain。
SLOPE_MOUNTAIN_DEG = 12.0          # 题面名义：坡度 >12° 且高程高 ⇒ mountain（自适应上限）
SLOPE_HILLS_DEG = 6.0              # 题面名义：6–12° ⇒ hills（自适应上限）
SLOPE_MOUNTAIN_FLOOR_DEG = 4.0     # 绝对下限：正常战区里坡度低于此值不判山地（全区达不到时自动退化为分位）
SLOPE_HILLS_FLOOR_DEG = 1.5        # 绝对下限：正常战区里坡度低于此值不判丘陵（同上）
SLOPE_MOUNTAIN_PCT = 0.93          # 自适应：全区坡度 93 分位
SLOPE_HILLS_PCT = 0.75             # 自适应：全区坡度 75 分位
SLOPE_REL_TOL = 2.0                # 参考：陡度须 > 全区中位坡度 * 2.0（写入 meta 便于核对）
SLOPE_REL_MULT_MOUNTAIN = 3.0      # 山地：坡度须 ≥ 中位坡度 * 3.0
SLOPE_REL_MULT_HILLS = 1.6         # 丘陵：坡度须 ≥ 中位坡度 * 1.6

# --- 高程阈值（米）：生效值 = clamp(全区分位, 绝对下限, 绝对上限)
ELEV_MOUNTAIN_MIN_M = 150.0        # mountain 的绝对高程下限（低地战区不会出现山地）
ELEV_MOUNTAIN_MAX_M = 500.0        # mountain 分位阈值上限（山区别把全图判成山）
ELEV_MOUNTAIN_PCT = 0.90
ELEV_HILLS_MIN_M = 80.0            # hills 的绝对高程下限
ELEV_HILLS_MAX_M = 260.0
ELEV_HILLS_PCT = 0.68
ELEV_SWAMP_PCT = 0.25              # 极低海拔：全区 20 分位
ELEV_SWAMP_MAX_M = 75.0            # swamp/water 的绝对高程上限

# --- 邻域高差 relief（米 = 本格心 + 6 邻格心的高程极差；与坡度互补，见 compute_relief） ---
RELIEF_BROKEN_MIN_M = 80.0         # 高差 ≥ 80 m ⇒ 地面破碎（高程未达丘陵阈值时判 rough）
RELIEF_HILLS_MIN_M = 95.0          # 高差 ≥ 95 m 且高程达丘陵阈值 ⇒ hills（台地边缘 / 孤丘）
RELIEF_MOUNTAIN_MIN_M = 160.0      # 高差 ≥ 160 m 且高程达山地阈值 ⇒ mountain（山脊 / 陡崖台地）
UPLIFT_LAKE_MAX_M = 1.0            # 湖泊格须是「局部洼底」（本格 = 邻域最低点，隆起度 ≤ 1 m）

# --- 林地 / 沼泽 / 水面（无真实地表覆盖数据 ⇒ 用确定性噪声场代理） ---
# 噪声自变量按「公里位置 / 特征尺寸」取样：特征尺寸决定斑块大小（约 1/3 ~ 1/2 图幅）
NOISE_SCALE_WOODS_KM = 20.0        # 林地斑块特征尺寸
NOISE_SCALE_WET_KM = 14.0          # 湿度（沼泽）斑块特征尺寸
NOISE_SCALE_LAKE_KM = 9.0          # 湖泊洼地特征尺寸
NOISE_SCALE_CITY_KM = 6.0          # 聚落选址抖动特征尺寸
# 注：fbm 是多倍频均值，取值集中在 0.5 附近（约 0.3–0.7），故阈值只需略高于 0.5
WOODS_NOISE_MIN = 0.535            # 森林噪声 ≥ 此值才可能是 woods
WOODS_MAX_SLOPE_DEG = 9.0          # 过陡不划林地
WOODS_MAX_ELEV_REL = 0.85          # 高程须 < mountain 阈值 * 0.85（山顶留给 mountain）
SWAMP_NOISE_MIN = 0.52             # 湿度噪声 ≥ 此值才可能是 swamp
SWAMP_RIVER_MAX_DIST = 2           # 沼泽须距河带 ≤ 2 格（河漫滩 / 牛轭湖）
SWAMP_SOURCE_TERRAIN = ("clear", "rough", "woods", "swamp")   # 可被改判为 swamp 的原类别
WATER_SOURCE_TERRAIN = ("clear", "rough", "woods", "swamp")   # 可被改判为 water（湖）的原类别
WATER_NOISE_MIN = 0.66             # 湖泊噪声 ≥ 此值 ⇒ water（静水，不可通行）
WATER_RIVER_MIN_DIST = 1           # 湖泊不吞掉河带本身
MAX_LAKE_CELLS = 14                # 水面格上限，防异常数据把地图变成湖

# --- 真实土地覆盖（WorldCover）生效阈值：按「格内面积占比」判定，不是格心一点 ---
LC_WOODS_SHARE = 0.45              # 树覆盖占比 ≥ ⇒ woods
LC_ROUGH_SHARE = 0.50              # 稀疏植被/苔原/积雪占比 ≥ ⇒ rough
LC_SWAMP_SHARE = 0.40              # 湿地类占比 ≥ ⇒ swamp
LC_WATER_SHARE = 0.34              # 水体占比 ≥ ⇒ water（河带格除外）
LC_CITY_SHARE = 0.25               # 建筑区占比 ≥ ⇒ 聚落候选
LC_CITY_MAX_FRACTION = 0.12        # 聚落格占全图上限：现实城区会连绵成片，游戏里须留野地
LC_CITY_LEVEL2_SHARE = 0.45        # 建筑区占比 ≥ ⇒ 聚落等级 2（大城市/枢纽）

# --- 水系 / 道路 / 桥梁 / 聚落 ---
RIVER_ROUTE_STEPS = (0.35, 0.55, 0.80, 1.05)  # 河带 Dijkstra 逐级放大的「谷底高程容差」（分位跨度）
RIVER_ROUTE_RIDGE_PCT = 0.70       # 高于此分位的格视作山脊，重罚（首两级生效）
RIVER_STEP_PENALTY_REL = 0.18      # 每格常数代价（相对高程跨度）：抑制过度蜿蜒
RIVER_BEND_PENALTY_PER_DEG = 0.35  # 转弯惩罚（度 → 高程当量），去锯齿
RIVER_MIN_ELEV_RATIO = 1.8         # 河带格高程一般不超过全图最低点的 1.8 倍（文档/调参参考）
BRIDGE_SPACING_COLS = 9            # 桥梁目标数 = clamp(round(width/此值), 2, 4)
BRIDGE_MIN = 2
BRIDGE_MAX = 4
ROAD_ROW_TOLERANCE = 2             # 道路起/终点行容差（保证主干大致东西向）
ROAD_ROW_DEVIATION_COST = 140.0    # 偏离目标行的代价（每格每行，高程当量）
ROAD_RIVER_BLOCK_COST = 1.0e6      # 非桥位穿河代价 ⇒ 桥数 == 走廊数
ROAD_WATER_BLOCK_COST = 4.0e5      # 道路不穿湖泊
ROAD_MOUNTAIN_PENALTY = 700.0      # 道路尽量绕开山地
ROAD_BEND_PENALTY_PER_DEG = 2.0    # 道路转弯惩罚
CITY_TARGET = 4
CITY_MIN = 2
CITY_MAX = 4
CITY_MIN_SEPARATION = 3            # 聚落最小六角格间距
CITY_JUNCTION_BONUS = 0.35         # 邻域道路越多（交叉口）加分越大
CITY_RIVER_MAX_DIST = 2            # 聚落优先选河岸 / 渡口

# =====================================================================================
# 3) 投影 / 格网几何
# =====================================================================================


def lon_to_x(lon_deg: float) -> float:
    """WGS84 经度 → EPSG:3857 X（米）。"""
    return lon_deg * HALF_CIRCUMFERENCE_M / 180.0


def lat_to_y(lat_deg: float) -> float:
    """WGS84 纬度 → EPSG:3857 Y（米），Web Mercator 标准公式。"""
    return EARTH_RADIUS_M * math.log(math.tan((90.0 + lat_deg) * math.pi / 360.0))


def x_to_lon(x_m: float) -> float:
    """EPSG:3857 X → 经度。"""
    return x_m * 180.0 / HALF_CIRCUMFERENCE_M


def y_to_lat(y_m: float) -> float:
    """EPSG:3857 Y（米）→ 纬度；先米→度，再套题面反变换，保证与正变换严格互逆。"""
    y_deg = y_m * 180.0 / HALF_CIRCUMFERENCE_M
    return math.degrees(2.0 * math.atan(math.exp(y_deg * math.pi / 180.0)) - math.pi / 2.0)


AXIAL_DIRS = ((1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1))   # 尖顶 axial 邻居
# 三条梯度轴（像素方向角 0°/±60°）对应的 (正向邻居, 负向邻居)
SLOPE_AXES = (((1, 0), (-1, 0)), ((1, -1), (-1, 1)), ((0, -1), (0, 1)))


def mercator_scale(lat_deg: float) -> float:
    """
    Web Mercator 在纬度 lat 的线性放大倍数 = 1/cos(lat)：墨卡托米 ÷ 该倍数 = 实地米。
    为什么必须显式处理：格网画在 EPSG:3857 里才能和瓦片零漂移对齐，但「对边距 5 km」
    是指挥员嘴里的实地 5 km。51°N 处两者差 60%，不换算就等于把 3.1 km 的格标成 5 km。
    """
    return 1.0 / max(0.05, math.cos(math.radians(clamp(lat_deg, -85.0, 85.0))))



class HexGrid:
    """尖顶六角格几何：格心 ↔ EPSG:3857。"""

    def __init__(self, center_lon: float, center_lat: float, width: int, height: int, cell_km: float):
        self.center_lon = center_lon
        self.center_lat = center_lat
        self.width = width
        self.height = height
        self.cell_km = cell_km
        self.scale = mercator_scale(center_lat)             # 墨卡托米 : 实地米
        self.cell_ground_m = cell_km * 1000.0              # 对边距（实地）——指挥员口径
        self.cell_m = self.cell_ground_m * self.scale       # 同一距离的墨卡托米
        # 尖顶格：横向邻格距离 W = sqrt(3)*radius 必须等于 cell_m ⇒ radius = cell/sqrt(3)
        self.radius_m = self.cell_m / math.sqrt(3.0)
        self.hex_width_m = math.sqrt(3.0) * self.radius_m    # == cell_m（契约校验点）
        self.row_step_m = 1.5 * self.radius_m                # 南北行距（墨卡托米）
        cx0 = lon_to_x(center_lon)
        cy0 = lat_to_y(center_lat)
        # 令「格网几何中心」= bbox 中心 = center_lonlat：按格心公式反推 ox/oy
        # 几何中心的分数索引 = ((width-1)/2, (height-1)/2)
        self.mercator_ox = cx0 - self.hex_width_m * ((width - 1) / 2.0 + (height - 1) / 4.0)
        self.mercator_oy = cy0 + self.row_step_m * ((height - 1) / 2.0)

    # ---- 索引：index = r*width + q（r 外层、q 内层） ----
    def index(self, q: int, r: int) -> int:
        return r * self.width + q

    def iter_cells(self):
        for r in range(self.height):
            for q in range(self.width):
                yield (q, r)

    def in_bounds(self, q: int, r: int) -> bool:
        return 0 <= q < self.width and 0 <= r < self.height

    # ---- 几何 ----
    def center(self, cell):
        """格心 EPSG:3857 (x, y)。"""
        q, r = cell
        return (self.mercator_ox + self.hex_width_m * (q + r / 2.0),
                self.mercator_oy - self.row_step_m * r)

    def center_lonlat(self, cell):
        """格心 → (lat, lon)，供 Open-Meteo 批量参数使用。"""
        x, y = self.center(cell)
        return (y_to_lat(y), x_to_lon(x))

    def neighbors(self, q: int, r: int):
        out = []
        for dq, dr in AXIAL_DIRS:
            nq, nr = q + dq, r + dr
            if self.in_bounds(nq, nr):
                out.append((nq, nr))
        return out

    def bbox(self):
        """格网外轮廓（中心包围盒 + 半格外接余量）→ WGS84 bbox。"""
        cs = [self.center(c) for c in self.iter_cells()]
        xs = [c[0] for c in cs]
        ys = [c[1] for c in cs]
        min_x = min(xs) - self.hex_width_m / 2.0
        max_x = max(xs) + self.hex_width_m / 2.0
        min_y = min(ys) - self.radius_m
        max_y = max(ys) + self.radius_m
        return (x_to_lon(min_x), y_to_lat(min_y), x_to_lon(max_x), y_to_lat(max_y))

    def distance_field(self, sources):
        """六角格 BFS 距离场（源格 = 0，不可达 = 99）。"""
        dist = {c: 0 for c in sources}
        dq = deque(sorted(sources))
        while dq:
            cur = dq.popleft()
            nd = dist[cur] + 1
            for n in self.neighbors(*cur):
                if n not in dist:
                    dist[n] = nd
                    dq.append(n)
        for c in self.iter_cells():
            dist.setdefault(c, 99)
        return dist

    def turn_angle(self, a, b, c):
        """路径 a→b→c 的转角（度），用于转弯惩罚去锯齿。"""
        if a is None:
            return 0.0
        ax, ay = self.center(a)
        bx, by = self.center(b)
        cx, cy = self.center(c)
        v1 = math.atan2(by - ay, bx - ax)
        v2 = math.atan2(cy - by, cx - bx)
        d = abs(math.degrees(v2 - v1)) % 360.0
        return min(d, 360.0 - d)


def grid_from_bbox(bbox, cell_km: float, min_side: int = 8):
    """
    范围框（WGS84 四至，指**格心**范围）+ 对边距 → 居中的格网。
    行列数由「铺满该范围」决定：列 = Δx/格宽 + 1，行 = Δy/行距 + 1，至少 min_side。
    """
    x0, y0 = lon_to_x(bbox[0]), lat_to_y(bbox[1])
    x1, y1 = lon_to_x(bbox[2]), lat_to_y(bbox[3])
    if not (x1 > x0 and y1 > y0):
        raise ValueError("bbox 需满足 西<东 且 南<北：%s" % (bbox,))
    clon = x_to_lon((x0 + x1) / 2.0)
    clat = y_to_lat((y0 + y1) / 2.0)
    probe = HexGrid(clon, clat, 1, 1, cell_km)
    width = int(round((x1 - x0) / probe.hex_width_m)) + 1
    height = int(round((y1 - y0) / probe.row_step_m)) + 1
    return HexGrid(clon, clat, max(min_side, width), max(min_side, height), cell_km)



def axial_distance(a, b) -> int:
    """轴向坐标的六角格距离（cube 距离）。"""
    ax, az = a
    bx, bz = b
    return max(abs(ax - bx), abs(-ax - az + bx + bz), abs(az - bz))


# =====================================================================================
# 4) 确定性噪声（value noise / fBm）：合成 DEM + 地表覆盖代理场
# =====================================================================================


def _hash2d(ix: int, iy: int, seed: int) -> float:
    """稳定整数哈希 → [0,1)。不依赖 Python 内置 hash()，跨进程/平台可复现。"""
    data = ("%d|%d|%d" % (int(ix), int(iy), int(seed))).encode("ascii")
    h = int.from_bytes(hashlib.blake2b(data, digest_size=8).digest(), "big")
    h ^= h >> 33
    h = (h * 0xFF51AFD7ED558CCD) & 0xFFFFFFFFFFFFFFFF
    h ^= h >> 33
    h = (h * 0xC4CEB9FE1A85EC53) & 0xFFFFFFFFFFFFFFFF
    return (h & 0xFFFFFFFFFFFFFFFF) / float(1 << 64)


def _fade(t: float) -> float:
    return t * t * (3.0 - 2.0 * t)


def value_noise(x: float, y: float, seed: int) -> float:
    """双线性插值 value noise（特征尺寸 = 1.0 单位）。"""
    ix, iy = math.floor(x), math.floor(y)
    u, v = _fade(x - ix), _fade(y - iy)
    n00 = _hash2d(ix, iy, seed)
    n10 = _hash2d(ix + 1, iy, seed)
    n01 = _hash2d(ix, iy + 1, seed)
    n11 = _hash2d(ix + 1, iy + 1, seed)
    a = n00 + (n10 - n00) * u
    b = n01 + (n11 - n01) * u
    return a + (b - a) * v


def fbm(x: float, y: float, seed: int, octaves: int = 4, gain: float = 0.5, lacunarity: float = 2.0) -> float:
    """分形叠加（多倍频 value noise），归一化后近似 [0,1]。"""
    amp, freq, total, norm = 1.0, 1.0, 0.0, 0.0
    for i in range(octaves):
        total += amp * value_noise(x * freq, y * freq, seed + 7919 * i)
        norm += amp
        amp *= gain
        freq *= lacunarity
    return total / norm


# =====================================================================================
# 5) 高程数据源：Open-Meteo（在线）/ 合成 DEM（降级）
# =====================================================================================


def _decode_json(raw: bytes):
    """容错解码：API 偶发 gzip/deflate 压缩响应体。"""
    try:
        return json.loads(raw.decode("utf-8"))
    except UnicodeDecodeError:
        pass
    import gzip
    import zlib
    for fn in (gzip.decompress, zlib.decompress):
        try:
            return json.loads(fn(raw).decode("utf-8"))
        except Exception:
            continue
    raise ValueError("无法解码高程 API 响应")


def _openmeteo_once(points, use_requests: bool):
    """单批请求 ⇒ elevation 列表。默认 urllib（零依赖）；requests 若可用则优先/兜底。"""
    lat_s = ",".join("%.6f" % p[0] for p in points)
    lon_s = ",".join("%.6f" % p[1] for p in points)
    if use_requests:
        try:
            import requests
            resp = requests.post(ELEV_API_URL, data={"latitude": lat_s, "longitude": lon_s},
                                 headers={"User-Agent": API_USER_AGENT,
                                          "Accept-Encoding": "gzip, deflate"},
                                 timeout=API_TIMEOUT_S)
            if resp.status_code == 200:
                payload = _decode_json(resp.content)
                if isinstance(payload.get("elevation"), list):
                    return payload
        except Exception:
            pass                                   # 落回 urllib 兜底
    qs = urllib.parse.urlencode({"latitude": lat_s, "longitude": lon_s})
    req = urllib.request.Request(ELEV_API_URL + "?" + qs,
                                 headers={"User-Agent": API_USER_AGENT,
                                          "Accept": "application/json",
                                          "Accept-Encoding": "gzip, deflate"})
    with urllib.request.urlopen(req, timeout=API_TIMEOUT_S) as fp:
        return _decode_json(fp.read())


def fetch_openmeteo(grid: HexGrid, batch: int, allow_requests: bool, quiet: bool):
    """
    批量抓取格心高程：点数 = width*height（默认 520），每批 ≤batch（≤100），批间 sleep 0.3s，
    每批最多重试 API_RETRIES 次（指数退避）。任一批最终失败 / 返回结构异常 ⇒ 返回 None（整体降级）。
    """
    pts = [grid.center_lonlat(c) for c in grid.iter_cells()]          # [(lat, lon), ...]
    elevs = []
    n_batches = (len(pts) + batch - 1) // batch
    for bi in range(n_batches):
        chunk = pts[bi * batch:(bi + 1) * batch]
        payload = None
        last_err = None
        for attempt in range(1, API_RETRIES + 1):
            try:
                payload = _openmeteo_once(chunk, allow_requests)
                break
            except (urllib.error.URLError, urllib.error.HTTPError, OSError, ValueError,
                    KeyError, TimeoutError) as exc:
                last_err = exc
                if not quiet:
                    print("  [api] 批次 %d/%d 第 %d 次尝试失败: %s" % (bi + 1, n_batches, attempt, exc))
                if attempt < API_RETRIES:
                    time.sleep(API_RETRY_BASE_S * (2 ** (attempt - 1)))
        if payload is None:
            print("  [api] 批次 %d/%d 连续 %d 次失败（%s）⇒ 放弃真实 DEM"
                  % (bi + 1, n_batches, API_RETRIES, last_err), file=sys.stderr)
            return None
        arr = payload.get("elevation")
        if not isinstance(arr, list) or len(arr) != len(chunk):
            print("  [api] 批次 %d 返回长度 %s ≠ %d ⇒ 放弃真实 DEM"
                  % (bi + 1, (len(arr) if isinstance(arr, list) else type(arr).__name__), len(chunk)),
                  file=sys.stderr)
            return None
        for v in arr:
            if v is None or isinstance(v, bool) or not isinstance(v, (int, float)) \
                    or not math.isfinite(float(v)):
                print("  [api] 批次 %d 含无效高程值 %r ⇒ 放弃真实 DEM" % (bi + 1, v), file=sys.stderr)
                return None
        elevs.extend(float(v) for v in arr)
        if not quiet:
            print("  [api] 批次 %d/%d OK（%d 点，累计 %d）" % (bi + 1, n_batches, len(chunk), len(elevs)))
        if bi < n_batches - 1:
            time.sleep(API_SLEEP_S)
    return elevs


def synthetic_dem(grid: HexGrid, seed: int):
    """
    确定性合成 DEM（模拟莱茵河下游地貌观感）：
      · 南北走向、缓慢蛇形的深切河谷：谷底约 18 m（全图最低），下切深度 42 m；
      · 两岸台地 40–60 m，东岸沿距离线性抬升（至 +64 m）；
      · 东南角一道 SE→NW 走向砂岩脊（最高 ≈ 190 m）——给 LOS 提供通视屏障；
      · 东岸支流切割的坡脊 + 多倍频小幅高频起伏（1–2 格尺度），
        使坡度分布非退化（河漫滩洼地供 swamp/water 分类）；
    """
    cell = grid.cell_m
    # 格网几何中心的墨卡托坐标（与 HexGrid 的 ox/oy 定义一致）
    cx0 = grid.mercator_ox + grid.hex_width_m * ((grid.width - 1) / 2.0 + (grid.height - 1) / 4.0)
    cy0 = grid.mercator_oy - grid.row_step_m * (grid.height - 1) / 2.0
    valley_half_width = 0.62 * cell       # 河谷半宽（米）
    depth = 42.0                          # 谷底相对台地下切（米）
    span_y = max(1.0, grid.height * grid.row_step_m)
    elevs = []
    for c in grid.iter_cells():
        x, y = grid.center(c)
        dx_m = x - cx0                    # 东为正
        dy_m = y - cy0                    # 北为正
        s = (dy_m) / span_y               # -0.5 .. 0.5（南北归一化位置）
        # 河谷中心线（东西偏移，米）：主波 + 次波 + 噪声，保证不与行列重合
        xc = (4200.0 * math.sin(2.0 * math.pi * (0.85 * s + 0.04))
              + 1500.0 * math.sin(2.0 * math.pi * (2.1 * s - 0.17))
              + 2600.0 * (fbm(0.0, s * 2.4, seed + 611, octaves=3) - 0.5))
        d = (dx_m - xc) / valley_half_width
        bell = math.exp(-d * d)
        terrace = 46.0 + 32.0 * (fbm(dx_m / cell * 0.55, dy_m / cell * 0.55, seed + 17, octaves=4) - 0.5)
        eastern = 0.0
        if dx_m > 1.2 * cell:
            eastern = min(64.0, 0.0075 * (dx_m - 1.2 * cell))
            eastern *= 0.6 + 0.8 * fbm(dx_m / cell * 0.7, dy_m / cell * 0.7, seed + 29, octaves=3)
        ridge = 0.0
        ex = dx_m - 0.50 * grid.width * cell          # 东南角脊心
        ey = -dy_m - 0.32 * grid.height * cell        # 南为正
        along = ex / 9000.0 - ey / 12000.0
        across = ex / 12000.0 + ey / 9000.0
        if dx_m > 0 and -dy_m > 0.10 * grid.height * cell:
            ridge = 118.0 * math.exp(-(across ** 2)) * math.exp(-0.25 * along ** 2)
        micro = 40.0 * (fbm(dx_m / cell * 1.9, dy_m / cell * 1.9, seed + 101, octaves=4) - 0.5)
        # 东岸支流切割的坡脊（~2 格尺度的起伏，让坡度分布不退化）
        ridgelets = 0.0
        if dx_m > 0.8 * cell:
            ridgelets = 46.0 * (fbm(dx_m / cell * 1.05 + 6.0, dy_m / cell * 1.05 - 4.0, seed + 313, octaves=3) - 0.5) \
                * min(1.0, (dx_m - 0.8 * cell) / (3.0 * cell))
        e = 26.0 + terrace + eastern + micro + ridgelets + ridge + 6.0 * bell - depth * bell
        elevs.append(max(6.0, round(e, 1)))
    return elevs


# =====================================================================================
# 6) 坡度（三轴方向导数 → 梯度模长）
# =====================================================================================


def compute_slopes(grid: HexGrid, elev):
    """
    三条梯度轴的像素方向角为 0° / +60° / −60°（单位向量长度均为 1，邻格距离 W = cell）：
      dA = gx,  dB = 0.5gx - (√3/2)gy,  dC = 0.5gx + (√3/2)gy
      ⇒ gx = (dA + dB + dC)/3（冗余求均值降噪）,  gy = (dC - dB)/√3
    边界缺邻居时退化为一阶差分；slope = atan(|grad|)（grad 单位 m/m，水平基线取**实地**格距，
    墨卡托米在 51°N 放大了 60%，用它算会把坡度系统性压扁）。
    """
    W = grid.cell_ground_m
    n = grid.width * grid.height
    slopes = [0.0] * n

    def e(q, r):
        return elev[r * grid.width + q]

    for (q, r) in grid.iter_cells():
        e0 = e(q, r)
        derivs = []
        for dp, dm in SLOPE_AXES:
            p = (q + dp[0], r + dp[1])
            m = (q + dm[0], r + dm[1])
            hp, hm = grid.in_bounds(*p), grid.in_bounds(*m)
            if hp and hm:
                d = (e(*p) - e(*m)) / (2.0 * W)
            elif hp:
                d = (e(*p) - e0) / W
            elif hm:
                d = (e0 - e(*m)) / W
            else:
                d = 0.0
            derivs.append(d)
        dA, dB, dC = derivs
        gx = (dA + dB + dC) / 3.0
        gy = (dC - dB) / math.sqrt(3.0)
        slopes[grid.index(q, r)] = round(math.degrees(math.atan(math.hypot(gx, gy))), 1)
    return slopes


def compute_relief(grid: HexGrid, elev):
    """
    邻域高差 relief（米）= 本格心 + 6 邻格心 的高程极差，另有「隆起度」= 本格 − 邻域最低点。
    作用：5km 基线上的 slope 是「区域平均坡度」，台地边缘 / 山脊顶部可能坡度很小但相对周围
    隆起很大 —— 这类格对 LOS（通视）才是关键。relief 与坡度互补，单位是米，无需自适应。
    """
    n = grid.width * grid.height
    relief = [0.0] * n
    uplift = [0.0] * n
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        vals = [elev[i]] + [elev[grid.index(nq, nr)] for (nq, nr) in grid.neighbors(q, r)]
        relief[i] = round(max(vals) - min(vals), 1)
        uplift[i] = round(elev[i] - min(vals), 1)
    return relief, uplift


# =====================================================================================
# 7) 阈值自适应 + 基础地形分类
# =====================================================================================


def percentile(values, p: float) -> float:
    if not values:
        return 0.0
    s = sorted(values)
    if len(s) == 1:
        return float(s[0])
    pos = max(0.0, min(1.0, p)) * (len(s) - 1)
    lo = int(math.floor(pos))
    hi = min(lo + 1, len(s) - 1)
    return float(s[lo] + (s[hi] - s[lo]) * (pos - lo))


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def build_thresholds(elev, slopes):
    """
    生效阈值 = 绝对常量 ∩ 全区分布分位。
    坡度注意：本数据包的 slope 是「邻格心距离 = cell_km（5km）」基线上的大尺度坡度，
    真实 90m DEM 在莱茵河下游通常只有 0.2–2°，永远够不到题面的 6°/12°。因此：
        slope 阈值 = min(名义上限, max(分位值, 绝对下限的退化值, 相对中位倍数))
      · 名义上限 6°/12°：山区战区不会把全图判成丘陵/山地；
      · 绝对下限 1.5°/4.0°：正常战区里的「平缓河漫滩」不会被误判成丘陵/山地；
      · 退化值 min(绝对下限, 全区 97 分位)：当整张图的坡度都低于绝对下限时（低地战区），
        自动改用分位阈值，保证仍能产出少量 hills/mountain（否则玩法会退化）；
      · 相对中位倍数：陡度必须显著高于本区中位坡度。
    """
    med = percentile(slopes, 0.5)

    def slope_thr(nominal, abs_floor, pct, rel_mult):
        degenerate = min(abs_floor, percentile(slopes, 0.97))
        rel = rel_mult * med
        return round(min(nominal, max(percentile(slopes, pct), degenerate, rel)), 2)

    sm = slope_thr(SLOPE_MOUNTAIN_DEG, SLOPE_MOUNTAIN_FLOOR_DEG, SLOPE_MOUNTAIN_PCT, SLOPE_REL_MULT_MOUNTAIN)
    sh = slope_thr(SLOPE_HILLS_DEG, SLOPE_HILLS_FLOOR_DEG, SLOPE_HILLS_PCT, SLOPE_REL_MULT_HILLS)
    if sh >= sm:                                            # 保证 hills < mountain
        sh = round(max(0.0, sm - 0.1), 2)
    return {
        "slope_mountain_deg": sm,
        "slope_hills_deg": sh,
        "slope_median_deg": round(med, 2),
        "slope_rel_floor_deg": round(max(SLOPE_HILLS_FLOOR_DEG * 0.5, med * SLOPE_REL_TOL), 2),
        "elev_mountain_m": round(clamp(percentile(elev, ELEV_MOUNTAIN_PCT),
                                       ELEV_MOUNTAIN_MIN_M, ELEV_MOUNTAIN_MAX_M), 1),
        "elev_hills_m": round(clamp(percentile(elev, ELEV_HILLS_PCT),
                                    ELEV_HILLS_MIN_M, ELEV_HILLS_MAX_M), 1),
        "elev_swamp_m": round(min(percentile(elev, ELEV_SWAMP_PCT), ELEV_SWAMP_MAX_M), 1),
        "relief_broken_m": RELIEF_BROKEN_MIN_M,
        "relief_hills_m": RELIEF_HILLS_MIN_M,
        "relief_mountain_m": RELIEF_MOUNTAIN_MIN_M,
    }


def classify_base(grid: HexGrid, elev, slopes, relief, th, seed):
    """
    先于水系/道路生成，产出基础 terrain（不含 river/city）与辅助噪声场。
    判据（slope 与 relief 互补：前者是 5km 基线的区域坡度，后者是邻域高差）：
      陡 = slope ≥ slope_mountain 或 relief ≥ RELIEF_MOUNTAIN_MIN_M
      缓陡 = slope ≥ slope_hills 或 relief ≥ RELIEF_HILLS_MIN_M
      1) 陡  且 高程 ≥ elev_mountain ⇒ mountain
      2) 陡  但 高程不足（陡而低的破碎地）⇒ rough
      3) 缓陡 且 高程 ≥ elev_hills    ⇒ hills
      4) 缓陡 但 高程不足              ⇒ rough
      5) 其余（平缓低地）：森林噪声达阈 + 坡度适中 + 非山顶 ⇒ woods；否则 ⇒ clear
         （swamp / water 需要「距河距离场」，在 build_payload 里二次定夺）
    """
    terr = ["clear"] * (grid.width * grid.height)
    aux = {}                      # index → (湿度噪声, 湖泊噪声)
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        e, sl, rl = elev[i], slopes[i], relief[i]
        px, py = grid.center((q, r))
        sx = (px - grid.mercator_ox) / 1000.0    # 相对原点的「公里」位置，作为噪声自变量
        sy = (grid.mercator_oy - py) / 1000.0
        forest = fbm(sx / NOISE_SCALE_WOODS_KM + 3.1, sy / NOISE_SCALE_WOODS_KM - 1.7, seed + 337, octaves=4)
        wet = fbm(sx / NOISE_SCALE_WET_KM - 5.5, sy / NOISE_SCALE_WET_KM + 2.3, seed + 541, octaves=4)
        lake = fbm(sx / NOISE_SCALE_LAKE_KM + 11.0, sy / NOISE_SCALE_LAKE_KM + 7.0, seed + 887, octaves=3)
        aux[i] = (wet, lake)
        steep = sl >= th["slope_mountain_deg"] or rl >= RELIEF_MOUNTAIN_MIN_M
        rolling = sl >= th["slope_hills_deg"] or rl >= RELIEF_HILLS_MIN_M
        broken = rl >= RELIEF_BROKEN_MIN_M
        if steep:
            terr[i] = "mountain" if e >= th["elev_mountain_m"] else "rough"
        elif rolling:
            terr[i] = "hills" if e >= th["elev_hills_m"] else "rough"
        elif broken:
            terr[i] = "rough"
        elif forest >= WOODS_NOISE_MIN and sl <= WOODS_MAX_SLOPE_DEG \
                and e < th["elev_mountain_m"] * WOODS_MAX_ELEV_REL:
            terr[i] = "woods"
        else:
            terr[i] = "clear"
    return terr, aux


# =====================================================================================
# 8) 图上最短路径（Dijkstra；节点代价 + 可选转弯代价；确定性 tie-break）
# =====================================================================================


def dijkstra(grid: HexGrid, starts, targets, node_cost, turn_cost=None):
    """
    starts/targets：格子集合。状态 = (prev_cell, cur_cell)，以便施加转弯惩罚（去锯齿）。
    返回最优路径（含起终点），无解返回 None。堆元素带全局递增 seq，
    使 cost 相同时的比较落在 seq 上（永不用比较坐标元组/None），结果确定。
    """
    dist = {}
    back = {}
    heap = []
    seq = 0
    for s in sorted(starts):
        state = (None, s)
        dist[state] = node_cost(*s)
        back[state] = None
        seq += 1
        heappush(heap, (dist[state], s[1], s[0], seq, state))
    best = None
    while heap:
        d, _, _, _, state = heappop(heap)
        if d > dist.get(state, float("inf")) + 1e-9:
            continue
        prev_cell, cur = state
        if cur in targets:
            best = state
            break
        for n in grid.neighbors(*cur):
            if prev_cell is not None and n == prev_cell:
                continue                                   # 禁止原路折返
            cost = d + node_cost(*n)
            if turn_cost is not None:
                cost += turn_cost(prev_cell, cur, n)
            nxt = (cur, n)
            if cost < dist.get(nxt, float("inf")) - 1e-9:
                dist[nxt] = cost
                back[nxt] = state
                seq += 1
                heappush(heap, (cost, n[1], n[0], seq, nxt))
    if best is None:                                       # target 被松弛但从未弹出
        cands = [(v, k) for k, v in dist.items() if k[1] in targets]
        if not cands:
            return None
        best = min(cands, key=lambda t: (t[0], t[1][1][1], t[1][1][0], repr(t[1][0])))[1]
    path = []
    node = best
    while node is not None:
        path.append(node[1])
        node = back.get(node)
    path.reverse()
    return path


def extract_river_band(grid: HexGrid, elev):
    """
    真实/合成 DEM 通用：在「谷底高程容差」内做 北边缘 → 南边缘 的最低代价路径，
    得到一条连续、纵向贯通的河带。容差按 RIVER_ROUTE_STEPS 逐级放大，
    保证任何 DEM 都能产出一条完整河流（不产出半成品）。
    代价 = 节点高程 + 常数步代价（抑制过长蜿蜒） + 转弯代价 + 高格惩罚。
    """
    n = grid.width * grid.height
    e_min, e_max = min(elev), max(elev)
    span = max(1.0, e_max - e_min)
    step_pen = RIVER_STEP_PENALTY_REL * span + 1.0
    all_starts = [(q, 0) for q in range(grid.width)]
    all_targets = [(q, grid.height - 1) for q in range(grid.width)]
    for level, pct in enumerate(RIVER_ROUTE_STEPS):
        low_cut = e_min + pct * span
        ridge_cut = e_min + RIVER_ROUTE_RIDGE_PCT * span
        hard_cap = e_min * RIVER_MIN_ELEV_RATIO

        def node_cost(q, r, low_cut=low_cut, ridge_cut=ridge_cut, hard_cap=hard_cap,
                      level=level, step_pen=step_pen, span=span):
            e = elev[grid.index(q, r)]
            c = e + step_pen
            if level < len(RIVER_ROUTE_STEPS) - 1:
                if e > ridge_cut:
                    c += 25.0 * span
                elif e > hard_cap + 0.5 * span:
                    c += 6.0 * span
            if e > low_cut:
                c += 3.0 * (e - low_cut) + 0.4 * span
            return c

        def bend(a, b, c, grid=grid, step_pen=step_pen):
            return RIVER_BEND_PENALTY_PER_DEG * grid.turn_angle(a, b, c)

        path = dijkstra(grid, all_starts, set(all_targets), node_cost, turn_cost=bend)
        if path and len(path) >= grid.height and \
                min(p[1] for p in path) == 0 and max(p[1] for p in path) == grid.height - 1 and \
                len(set(path)) == len(path):
            return sorted(set(path))
    # 极端兜底：中央列直通（实践中不会走到）
    q_mid = grid.width // 2
    return sorted([(q_mid, r) for r in range(grid.height)])


# =====================================================================================
# 9) 道路 / 桥梁 / 聚落
# =====================================================================================


def bridge_target_count(grid: HexGrid) -> int:
    """桥梁目标数：随宽度缩放，并 clamp 到 [BRIDGE_MIN, BRIDGE_MAX]（渡河作战难度控制点）。"""
    return int(clamp(int(round(grid.width / float(BRIDGE_SPACING_COLS))), BRIDGE_MIN, BRIDGE_MAX))


def road_corridor_rows(grid: HexGrid, seed: int, k: int):
    """k 条大致东西向走廊：行号均匀分布 + 轻微确定性抖动。"""
    rows = []
    for i in range(k):
        base = int(round(grid.height * (i + 1) / (k + 1.0)))
        jitter = int(round(2.0 * (fbm(i * 3.7, 0.5, seed + 1201, octaves=2) - 0.5)))
        rows.append(int(clamp(base + jitter, 1, grid.height - 2)))
    rows = sorted(set(rows))
    while len(rows) < BRIDGE_MIN:                   # 小格网也要保证 ≥2 处渡河点
        rows.append(int(clamp(rows[-1] + 3, 1, grid.height - 2)))
        rows = sorted(set(rows))
    return rows[:BRIDGE_MAX]


def build_roads(grid: HexGrid, elev, terr, river_band, seed: int):
    """
    每条走廊在河带上指定唯一桥位（河带中该行号最接近走廊的格），再从西/东边缘各修一条路汇到桥位。
    非桥位的河格代价 = ROAD_RIVER_BLOCK_COST ⇒ 桥数 == 走廊数 ∈ [BRIDGE_MIN, BRIDGE_MAX]。
    返回 (road_cells, bridge_cells)。
    """
    river_set = set(river_band)
    k = bridge_target_count(grid)
    rows = road_corridor_rows(grid, seed, k)
    road = set()
    for target_row in rows:
        crossing = min(river_band, key=lambda c: (abs(c[1] - target_row), c[0]))
        lo = max(0, target_row - ROAD_ROW_TOLERANCE)
        hi = min(grid.height, target_row + ROAD_ROW_TOLERANCE + 1)
        starts_w = {(0, r) for r in range(lo, hi)}
        starts_e = {(grid.width - 1, r) for r in range(lo, hi)}

        def cost(q, r, crossing=crossing, target_row=target_row):
            i = grid.index(q, r)
            t = terr[i]
            c = elev[i] + ROAD_ROW_DEVIATION_COST * abs(r - target_row)
            if t == "river" and (q, r) != crossing:
                c += ROAD_RIVER_BLOCK_COST
            if t == "water":
                c += ROAD_WATER_BLOCK_COST
            if t == "mountain":
                c += ROAD_MOUNTAIN_PENALTY
            return c

        def bend(a, b, c, grid=grid):
            return ROAD_BEND_PENALTY_PER_DEG * grid.turn_angle(a, b, c)

        pw = dijkstra(grid, starts_w, {crossing}, cost, turn_cost=bend)
        pe = dijkstra(grid, starts_e, {crossing}, cost, turn_cost=bend)
        if pw is None or pe is None:
            pw = [(q, target_row) for q in range(crossing[0] + 1)]
            pe = [(q, target_row) for q in range(crossing[0], grid.width)]
        for cell in list(pw) + list(reversed(pe)):
            road.add(cell)
    # 实际渡河点 = 道路 ∩ 河带（桥只可能出现在这里）
    bridges = sorted(road & river_set, key=lambda c: (c[1], c[0]))
    return road, set(bridges)


def place_cities(grid: HexGrid, terr, road, river_dist, bridge_set, seed):
    """
    聚落候选：道路格 + 紧邻河带（渡口/桥头堡），邻域道路越多（交叉口）加分越高。
    数量 CITY_MIN..CITY_MAX，相互间距 ≥ CITY_MIN_SEPARATION。返回 {cell: city 等级(1/2)}。
    """
    cand = []
    for (q, r) in sorted(road):
        i = grid.index(q, r)
        if (q, r) in bridge_set or terr[i] in ("water", "swamp", "mountain", "river"):
            continue
        d = river_dist.get((q, r), 99)
        if d > CITY_RIVER_MAX_DIST:
            continue
        nbr_roads = sum(1 for n in grid.neighbors(q, r) if n in road)
        px, py = grid.center((q, r))
        sx = (px - grid.mercator_ox) / 1000.0
        sy = (grid.mercator_oy - py) / 1000.0
        noise = fbm(sx / NOISE_SCALE_CITY_KM + 4.4, sy / NOISE_SCALE_CITY_KM - 2.2, seed + 2203, octaves=3)
        score = (3.0 if d == 1 else 1.4) + CITY_JUNCTION_BONUS * nbr_roads + noise
        cand.append((score, d, i, (q, r)))
    cand.sort(key=lambda t: (-t[0], t[2]))
    picks = []
    for _, _, _, cell in cand:
        if len(picks) >= CITY_TARGET:
            break
        if all(axial_distance(cell, p) >= CITY_MIN_SEPARATION for p in picks):
            picks.append(cell)
    if len(picks) < CITY_MIN:                       # 兜底：放宽河岸约束，仅要求道路格
        for cell in sorted(road):
            if len(picks) >= CITY_MIN:
                break
            if cell in bridge_set or terr[grid.index(*cell)] in ("water", "river"):
                continue
            if all(axial_distance(cell, p) >= 2 for p in picks):
                picks.append(cell)
    marks = {}
    for idx, cell in enumerate(picks[:CITY_MAX]):
        marks[cell] = 2 if idx == 0 else 1          # 最优者为首府（较大城镇）
    return marks


# =====================================================================================
# 10) 组装 + 自校验
# =====================================================================================


def build_hexes(grid, elev, slopes, terr, road, river_set, bridge_set, city_marks):
    out = []
    for (q, r) in grid.iter_cells():                # r 外层、q 内层
        i = grid.index(q, r)
        e = float(elev[i])
        e_val = int(round(e)) if abs(e - round(e)) < 0.05 else round(e, 1)
        out.append({
            "q": q,
            "r": r,
            "terrain": terr[i],
            "elev": e_val,
            "slope": round(float(slopes[i]), 1),
            "road": (q, r) in road,
            "river": (q, r) in river_set,
            "bridge": (q, r) in bridge_set,
            "city": city_marks.get((q, r), 0),
        })
    return out


def validate(data: dict, grid: HexGrid, cell_km: float, strict: bool = True):
    """
    契约自校验，返回 [(级别, 检查名, 说明)]，级别 OK / WARN / FAIL：
      · 格数 = width*height、q/r 范围正确、无重复、遍历顺序 r 外 q 内
      · terrain 全部落在白名单
      · |sqrt(3)*radius_m - cell_km*1000| < 1e-6
      · bridge 只出现在 river 格；道路∩河带 == 桥位；桥数 ∈ [2,4]
      · city ≥ 1 ⇒ terrain == city；聚落数 ∈ [2,4]
      · 河带单连通且纵向贯通；stats 与 hexes 一致；ox/oy 中心对齐；bbox 覆盖全部格心
    """
    res = []
    meta = data["meta"]
    hexes = data["hexes"]
    w, h = meta["width"], meta["height"]
    expect = w * h
    by_cell = {(hx["q"], hx["r"]): hx for hx in hexes}

    ids = [(hx["q"], hx["r"]) for hx in hexes]
    res.append((("OK" if len(ids) == expect else "FAIL"), "格数完整",
                "%d / 期望 %d (=width*height)" % (len(ids), expect)))
    dup = len(ids) - len(set(ids))
    res.append((("OK" if dup == 0 and len(by_cell) == expect else "FAIL"), "q/r 无重复",
                "重复 %d，唯一 %d" % (dup, len(by_cell))))
    order_ok = len(ids) == expect and all(ids[i] == (i % w, i // w) for i in range(expect))
    res.append((("OK" if order_ok else "FAIL"), "顺序 r外/q内", "index = r*width + q"))
    range_ok = all(0 <= hx["q"] < w and 0 <= hx["r"] < h for hx in hexes)
    res.append((("OK" if range_ok else "FAIL"), "q/r 范围", "q∈[0,%d] r∈[0,%d]" % (w - 1, h - 1)))

    bad_t = sorted({hx["terrain"] for hx in hexes} - TERRAIN_SET)
    res.append((("OK" if not bad_t else "FAIL"), "terrain 取值合法",
                "非法值: %s" % (bad_t if bad_t else "无")))

    dev = abs(math.sqrt(3.0) * meta["radius_m"] - cell_km * 1000.0)
    res.append((("OK" if dev < 1e-6 else "FAIL"), "sqrt(3)*radius≈cell_km",
                "%.6f m vs %.6f m，差 %.3e" % (math.sqrt(3.0) * meta["radius_m"], cell_km * 1000.0, dev)))

    bridge_cells = [(hx["q"], hx["r"]) for hx in hexes if hx["bridge"]]
    bad_bridge = [c for c in bridge_cells if by_cell[c]["terrain"] != "river" or not by_cell[c]["river"]]
    res.append((("OK" if not bad_bridge else "FAIL"), "bridge 仅在 river 格",
                "越界桥位: %s" % (bad_bridge if bad_bridge else "无")))
    crossings = {(hx["q"], hx["r"]) for hx in hexes if hx["road"] and hx["river"]}
    res.append((("OK" if crossings == set(bridge_cells) else "FAIL"), "道路∩河 = 桥位",
                "渡河点 %d，桥 %d" % (len(crossings), len(bridge_cells))))
    nb = len(bridge_cells)
    res.append((("OK" if BRIDGE_MIN <= nb <= BRIDGE_MAX else "WARN"), "桥梁总数 2–4",
                "实际 %d 座 @ %s" % (nb, bridge_cells)))
    res.append((("OK" if all(by_cell[c]["road"] for c in bridge_cells) else "FAIL"),
                "桥在道路上", "%s" % (bridge_cells if bridge_cells else "无")))

    bad_city = [(hx["q"], hx["r"], hx["terrain"]) for hx in hexes if hx["city"] >= 1 and hx["terrain"] != "city"]
    res.append((("OK" if not bad_city else "FAIL"), "city≥1 ⇒ terrain=city",
                "%s" % (bad_city if bad_city else "无")))
    nc = sum(1 for hx in hexes if hx["city"] >= 1)
    res.append((("OK" if CITY_MIN <= nc <= CITY_MAX else "WARN"), "聚落数量 2–4", "实际 %d" % nc))

    river_cells = {(hx["q"], hx["r"]) for hx in hexes if hx["terrain"] == "river"}
    north = [c for c in river_cells if c[1] == 0]
    south = [c for c in river_cells if c[1] == h - 1]
    res.append((("OK" if north and south and len(river_cells) >= h else "FAIL"), "河带纵向贯通",
                "r=0: %s / r=%d: %s / 共 %d 格" % (sorted(north), h - 1, sorted(south), len(river_cells))))
    seen = set()
    if river_cells:
        stack = [sorted(river_cells)[0]]
        while stack:
            cur = stack.pop()
            if cur in seen:
                continue
            seen.add(cur)
            stack.extend(n for n in grid.neighbors(*cur) if n in river_cells and n not in seen)
        res.append((("OK" if len(seen) == len(river_cells) else "WARN"), "河带单连通",
                    "%d/%d 格可达" % (len(seen), len(river_cells))))

    num_ok = all(isinstance(hx["elev"], (int, float)) and math.isfinite(float(hx["elev"])) and
                 isinstance(hx["slope"], (int, float)) and math.isfinite(float(hx["slope"]))
                 and float(hx["slope"]) >= 0.0 and isinstance(hx["city"], int)
                 and isinstance(hx["road"], bool) and isinstance(hx["river"], bool)
                 and isinstance(hx["bridge"], bool) for hx in hexes)
    res.append((("OK" if num_ok else "FAIL"), "字段类型/数值有效", "elev·slope·bool·city"))

    cx = meta["mercator_ox"] + grid.hex_width_m * ((w - 1) / 2.0 + (h - 1) / 4.0)
    cy = meta["mercator_oy"] - grid.row_step_m * ((h - 1) / 2.0)
    dlon = abs(x_to_lon(cx) - meta["center_lonlat"][0])
    dlat = abs(y_to_lat(cy) - meta["center_lonlat"][1])
    res.append((("OK" if dlon < 1e-9 and dlat < 1e-9 else "FAIL"), "ox/oy 中心对齐",
                "Δlon=%.2e Δlat=%.2e" % (dlon, dlat)))

    bb = meta["bbox"]
    inside = (bb[0] < bb[2] and bb[1] < bb[3] and
              all(bb[0] <= x_to_lon(grid.center(c)[0]) <= bb[2] and
                  bb[1] <= y_to_lat(grid.center(c)[1]) <= bb[3] for c in grid.iter_cells()))
    res.append((("OK" if inside else "FAIL"), "bbox 覆盖格心",
                "%s" % [round(v, 5) for v in bb]))

    counts = {}
    for hx in hexes:
        counts[hx["terrain"]] = counts.get(hx["terrain"], 0) + 1
    e_all = [float(hx["elev"]) for hx in hexes]
    st = meta["stats"]
    sc_ok = (counts == st["terrain_counts"]
             and abs(float(st["elev_min"]) - min(e_all)) < 1e-6
             and abs(float(st["elev_max"]) - max(e_all)) < 1e-6)
    res.append((("OK" if sc_ok else "FAIL"), "stats 与 hexes 一致",
                json.dumps(counts, ensure_ascii=False, sort_keys=True)))

    roads = {(hx["q"], hx["r"]) for hx in hexes if hx["road"]}
    west = sorted(c for c in roads if c[0] == 0)
    east = sorted(c for c in roads if c[0] == w - 1)
    # 逐分量检查：每条走廊是一个独立连通分量，且必须同时触及西、东边缘（无断头路）
    comps = []
    unvisited = set(roads)
    while unvisited:
        seed_cell = sorted(unvisited)[0]
        stack = [seed_cell]
        comp = set()
        while stack:
            cur = stack.pop()
            if cur in comp:
                continue
            comp.add(cur)
            stack.extend(n for n in grid.neighbors(*cur) if n in roads and n not in comp)
        unvisited -= comp
        comps.append(comp)
    conn = bool(roads) and bool(west) and bool(east) and \
        all((len(c & set(west)) > 0 and len(c & set(east)) > 0) for c in comps)
    res.append((("OK" if conn else "WARN"), "每条走廊通东西边缘",
                "道路 %d 格 / 分量 %d 个(尺寸 %s) / 西端 %d / 东端 %d"
                % (len(roads), len(comps), sorted(len(c) for c in comps), len(west), len(east))))
    if strict:
        res.append((("OK" if meta.get("source") in (SOURCE_REAL, SOURCE_SYNTH)
                    or str(meta.get("source", "")).startswith((SOURCE_REAL, SOURCE_SYNTH)) else "WARN"),
                    "meta.source 标注", str(meta.get("source"))))
    return res


def print_report(res, title="自校验"):
    fails = warns = 0
    print("\n=== %s ===" % title)
    for lvl, name, note in res:
        if lvl == "FAIL":
            fails += 1
        elif lvl == "WARN":
            warns += 1
        print("  [%-4s] %-20s %s" % (lvl, name, note))
    print("  → 合计 %d 项：FAIL %d / WARN %d / OK %d"
          % (len(res), fails, warns, len(res) - fails - warns))
    return fails, warns


# =====================================================================================
# 11) 主流程
# =====================================================================================


def resolve_default_out() -> str:
    """默认输出 = <repo>/data/gis/rhine_5km.json（相对脚本定位，不依赖 CWD）。"""
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "data").is_dir() or (parent / "hexfront").is_dir():
            return str(parent / DEFAULT_OUT_REL)
    return str((here.parent.parent / DEFAULT_OUT_REL).resolve())


def get_elevations(grid: HexGrid, args):
    """返回 (elevations, source, note)。真实 DEM 失败 ⇒ 合成降级，绝不抛异常。"""
    if args.load_dem:
        try:
            cache = json.loads(Path(args.load_dem).read_text(encoding="utf-8"))
            if (cache.get("width"), cache.get("height"), cache.get("cell_km")) == \
                    (args.width, args.height, args.cell_km) and isinstance(cache.get("elev"), list) \
                    and len(cache["elev"]) == grid.width * grid.height:
                return ([float(v) for v in cache["elev"]], SOURCE_REAL,
                        "replayed from cache %s (original: %s)" % (args.load_dem, cache.get("source", "?")))
            print("[dem] 缓存与当前格网参数不匹配 ⇒ 忽略缓存", file=sys.stderr)
        except Exception as exc:
            print("[dem] 缓存读取失败（%s）⇒ 忽略缓存" % exc, file=sys.stderr)
    if args.offline:
        return synthetic_dem(grid, args.seed), SOURCE_SYNTH, "forced --offline synthetic DEM"
    print("[dem] 抓取 Open-Meteo 高程：%d 点 / 每批 ≤%d / 批间 %.1fs / 重试 %d 次"
          % (grid.width * grid.height, args.batch, API_SLEEP_S, API_RETRIES))
    got = None
    try:
        got = fetch_openmeteo(grid, args.batch, allow_requests=not args.no_requests, quiet=args.quiet)
    except Exception as exc:                       # 任何未预期异常都降级，不退出
        print("[dem] 高程抓取异常（%s）⇒ 降级" % exc, file=sys.stderr)
        got = None
    if got is None or len(got) != grid.width * grid.height:
        return synthetic_dem(grid, args.seed), SOURCE_SYNTH, "open-meteo unavailable/failed -> deterministic synthetic DEM"
    if args.save_dem:
        try:
            p = Path(args.save_dem)
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(json.dumps({"source": SOURCE_REAL, "lon": args.lon, "lat": args.lat,
                                     "width": args.width, "height": args.height,
                                     "cell_km": args.cell_km, "elev": got}, ensure_ascii=False),
                         encoding="utf-8")
            print("[dem] 高程已缓存 → %s" % p.resolve())
        except Exception as exc:
            print("[dem] 缓存写入失败（%s）" % exc, file=sys.stderr)
    return got, SOURCE_REAL, ""


def build_payload(args):
    grid = HexGrid(args.lon, args.lat, args.width, args.height, args.cell_km)
    elev, source, note = get_elevations(grid, args)
    elev = [round(max(-200.0, float(v)), 1) for v in elev]
    slopes = compute_slopes(grid, elev)
    relief, uplift = compute_relief(grid, elev)
    th = build_thresholds(elev, slopes)
    print("[dem] source=%s%s" % (source, (" (%s)" % note) if note else ""))
    print("[dem] elev %.1f–%.1f m (median %.1f) / slope %.1f–%.1f deg (median %.1f) / relief %.0f–%.0f m (median %.0f)"
          % (min(elev), max(elev), percentile(elev, 0.5), min(slopes), max(slopes), percentile(slopes, 0.5),
             min(relief), max(relief), percentile(relief, 0.5)))
    print("[dem] 生效阈值: slope hills/mountain = %.2f°/%.2f°, rel floor %.2f°, elev hills/mountain/swamp = %.0f/%.0f/%.0f m, relief hills/mountain = %.0f/%.0f m"
          % (th["slope_hills_deg"], th["slope_mountain_deg"], th["slope_rel_floor_deg"],
             th["elev_hills_m"], th["elev_mountain_m"], th["elev_swamp_m"],
             RELIEF_HILLS_MIN_M, RELIEF_MOUNTAIN_MIN_M))

    terr, aux = classify_base(grid, elev, slopes, relief, th, args.seed)

    band = extract_river_band(grid, elev)
    river_set = set(band)
    for (q, r) in river_set:
        terr[grid.index(q, r)] = "river"
    river_dist = grid.distance_field(river_set)

    # 河漫滩沼泽 / 洼地湖泊（需要 river_dist 才能定夺；不覆盖河带、丘陵、山地）
    lake_cands = []
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        if terr[i] == "river":
            continue
        wet, lake = aux[i]
        d = river_dist.get((q, r), 99)
        very_low = elev[i] <= th["elev_swamp_m"]
        if very_low and wet >= SWAMP_NOISE_MIN and d <= SWAMP_RIVER_MAX_DIST \
                and terr[i] in SWAMP_SOURCE_TERRAIN:
            terr[i] = "swamp"
        elif very_low and lake >= WATER_NOISE_MIN and uplift[i] <= UPLIFT_LAKE_MAX_M \
                and d >= WATER_RIVER_MIN_DIST and terr[i] in WATER_SOURCE_TERRAIN:
            lake_cands.append((lake, i))
    lake_cands.sort(key=lambda t: (-t[0], t[1]))
    for _, i in lake_cands[:MAX_LAKE_CELLS]:
        terr[i] = "water"

    road, bridge_set = build_roads(grid, elev, terr, band, args.seed)
    city_marks = place_cities(grid, terr, road, river_dist, bridge_set, args.seed)
    for (q, r) in city_marks:
        terr[grid.index(q, r)] = "city"

    hexes = build_hexes(grid, elev, slopes, terr, road, river_set, bridge_set, city_marks)

    counts = {}
    for hx in hexes:
        counts[hx["terrain"]] = counts.get(hx["terrain"], 0) + 1
    e_all = [float(hx["elev"]) for hx in hexes]
    s_all = [float(hx["slope"]) for hx in hexes]
    bb = grid.bbox()
    meta = {
        "source": source,
        "source_note": note,
        "bbox": [round(bb[0], 6), round(bb[1], 6), round(bb[2], 6), round(bb[3], 6)],
        "center_lonlat": [args.lon, args.lat],
        "cell_km": args.cell_km,
        "width": args.width,
        "height": args.height,
        "mercator_ox": grid.mercator_ox,
        "mercator_oy": grid.mercator_oy,
        "radius_m": grid.radius_m,
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "stats": {
            "terrain_counts": {k: counts[k] for k in sorted(counts)},
            "elev_min": min(e_all),
            "elev_max": max(e_all),
            "elev_mean": round(sum(e_all) / len(e_all), 1),
            "slope_min": round(min(s_all), 1),
            "slope_max": round(max(s_all), 1),
            "slope_mean": round(sum(s_all) / len(s_all), 2),
            "relief_min": round(min(relief), 1),
            "relief_max": round(max(relief), 1),
            "relief_median": round(percentile(relief, 0.5), 1),
            "river_cells": len(river_set),
            "road_cells": len(road),
            "bridge_count": len(bridge_set),
            "city_cells": len(city_marks),
        },
        "projection": "EPSG:3857 (Web Mercator); pointy-top axial hexes, cx=ox+W*(q+r/2), cy=oy-1.5*radius*r",
        "hex_order": "r outer loop, q inner loop; index = r*width + q",
        "seed": args.seed,
        "cell_row_spacing_m": grid.row_step_m,
        "dem_points": len(e_all),
        "effective_thresholds": th,
        "bridge_hexes": [{"q": q, "r": r} for (q, r) in sorted(bridge_set, key=lambda c: (c[1], c[0]))],
    }
    return grid, {"meta": meta, "hexes": hexes}


def main(argv=None):
    # Windows 控制台默认 GBK，中文输出可能崩 ⇒ 尽力切 UTF-8
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    try:
        locale.setlocale(locale.LC_ALL, "")
    except Exception:
        pass

    p = argparse.ArgumentParser(
        prog="hexgen.py",
        description="生成兵棋引擎的 GIS 六角格地形数据包（Open-Meteo 真实高程；失败自动降级确定性合成 DEM）。",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
        epilog="示例: python hexgen.py --lon 6.95 --lat 51.42 --width 26 --height 20 --cell-km 5 "
               "--out ../../data/gis/rhine_5km.json",
    )
    p.add_argument("--lon", type=float, default=DEFAULT_LON, help="格网中心经度（WGS84）")
    p.add_argument("--lat", type=float, default=DEFAULT_LAT, help="格网中心纬度（WGS84）")
    p.add_argument("--width", type=int, default=DEFAULT_WIDTH, help="列数（q ∈ [0,width-1]）")
    p.add_argument("--height", type=int, default=DEFAULT_HEIGHT, help="行数（r ∈ [0,height-1]）")
    p.add_argument("--cell-km", dest="cell_km", type=float, default=DEFAULT_CELL_KM,
                   help="格距（尖顶格宽 sqrt(3)*radius = cell_km）")
    p.add_argument("--out", default=None, help="输出 JSON（默认 <repo>/data/gis/rhine_5km.json）")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED, help="合成 DEM / 噪声场种子（确定性）")
    p.add_argument("--batch", type=int, default=API_BATCH_SIZE, help="高程 API 每批点数（1..100）")
    p.add_argument("--offline", action="store_true", help="跳过网络，强制使用确定性合成 DEM")
    p.add_argument("--no-requests", dest="no_requests", action="store_true",
                   help="只用 urllib（默认允许优先尝试已安装的 requests，urllib 始终兜底）")
    p.add_argument("--save-dem", default=None, metavar="PATH", help="把抓到的真实高程缓存到 JSON，便于离线复现")
    p.add_argument("--load-dem", default=None, metavar="PATH", help="优先从缓存读取高程（跳过网络）")
    p.add_argument("--only-validate", dest="only_validate", action="store_true",
                   help="只校验 --out 已存在的数据包，不生成")
    p.add_argument("--allow-invalid", dest="allow_invalid", action="store_true",
                   help="即使自校验 FAIL 也写文件（默认不产出半成品）")
    p.add_argument("--quiet", action="store_true", help="减少进度输出")
    args = p.parse_args(argv)

    if not 1 <= args.batch <= 100:
        p.error("--batch 必须在 1..100")
    if args.width < 6 or args.height < 6:
        p.error("--width/--height 至少为 6")
    if args.cell_km <= 0:
        p.error("--cell-km 必须为正")
    if args.out is None:
        args.out = resolve_default_out()
    out_path = Path(args.out)

    if args.only_validate:
        data = json.loads(out_path.read_text(encoding="utf-8"))
        m = data["meta"]
        g = HexGrid(m["center_lonlat"][0], m["center_lonlat"][1], m["width"], m["height"], m["cell_km"])
        res = validate(data, g, m["cell_km"], strict=False)
        fails, warns = print_report(res, "自校验 %s" % out_path)
        return 1 if fails else 0

    grid, payload = build_payload(args)
    res = validate(payload, grid, args.cell_km, strict=True)
    fails, warns = print_report(res)
    st = payload["meta"]["stats"]
    print("\n[meta] source = %s%s" % (payload["meta"]["source"],
                                      (" | " + payload["meta"]["source_note"]) if payload["meta"]["source_note"] else ""))
    print("[meta] bbox = %s" % payload["meta"]["bbox"])
    print("[meta] radius_m = %.3f, row_step = %.3f m, ox/oy = %.3f / %.3f"
          % (payload["meta"]["radius_m"], grid.row_step_m,
             payload["meta"]["mercator_ox"], payload["meta"]["mercator_oy"]))
    print("[meta] elev %.1f–%.1f m / slope %.1f–%.1f deg / river %d / road %d / bridge %d @ %s / city %d"
          % (st["elev_min"], st["elev_max"], st["slope_min"], st["slope_max"],
             st["river_cells"], st["road_cells"], st["bridge_count"],
             [(b["q"], b["r"]) for b in payload["meta"]["bridge_hexes"]], st["city_cells"]))
    print("[meta] terrain = %s" % json.dumps(st["terrain_counts"], ensure_ascii=False))

    if fails and not args.allow_invalid:
        print("\n[abort] 自校验有 %d 项 FAIL ⇒ 未写出文件（不产出半成品）。"
              "确需强行写出请加 --allow-invalid。" % fails, file=sys.stderr)
        return 1

    out_path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(payload, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    with open(str(out_path), "w", encoding="utf-8", newline="\n") as fp:
        fp.write(text)
    print("\n[write] %s" % Path(out_path).resolve())
    print("[write] %d 格 / %d 字节 / sha256[:16]=%s"
          % (len(payload["hexes"]), len(text.encode("utf-8")),
             hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
