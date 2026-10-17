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

格网几何（尖顶格，odd-r 偏移 /「砖墙」约定，整盘铺成矩形）：
  格心：cx = ox + W*(q + (r&1)/2)   cy = oy - 1.5*radius*r      W = sqrt(3)*radius = cell_km*1000
  邻居：按行奇偶取偏移表（偶数行 NE=(q,r-1)，奇数行 NE=(q+1,r-1)）  行距（南北）= 1.5*radius
  距离/直线等立方体运算先转 axial：aq = q - (r - (r&1))/2, ar = r
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

# --- 真实水系矢量（Natural Earth 10m gpkg，见 rasters.water_by_hex）---
LAKE_SHARE_MIN = 0.30              # 湖面占格面积 ≥ ⇒ water（静水，不可通行）
LAKE_SHARE_LEVEL2 = 0.60           # 占比很高的格优先计入湖泊上限（真湖心）
REAL_WATER_MAX_FRACTION = 0.20     # 真实水面（湖）格占全图上限，防大河口/湖区把地图变水域

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
RIVER_RUN_BRIDGE_MAX = 3           # 道路∩河带的连通段 ≤ 此格数 ⇒ 真渡河点（桥）；更长 ⇒ 沿河公路
CITY_TARGET = 4
CITY_MIN = 2
CITY_MAX = 4
CITY_ABS_MAX = 24                # 真实城区连片时的绝对上限（游戏性截断）
CITY_MIN_SEPARATION = 3            # 聚落最小六角格间距
CITY_JUNCTION_BONUS = 0.35         # 邻域道路越多（交叉口）加分越大
CITY_RIVER_MAX_DIST = 2            # 聚落优先选河岸 / 渡口

# --- 地物层：铁路 / 车站 / 隧道 / 电网 / 电厂 / 变电站 / 机场 ---
# 铁路：一条近东西干线穿首府，格网够宽时补一条支线到次级聚落。
RAIL_BRANCH_MIN_CELLS = 500        # 小于此格数只做单线（支线会把图挤满）
RAIL_RIVER_BLOCK_COST = 1.0e6      # 铁路只在既有的桥上渡河 ⇒ 铁路∩河 ⊆ 桥位
RAIL_WATER_BLOCK_COST = 4.0e5
RAIL_MOUNTAIN_PENALTY = 900.0
RAIL_BEND_PENALTY_PER_DEG = 2.5
RAIL_ROAD_BONUS = 60.0             # 现实里铁路与公路共走廊，轻微吸引
RAIL_STATION_MAX_DIST = 2          # 车站取「距聚落 ≤ 此值」的铁路格
# 隧道：线路在陡区「内部」穿越（前后都沿线）才判隧道，刚上山的那个格不算。
TUNNEL_LINE_NEIGHBORS = 2
TUNNEL_TERRAIN = ("hills", "mountain")
TUNNEL_DERIVED_MAX = 6
TUNNEL_MIN_RELIEF_M = 40.0         # 局部高差不足 ⇒ 不是「穿山」而是缓坡过脊
# 电力：电厂近水（冷却/水电）、变电站进城镇、线路按「最近已连通节点」成网。
PLANT_MIN = 1
PLANT_MAX = 2
PLANT_RIVER_MAX_DIST = 1
PLANT_SEPARATION = 4
PLANT_CITY_MIN_DIST = 2            # 厂区不压在城镇格上（但要在供水/供电范围内）
SUBSTATION_MAX = 4
POWER_WATER_BLOCK_COST = 4.0e5
POWER_MOUNTAIN_PENALTY = 500.0
POWER_BEND_PENALTY_PER_DEG = 1.5
POWER_MAX_SLOPE_DEG = 9.0          # 线路尽量沿低坡走（罚，不是禁）
POWERLINE_MAX_FRACTION = 0.10      # 线路格占全图上限，防成网后铺满地图
# 机场：真实点优先；战区里没有真实机场要素时，在首府近郊找一块平地派生一处野场。
AIRPORT_DERIVED_SLOPE_MAX_DEG = 1.5
AIRPORT_CITY_MIN_DIST = 2
AIRPORT_CITY_MAX_DIST = 6
AIRPORT_MAX = 3

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


DIRS_EVEN = ((1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1))   # 偶数行：本行不右移
DIRS_ODD = ((1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1))      # 奇数行：本行右移半格
# 顺时针自 E 起，与 C# Hex.DirectionsFor / web/js/hex.js dirsFor 完全一致


def dirs_for(r: int):
    """odd-r 偏移坐标的 6 邻方向（按行奇偶）。"""
    return DIRS_ODD if (r & 1) else DIRS_EVEN


def to_axial(cell):
    """odd-r 偏移 (q, r) → axial (aq, ar)：立方体运算统一在 axial 空间做。"""
    q, r = cell
    return (q - (r - (r & 1)) // 2, r)


def from_axial(aq: int, ar: int):
    """axial → odd-r 偏移。"""
    return (aq + (ar - (ar & 1)) // 2, ar)


def slope_axes(r: int):
    """三条梯度轴 = 三对对角邻居（dirs 索引 0/3、1/4、2/5）。"""
    d = dirs_for(r)
    return ((d[0], d[3]), (d[1], d[4]), (d[2], d[5]))


def mercator_scale(lat_deg: float) -> float:
    """
    Web Mercator 在纬度 lat 的线性放大倍数 = 1/cos(lat)：墨卡托米 ÷ 该倍数 = 实地米。
    为什么必须显式处理：格网画在 EPSG:3857 里才能和瓦片零漂移对齐，但「对边距 5 km」
    是指挥员嘴里的实地 5 km。51°N 处两者差 60%，不换算就等于把 3.1 km 的格标成 5 km。
    """
    return 1.0 / max(0.05, math.cos(math.radians(clamp(lat_deg, -85.0, 85.0))))



class HexGrid:
    """尖顶六角格几何：格心 ↔ EPSG:3857。"""

    def __init__(self, center_lon: float, center_lat: float, width: int, height: int, cell_km: float,
                 scale: float | None = None):
        self.center_lon = center_lon
        self.center_lat = center_lat
        self.width = width
        self.height = height
        self.cell_km = cell_km
        # scale 可由调用方覆盖：旧数据包没标注墨卡托放大，按赤道口径回验
        self.scale = mercator_scale(center_lat) if scale is None else float(scale)
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
        """格心 EPSG:3857 (x, y)。奇数行整体右移半格 ⇒ 矩形棋盘。"""
        q, r = cell
        return (self.mercator_ox + self.hex_width_m * (q + (r & 1) / 2.0),
                self.mercator_oy - self.row_step_m * r)

    def center_lonlat(self, cell):
        """格心 → (lat, lon)，供 Open-Meteo 批量参数使用。"""
        x, y = self.center(cell)
        return (y_to_lat(y), x_to_lon(x))

    def neighbors(self, q: int, r: int):
        out = []
        for dq, dr in dirs_for(r):
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


# ---- 对边距推荐（创建副本时的一键「用推荐」，与生成同源，避免两套口径） ----

CELL_LADDER_KM = (0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 5.0, 7.5, 10.0, 15.0, 20.0)
PLAN_TARGET_CELLS = 2400   # 一次交战的可视战场：再大前端渲染与结算都拖沓
PLAN_MIN_SIDE = 12         # 短边至少 12 格，否则机动没有纵深
DEM_FLOOR_KM = 1.0         # 本地 DEM 30″≈0.93 km，格距再小也不会更「真实」


def bbox_ground_km(bbox):
    """WGS84 四至 → 实地东西/南北公里（墨卡托放大的逆运算）。"""
    clat = (bbox[1] + bbox[3]) / 2.0
    w = (bbox[2] - bbox[0]) * 111.320 * math.cos(math.radians(clat))
    h = (bbox[3] - bbox[1]) * 110.574
    return max(w, 1e-6), max(h, 1e-6)


def fmt_km(v):
    return "%.0f" % v if v >= 10 else "%.1f" % v


def recommend_cell_km(bbox):
    """返回 (推荐对边距, 理由)。尖顶格行距 = 0.866×对边距 ⇒ 格数 ≈ 面积/(0.866·c²)。"""
    w, h = bbox_ground_km(bbox)
    ideal = math.sqrt(w * h / (0.866 * PLAN_TARGET_CELLS))
    floor_km = max(ideal, DEM_FLOOR_KM)
    cap = min(w, h) / (PLAN_MIN_SIDE - 1.0)   # 保证短边铺得下 PLAN_MIN_SIDE 格
    reasons = ["目标 %d 格左右（实地 %s×%s km）⇒ 理论格距 %.2f km" %
               (PLAN_TARGET_CELLS, fmt_km(w), fmt_km(h), ideal)]
    pick = next((c for c in CELL_LADDER_KM if c >= floor_km), CELL_LADDER_KM[-1])
    if pick > cap:
        smaller = [c for c in CELL_LADDER_KM if c <= cap]
        if smaller:
            pick = smaller[-1]
            reasons.append("为在短边铺够 %d 格，改取更小格距（格数会超过目标）" % PLAN_MIN_SIDE)
        else:
            pick = CELL_LADDER_KM[0]
            reasons.append("范围过小：已取最小格距，建议扩大范围框")
    if ideal < DEM_FLOOR_KM:
        reasons.append("格距不低于高程栅格分辨率（%.1f km），更细只是插值" % DEM_FLOOR_KM)
    reasons.append("对边距按实地计，中心纬度墨卡托放大 %.2f 倍" % mercator_scale((bbox[1] + bbox[3]) / 2.0))
    return pick, reasons


def plan_payload(bbox, cell_km=None):
    w_km, h_km = bbox_ground_km(bbox)
    rec, reasons = recommend_cell_km(bbox)
    clat = (bbox[1] + bbox[3]) / 2.0
    ladder = []
    for c in CELL_LADDER_KM:
        g = grid_from_bbox(bbox, c)
        n = g.width * g.height
        label = ("recommended" if c == rec else
                 "heavy" if n > PLAN_TARGET_CELLS * 2.5 else
                 "coarse" if min(g.width, g.height) < PLAN_MIN_SIDE else "ok")
        ladder.append({"cell_km": c, "width": g.width, "height": g.height, "cells": n, "label": label})
    cur = None
    if cell_km:
        g = grid_from_bbox(bbox, cell_km)
        cur = {"cell_km": cell_km, "width": g.width, "height": g.height,
               "cells": g.width * g.height, "mercator_scale": g.scale}
    return {
        "ok": True,
        "bbox": [bbox[0], bbox[1], bbox[2], bbox[3]],
        "center_lonlat": [(bbox[0] + bbox[2]) / 2.0, clat],
        "ground_km": [round(w_km, 2), round(h_km, 2)],
        "mercator_scale": mercator_scale(clat),
        "target_cells": PLAN_TARGET_CELLS,
        "min_side": PLAN_MIN_SIDE,
        "dem_floor_km": DEM_FLOOR_KM,
        "recommended": {"cell_km": rec, "reasons": reasons},
        "current": cur,
        "ladder": ladder,
    }


def hex_distance(a, b) -> int:
    """odd-r 偏移坐标的六角格距离：转 axial 后取 cube 距离。"""
    ax, ay = to_axial(a)
    bx, by = to_axial(b)
    return max(abs(ax - bx), abs(ay - by), abs(-ax - ay + bx + by))


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
        for dp, dm in slope_axes(r):
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


def lc_flat_terrain(shares):
    """真实土地覆盖 → 平缓地带形（只裁决 woods/rough/clear；water/city/swamp 需河距，留给上层）。"""
    if (shares.get(10, 0.0) >= LC_WOODS_SHARE):
        return "woods"
    if (shares.get(20, 0.0) + shares.get(90, 0.0) + shares.get(70, 0.0)) >= LC_ROUGH_SHARE:
        return "rough"
    return "clear"


def classify_base(grid: HexGrid, elev, slopes, relief, th, seed, lc=None):
    """
    先于水系/道路生成，产出基础 terrain（不含 river/city）与辅助噪声场。
    判据（slope 与 relief 互补：前者是实地格距基线上的区域坡度，后者是邻域高差）：
      陡 = slope ≥ slope_mountain 或 relief ≥ RELIEF_MOUNTAIN_MIN_M
      缓陡 = slope ≥ slope_hills 或 relief ≥ RELIEF_HILLS_MIN_M
      1) 陡  且 高程 ≥ elev_mountain ⇒ mountain
      2) 陡  但 高程不足（陡而低的破碎地）⇒ rough
      3) 缓陡 且 高程 ≥ elev_hills    ⇒ hills
      4) 缓陡 但 高程不足              ⇒ rough
      5) 其余（平缓低地）：有真实土地覆盖 ⇒ 按格内面积占比定 woods/rough/clear；
         没有 ⇒ 森林噪声达阈 + 坡度适中 + 非山顶 ⇒ woods；否则 ⇒ clear
         （swamp / water 需要「距河距离场」，在 build_payload 里二次定夺）
      地势类别（mountain/hills/rough）永不被覆盖层改写——树也长在山上。
    """
    terr = ["clear"] * (grid.width * grid.height)
    aux = {}                      # index → (湿度噪声, 湖泊噪声)
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        e, sl, rl = elev[i], slopes[i], relief[i]
        px, py = grid.center((q, r))
        sx = (px - grid.mercator_ox) / grid.scale / 1000.0    # 相对原点的「实地公里」位置，作为噪声自变量
        sy = (grid.mercator_oy - py) / grid.scale / 1000.0
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
        elif lc is not None:
            terr[i] = lc_flat_terrain(lc[(q, r)]["shares"]) if not broken else \
                ("woods" if lc[(q, r)]["shares"].get(10, 0.0) >= LC_WOODS_SHARE else "rough")
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


def bridges_at_crossings(grid: HexGrid, road, river_set):
    """
    桥位 = 道路 ∩ 河带中「真的把路送过对岸」的那几段。
    判定按连通段长度：段长 ≤ RIVER_RUN_BRIDGE_MAX ⇒ 一次渡河 ⇒ 整段记桥；
    更长的段是沿河/河谷公路（贴着水面走几公里，并没有跨水）⇒ 不记桥。
    不做这一步的话，莱茵-鲁尔这种都会区会把 48 个沿河格全标成桥，渡河不再是作战课题。
    返回 (bridge_cells, {"crossings": 渡河处数, "沿河段": 长段数})。
    """
    cross = road & river_set
    seen, bridges, long_runs, crossings = set(), set(), 0, 0
    for cell in sorted(cross):
        if cell in seen:
            continue
        stack, comp = [cell], []
        while stack:
            cur = stack.pop()
            if cur in seen:
                continue
            seen.add(cur)
            comp.append(cur)
            stack.extend(n for n in grid.neighbors(*cur) if n in cross and n not in seen)
        if len(comp) <= RIVER_RUN_BRIDGE_MAX:
            bridges |= set(comp)
            crossings += 1
        else:
            long_runs += 1
    return bridges, {"crossings": crossings, "bank_runs": long_runs}


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
        if all(hex_distance(cell, p) >= CITY_MIN_SEPARATION for p in picks):
            picks.append(cell)
    if len(picks) < CITY_MIN:                       # 兜底：放宽河岸约束，仅要求道路格
        for cell in sorted(road):
            if len(picks) >= CITY_MIN:
                break
            if cell in bridge_set or terr[grid.index(*cell)] in ("water", "river"):
                continue
            if all(hex_distance(cell, p) >= 2 for p in picks):
                picks.append(cell)
    marks = {}
    for idx, cell in enumerate(picks[:CITY_MAX]):
        marks[cell] = 2 if idx == 0 else 1          # 最优者为首府（较大城镇）
    return marks


# =====================================================================================
# 9b) 地物层：铁路 / 车站 / 隧道 / 电网 / 机场
# =====================================================================================


def build_railway(grid, elev, terr, road, bridge_set, city_marks):
    """
    铁路：一条近东西干线穿过首府（等级高的聚落），格网够大时再补一条支线到次级聚落。
    非桥位渡河代价 = RAIL_RIVER_BLOCK_COST ⇒ 铁路 ∩ 河 只会落在既有桥位上
    （现实里铁路和公路抢同一批渡口/桥头，玩法上也保住「桥 = 道路 ∩ 河」契约不被铁路破坏）。
    返回 (rail_cells, stations:{cell: 聚落等级})。
    """
    emin = min(elev)

    def cost(q, r):
        i = grid.index(q, r)
        t = terr[i]
        c = elev[i] - emin
        if t == "river" and (q, r) not in bridge_set:
            c += RAIL_RIVER_BLOCK_COST
        if t in ("water", "swamp"):
            c += RAIL_WATER_BLOCK_COST
        if t == "mountain":
            c += RAIL_MOUNTAIN_PENALTY
        if (q, r) in road:
            c -= RAIL_ROAD_BONUS
        return max(0.0, c)

    def bend(a, b, c):
        return RAIL_BEND_PENALTY_PER_DEG * grid.turn_angle(a, b, c)

    cities = sorted(city_marks.items(), key=lambda kv: (-kv[1], grid.index(*kv[0])))
    rail = set()
    if cities:
        capital = cities[0][0]
        west = {(0, r) for r in range(grid.height)}
        east = {(grid.width - 1, r) for r in range(grid.height)}
        leg_a = dijkstra(grid, west, {capital}, cost, turn_cost=bend)
        leg_b = dijkstra(grid, {capital}, east, cost, turn_cost=bend)
        if leg_a and leg_b:
            rail = set(leg_a) | set(leg_b)
        else:                                       # 全图被湖/山堵死 ⇒ 退成过首府的行直列（保证有干线）
            rail = {(q, capital[1]) for q in range(grid.width)}
        if grid.width * grid.height >= RAIL_BRANCH_MIN_CELLS and len(cities) > 1:
            branch = dijkstra(grid, set(rail), {cities[1][0]}, cost, turn_cost=bend)
            if branch:
                rail |= set(branch)
    stations = {}
    for cell, grade in cities:
        cand = [(hex_distance(cell, rc), grid.index(*rc), rc) for rc in rail
                if hex_distance(cell, rc) <= RAIL_STATION_MAX_DIST
                and terr[grid.index(*rc)] not in ("river", "water")]
        if cand:
            stations[min(cand)[2]] = grade
    return rail, stations


def build_tunnels(grid, line_cells, terr, slopes, relief, th, real_tunnel_cells):
    """
    隧道 = ①真实隧道格（World_Roads 的 RD_FEATURE=Tunnel，须落在线路上）
          ∪ ②线路在陡区「内部」穿行的格（前后都沿线 ⇒ 是穿山，不是刚上山）。
    返回 (tunnel_cells, {"real": n, "derived": m, "dropped_real": k})。
    """
    real_on_line = {c for c in real_tunnel_cells if c in line_cells
                    and terr[grid.index(*c)] not in ("river", "water")}
    dropped = len(real_tunnel_cells) - len(real_on_line)
    cands = []
    for c in sorted(line_cells):
        if c in real_on_line:
            continue
        i = grid.index(*c)
        if terr[i] not in TUNNEL_TERRAIN:
            continue
        if slopes[i] < th["slope_hills_deg"] or relief[i] < TUNNEL_MIN_RELIEF_M:
            continue
        if sum(1 for n in grid.neighbors(*c) if n in line_cells) < TUNNEL_LINE_NEIGHBORS:
            continue
        cands.append((-slopes[i], i, c))
    cands.sort(key=lambda t: (t[0], t[1]))
    derived = {c for _, _, c in cands[:TUNNEL_DERIVED_MAX]}
    return real_on_line | derived, {
        "real": len(real_on_line), "derived": len(derived), "dropped_real": dropped}


def build_power(grid, elev, terr, slopes, city_marks, river_dist):
    """
    电网：变电站进城镇 → 电厂挑近水的平地（远离城镇但不脱离供水/负荷）→ 线路按
    「把未连通的节点接到最近的已连通节点」逐步成网（电厂侧起手，保证每个电厂都上网）。
    线路和公路不同：跨河是架线塔的正常做法，只罚不禁，否则会沿河谷绕出无意义的长蛇阵。
    返回 (plants, substations, powerline_cells)。
    """
    n_cells = grid.width * grid.height
    if not city_marks:
        return set(), set(), set()
    cities = sorted(city_marks.items(), key=lambda kv: (-kv[1], grid.index(*kv[0])))
    substations = {cell for cell, _ in cities[:SUBSTATION_MAX]}
    city_dist = grid.distance_field(substations)
    emin, emax = min(elev), max(elev)
    span = max(1.0, emax - emin)

    plant_max = PLANT_MAX if n_cells >= RAIL_BRANCH_MIN_CELLS else PLANT_MIN
    cand = []
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        cell = (q, r)
        if terr[i] not in ("clear", "rough") or cell in substations:
            continue
        if river_dist.get(cell, 99) > PLANT_RIVER_MAX_DIST:
            continue
        if slopes[i] > POWER_MAX_SLOPE_DEG or city_dist.get(cell, 99) < PLANT_CITY_MIN_DIST:
            continue
        score = -slopes[i] / POWER_MAX_SLOPE_DEG - 0.4 * city_dist[cell]
        cand.append((-score, i, cell))
    cand.sort(key=lambda t: (t[0], t[1]))
    plants = []
    for _, _, cell in cand:
        if len(plants) >= plant_max:
            break
        if all(hex_distance(cell, p) >= PLANT_SEPARATION for p in plants):
            plants.append(cell)
    plants = set(plants)
    if not plants:                                  # 全图找不到近水平地 ⇒ 贴着变电站摆一处（保住玩法目标）
        near = sorted(substations, key=lambda c: grid.index(*c))[:1]
        for c in near:
            for n in [c] + grid.neighbors(*c):
                if terr[grid.index(*n)] in ("clear", "rough") and n not in substations:
                    plants.add(n)
                    break
            if plants:
                break

    def cost(q, r):
        i = grid.index(q, r)
        t = terr[i]
        c = (elev[i] - emin) + 120.0 * slopes[i] / POWER_MAX_SLOPE_DEG
        if t in ("water", "swamp"):
            c += POWER_WATER_BLOCK_COST
        if t == "mountain":
            c += POWER_MOUNTAIN_PENALTY
        if t == "river":
            c += 0.6 * span                         # 架线塔跨河：可行但不便宜
        return max(0.0, c)

    def bend(a, b, c):
        return POWER_BEND_PENALTY_PER_DEG * grid.turn_angle(a, b, c)

    lines, linked = set(), set()
    nodes = plants | substations
    cap = int(POWERLINE_MAX_FRACTION * n_cells)

    def attach(starts, targets):
        path = dijkstra(grid, starts, targets, cost, turn_cost=bend)
        if not path:
            return False
        lines.update(path)
        linked.update(c for c in path if c in nodes)
        return True

    # ① 先给每个电厂各拉一条到变电站的出线（否则多电厂时只有「顺路」那个上网，另一个成孤岛）
    for p in sorted(plants, key=lambda c: grid.index(*c)):
        if len(lines) >= cap or not substations:
            break
        attach({p}, substations - {p})
        linked.add(p)
    # ② 再把尚未上网的变电站按「最近的已连通节点」逐个挂上，逐步成网
    for s in sorted(substations - linked, key=lambda c: grid.index(*c)):
        if len(lines) >= cap:
            break
        if attach(linked | plants, {s}):
            linked.add(s)
    lines.update(plants)
    lines.update(s for s in substations if s in linked)
    return plants, substations, lines


def place_airports(grid, terr, slopes, road, rail, city_marks, real_airports):
    """
    机场：真实机场点（Natural Earth 10m）优先；战区里没有真实机场要素时，
    在首府近郊找一块不压路的平地派生一处野场（meta 里如实标注 derived）。
    返回 ({cell: 等级 1|2}, "real" | "derived" | "off")。
    """
    marks = dict(real_airports or {})
    if marks:
        ranked = sorted(marks.items(),
                        key=lambda kv: (-kv[1], grid.index(*kv[0])))
        return dict(ranked[:AIRPORT_MAX]), "real"
    if not city_marks:
        return {}, "off"
    capital = sorted(city_marks.items(), key=lambda kv: (-kv[1], grid.index(*kv[0])))[0][0]
    cands = []
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        cell = (q, r)
        d = hex_distance(cell, capital)
        if not AIRPORT_CITY_MIN_DIST <= d <= AIRPORT_CITY_MAX_DIST:
            continue
        if terr[i] not in ("clear", "rough") or slopes[i] > AIRPORT_DERIVED_SLOPE_MAX_DEG:
            continue
        if cell in road or cell in rail:
            continue
        cands.append((d, slopes[i], i, cell))
    if not cands:
        return {}, "off"
    best = min(cands, key=lambda t: (t[0], t[1], t[2]))
    return {best[3]: 1}, "derived"


def build_features(grid, elev, terr, slopes, relief, th, road, bridge_set, city_marks,
                   river_dist, real_tunnel, real_airports, args):
    """
    汇总地物层。features=off 时不派生任何玩法地物（真实矢量落在格上的机场也一并不写，
    保持「关掉这层 = 地图回到只有地形/道路/河/桥」的干净语义）。
    """
    feat = {"railway": set(), "rail_station": {}, "tunnel": set(), "power_plant": set(),
            "substation": set(), "powerline": set(), "airport": {}}
    if args.features == "off":
        return feat, {"railway": "off", "power": "off", "tunnels": "off", "airports": "off"}
    rail, stations = build_railway(grid, elev, terr, road, bridge_set, city_marks)
    tunnels, tun_note = build_tunnels(grid, road | rail, terr, slopes, relief, th,
                                      real_tunnel or set())
    plants, subs, lines = build_power(grid, elev, terr, slopes, city_marks, river_dist)
    airports, src = place_airports(grid, terr, slopes, road, rail, city_marks, real_airports)
    feat.update(railway=rail, rail_station=stations, tunnel=tunnels,
                power_plant=plants, substation=subs, powerline=lines, airport=airports)
    return feat, {"railway": "derived-route", "power": "derived-grid",
                  "tunnels": tun_note, "airports": src}


# =====================================================================================
# 10) 组装 + 自校验
# =====================================================================================


def build_hexes(grid, elev, slopes, terr, road, river_set, bridge_set, city_marks, lc=None,
                feat=None):
    from rasters import WORLDCOVER_TERRAIN          # 只在真的用了覆盖层时才拉可选依赖
    out = []
    for (q, r) in grid.iter_cells():                # r 外层、q 内层
        i = grid.index(q, r)
        e = float(elev[i])
        e_val = int(round(e)) if abs(e - round(e)) < 0.05 else round(e, 1)
        hx = {
            "q": q,
            "r": r,
            "terrain": terr[i],
            "elev": e_val,
            "slope": round(float(slopes[i]), 1),
            "road": (q, r) in road,
            "river": (q, r) in river_set,
            "bridge": (q, r) in bridge_set,
            "city": city_marks.get((q, r), 0),
        }
        if feat is not None:                        # 地物层只写「有」的格：30k 格包省下几十列 false
            cell = (q, r)
            for key in ("railway", "tunnel", "powerline", "power_plant", "substation"):
                if cell in feat[key]:
                    hx[key] = True
            if feat["rail_station"].get(cell):
                hx["rail_station"] = True
            if feat["airport"].get(cell):
                hx["airport"] = int(feat["airport"][cell])
        if lc is not None:                          # 判类证据留在包里，便于复核「为什么这格是林地」
            v = lc[(q, r)]
            hx["lc"] = {"class": v["class"], "share": round(v["share"], 3),
                        "terrain": WORLDCOVER_TERRAIN.get(v["class"])}
        out.append(hx)
    return out


def validate(data: dict, grid: HexGrid, cell_km: float, strict: bool = True):
    """
    契约自校验，返回 [(级别, 检查名, 说明)]，级别 OK / WARN / FAIL：
      · 格数 = width*height、q/r 范围正确、无重复、遍历顺序 r 外 q 内
      · terrain 全部落在白名单
      · |sqrt(3)*radius_m - cell_km*1000| < 1e-6
      · bridge 只出现在 river 格；桥位 ⊆ 道路∩河带，短渡河段全部成桥（长段 = 沿河公路不记桥）
      · city ≥ 1 ⇒ terrain == city；聚落数 ∈ [2,4]
      · 河带单连通且纵向贯通；stats 与 hexes 一致；ox/oy 中心对齐；bbox 覆盖全部格心
      · 地物层：隧道在线上且不在水面、铁路只在桥位渡河、车站是铁路格、变电站在聚落、
        电厂建在平地且都上电网、机场等级合法；地物 7 项计数与 stats 相符
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

    scale = float(meta.get("mercator_scale", 1.0))
    dev = abs(math.sqrt(3.0) * meta["radius_m"] - cell_km * 1000.0 * scale)
    res.append((("OK" if dev < 1e-6 else "FAIL"), "sqrt(3)*radius≈cell×墨卡托放大",
                "%.6f m vs %.6f m（scale=%.6f），差 %.3e"
                % (math.sqrt(3.0) * meta["radius_m"], cell_km * 1000.0 * scale, scale, dev)))
    if "mercator_scale" not in meta:
        res.append(("WARN", "旧数据包未标注 mercator_scale",
                    "按 scale=1（赤道）口径校验；实地格距在中纬会偏大，重新生成可得标注"))
    cg = meta.get("cell_ground_m")
    res.append((("OK" if cg is None or abs(float(cg) - cell_km * 1000.0) < 1e-6 else "FAIL"),
                "对边距=实地口径", "cell_ground_m=%s" % cg))

    layers = meta.get("layers") or {}
    roads_off = ((layers.get("roads") or {}).get("kind") == "off")
    lc_real = ((layers.get("landcover") or {}).get("kind") == "esa-worldcover-v200")
    need_layers = ("elevation", "landcover", "roads", "hydrology", "settlements",
                   "railway", "power", "tunnels", "airports")
    layer_note = json.dumps({k: (v or {}).get("kind") or (v or {}).get("source")
                             for k, v in layers.items()}, ensure_ascii=False) if layers else "无 meta.layers"
    lvl = "OK" if all(k in layers for k in need_layers) else ("WARN" if not strict else "FAIL")
    res.append((lvl, "数据层标注完整", layer_note))

    bridge_cells = [(hx["q"], hx["r"]) for hx in hexes if hx["bridge"]]
    bad_bridge = [c for c in bridge_cells if by_cell[c]["terrain"] != "river" or not by_cell[c]["river"]]
    res.append((("OK" if not bad_bridge else "FAIL"), "bridge 仅在 river 格",
                "越界桥位: %s" % (bad_bridge if bad_bridge else "无")))
    crossings = {(hx["q"], hx["r"]) for hx in hexes if hx["road"] and hx["river"]}
    if roads_off:
        res.append((("OK" if not crossings and not bridge_cells else "FAIL"), "道路关闭时无路无桥",
                    "道路格 %d / 桥 %d" % (sum(1 for hx in hexes if hx["road"]), len(bridge_cells))))
    else:
        # 桥位必须是道路∩河的子集；反过来的「道路穿过河却没桥」只允许是沿河长段（贴水不跨水）
        stray = sorted(set(bridge_cells) - crossings)
        res.append((("OK" if not stray else "FAIL"), "桥位 ⊆ 道路∩河",
                    "%s" % (stray[:6] if stray else "无")))
        segs, seen = [], set()
        for cell in sorted(crossings):
            if cell in seen:
                continue
            stack, comp = [cell], []
            while stack:
                cur = stack.pop()
                if cur in seen:
                    continue
                seen.add(cur)
                comp.append(cur)
                stack.extend(n for n in grid.neighbors(*cur) if n in crossings and n not in seen)
            segs.append(comp)
        short_unbridged = [c for s in segs if len(s) <= RIVER_RUN_BRIDGE_MAX for c in s
                           if c not in set(bridge_cells)]
        res.append((("OK" if not short_unbridged else "FAIL"), "短渡河段全部成桥",
                    "%d 段缺桥 %s" % (len({tuple(sorted(c)) for c in short_unbridged}),
                                      short_unbridged[:6]) if short_unbridged
                    else "%d 处渡河段全有桥（另有 %d 段沿河公路不记桥）"
                         % (sum(1 for s in segs if len(s) <= RIVER_RUN_BRIDGE_MAX),
                            sum(1 for s in segs if len(s) > RIVER_RUN_BRIDGE_MAX))))
    nb = len(set(bridge_cells))
    real_roads = str((layers.get("roads") or {}).get("kind", "")).startswith("natural-earth")
    if roads_off:
        res.append((("OK", "道路关闭", "未生成道路")))
    elif real_roads:
        res.append((("OK" if nb >= BRIDGE_MIN else "WARN"), "渡河点数量（真实路网口径）",
                    "桥格 %d 处（真实高速跨河本来就是多处，不受合成场景 %d–%d 约束）"
                    % (nb, BRIDGE_MIN, BRIDGE_MAX)))
    else:
        res.append((("OK" if BRIDGE_MIN <= len(bridge_cells) <= BRIDGE_MAX else "WARN"),
                    "桥梁总数 2–4", "实际 %d 座 @ %s" % (len(bridge_cells), bridge_cells)))
    res.append((("OK" if all(by_cell[c]["road"] for c in bridge_cells) else "FAIL"),
                "桥在道路上", "%d 处" % len(bridge_cells) if bridge_cells else "无"))

    bad_city = [(hx["q"], hx["r"], hx["terrain"]) for hx in hexes if hx["city"] >= 1 and hx["terrain"] != "city"]
    res.append((("OK" if not bad_city else "FAIL"), "city≥1 ⇒ terrain=city",
                "%s" % (bad_city if bad_city else "无")))
    nc = sum(1 for hx in hexes if hx["city"] >= 1)
    cmax = CITY_ABS_MAX if lc_real else CITY_MAX
    res.append((("OK" if (0 if roads_off and not lc_real else CITY_MIN) <= nc <= cmax else "WARN"),
                "聚落数量 %d–%d" % (CITY_MIN, cmax), "实际 %d（来源 %s）"
                % (nc, (layers.get("settlements") or {}).get("kind", "?"))))

    if lc_real:
        missing = sum(1 for hx in hexes if "lc" not in hx)
        res.append((("OK" if missing == 0 else "FAIL"), "每格带覆盖判据 lc", "缺 %d 格" % missing))
        hit = sum(1 for hx in hexes
                  if hx.get("lc", {}).get("class") == 50 and hx["lc"].get("share", 0) >= LC_CITY_SHARE)
        res.append((("OK" if nc >= CITY_MIN else "WARN"), "聚落来自真实建筑区",
                    "达阈 %d 格 ⇒ 按游戏性上限取 %d 格（聚落是目标点，不能把城区铺满）" % (hit, nc)))

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
    if roads_off:
        res.append((("OK" if not roads else "FAIL"), "道路关闭", "0 道路格"))
    else:
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
        conn = [c for c in comps if (c & set(west)) and (c & set(east))]
        # 真实路网的 rural 支线本就不碰图边 ⇒ 只要求「存在一条贯通东西的主干」，
        # 不要求每个分量都贯通（那是派生走廊时代的强约束，接真实数据后是误报）。
        res.append((("OK" if roads and conn else "WARN"), "存在贯通东西的公路主干",
                    "道路 %d 格 / 分量 %d 个（贯通 %d 个，尺寸 %s）/ 西端 %d / 东端 %d"
                    % (len(roads), len(comps), len(conn), sorted(len(c) for c in comps),
                       len(west), len(east))))
    if strict:
        res.append((("OK" if str(meta.get("source", "")).startswith(
                        (SOURCE_REAL, SOURCE_SYNTH, SOURCE_LOCAL_DEM)) else "WARN"),
                    "meta.source 标注", str(meta.get("source"))))
    res.extend(_validate_features(data, grid, meta, hexes, by_cell, layers))
    return res


FEATURE_BOOL_KEYS = ("railway", "tunnel", "powerline", "power_plant", "substation", "rail_station")


def _validate_features(data, grid, meta, hexes, by_cell, layers):
    """
    地物层自校验：契约（隧道/车站/线路必须挂在线上、铁路只能走既有桥位渡河、电厂必上网）
    为 FAIL，数量区间为 WARN。老数据包没有这些字段 ⇒ 视为未生成，只查「不该有值」。
    """
    def cells_of(key):
        return {(hx["q"], hx["r"]) for hx in hexes if hx.get(key)}
    rail, tun = cells_of("railway"), cells_of("tunnel")
    line, plant = cells_of("powerline"), cells_of("power_plant")
    subs, station = cells_of("substation"), cells_of("rail_station")
    apt = {(hx["q"], hx["r"]): int(hx.get("airport", 0) or 0) for hx in hexes if hx.get("airport")}
    road = {(hx["q"], hx["r"]) for hx in hexes if hx["road"]}
    river = {(hx["q"], hx["r"]) for hx in hexes if hx["river"]}
    bridge = {(hx["q"], hx["r"]) for hx in hexes if hx["bridge"]}
    city = {(hx["q"], hx["r"]) for hx in hexes if hx["city"] >= 1}
    feats_off = (layers.get("railway") or {}).get("kind") == "off"
    res = []

    if "railway_cells" not in (meta.get("stats") or {}):
        res.append((("WARN" if not (rail or tun or line or plant or subs or station or apt) else "FAIL"),
                    "旧数据包无地物层",
                    "重新生成可得到铁路/电网/机场/隧道；本次未见字段"
                    if not (rail or tun or line or plant or subs or station or apt)
                    else "无 stats 计数却有地物格 %d" % len(rail | tun | line | plant | subs | station)))
        return res

    if feats_off:
        res.append((("OK" if not (rail or tun or line or plant or subs or station or apt) else "FAIL"),
                    "地物层关闭时无残留",
                    "铁路 %d / 隧道 %d / 线路 %d / 电厂 %d / 变电站 %d / 车站 %d / 机场 %d"
                    % (len(rail), len(tun), len(line), len(plant), len(subs), len(station), len(apt))))
        return res

    bad_tun = [c for c in sorted(tun)
               if (c not in road and c not in rail) or by_cell[c]["terrain"] in ("river", "water")]
    res.append((("OK" if not bad_tun else "FAIL"), "隧道在线上且不在水面",
                "%s" % (bad_tun[:6] if bad_tun else "无，共 %d 处" % len(tun))))
    rail_river = sorted((rail & river) - bridge)
    res.append((("OK" if not rail_river else "FAIL"), "铁路只在桥位渡河",
                "越界 %s" % (rail_river[:6] if rail_river else "无（铁路∩河 ⊆ 桥位）")))
    bad_st = sorted(station - rail)
    res.append((("OK" if not bad_st else "FAIL"), "车站是铁路格", "%s" % (bad_st if bad_st else "无")))
    res.append((("OK" if len(station) <= max(1, len(city)) else "WARN"), "车站数 ≤ 聚落数",
                "车站 %d / 聚落 %d" % (len(station), len(city))))

    comps, unvisited = [], set(rail)
    while unvisited:
        stack, comp = [sorted(unvisited)[0]], set()
        while stack:
            cur = stack.pop()
            if cur in comp:
                continue
            comp.add(cur)
            stack.extend(n for n in grid.neighbors(*cur) if n in rail and n not in comp)
        unvisited -= comp
        comps.append(comp)
    w = meta["width"]
    trunk = any(c and any(x[0] == 0 for x in c) and any(x[0] == w - 1 for x in c) for c in comps)
    res.append((("OK" if trunk else "WARN"), "铁路干线通东西边缘",
                "%d 格 / 分量 %d 个 / 贯通 %s" % (len(rail), len(comps), "是" if trunk else "否")))

    bad_plant = [c for c in sorted(plant) if by_cell[c]["terrain"] not in ("clear", "rough")]
    res.append((("OK" if not bad_plant else "FAIL"), "电厂建在可通行平地",
                "%s" % (bad_plant if bad_plant else "共 %d 处" % len(plant))))
    res.append((("OK" if PLANT_MIN <= len(plant) <= PLANT_MAX else "WARN"), "电厂数量 %d–%d"
                % (PLANT_MIN, PLANT_MAX), "实际 %d" % len(plant)))
    res.append((("OK" if len(subs) <= SUBSTATION_MAX else "WARN"), "变电站数量 ≤ %d" % SUBSTATION_MAX,
                "实际 %d（聚落 %d）" % (len(subs), len(city))))
    bad_sub = sorted(subs - city)
    res.append((("OK" if not bad_sub else "FAIL"), "变电站落在聚落", "%s" % (bad_sub if bad_sub else "无")))

    def reach(src):
        seen, stack = set(), list(src)
        while stack:
            cur = stack.pop()
            if cur in seen:
                continue
            seen.add(cur)
            stack.extend(n for n in grid.neighbors(*cur) if n in line and n not in seen)
        return seen
    orphan = [c for c in sorted(plant) if not (reach({c}) & subs)]
    res.append((("OK" if (not plant or not subs or not orphan) else "FAIL"), "每个电厂都上了电网",
                "孤岛电厂 %s" % (orphan if orphan else "无")))
    res.append((("OK" if len(line) <= int(POWERLINE_MAX_FRACTION * w * meta["height"]) + len(subs) + len(plant)
                else "WARN"), "线路格占比受控", "%d 格（上限约 %d）"
                % (len(line), int(POWERLINE_MAX_FRACTION * w * meta["height"]))))

    bad_apt = [(c, g) for c, g in sorted(apt.items())
               if g not in (1, 2) or by_cell[c]["terrain"] in ("water", "river", "city")]
    res.append((("OK" if not bad_apt else "FAIL"), "机场等级合法且不在水面/城镇",
                "%s" % (bad_apt if bad_apt else "共 %d 处" % len(apt))))
    res.append((("OK" if len(apt) <= AIRPORT_MAX else "WARN"), "机场数量 ≤ %d" % AIRPORT_MAX,
                "实际 %d（来源 %s）" % (len(apt), (layers.get("airports") or {}).get("kind", "?"))))

    st = meta.get("stats", {})
    want = {"railway_cells": rail, "tunnel_cells": tun, "powerline_cells": line,
            "power_plant_cells": plant, "substation_cells": subs, "rail_station_cells": station,
            "airport_cells": {c for c in apt}}
    drift = {k: (st.get(k), len(v)) for k, v in want.items() if st.get(k) != len(v)}
    res.append((("OK" if not drift else "FAIL"), "地物 stats 与 hexes 一致",
                "%s" % (json.dumps(drift, ensure_ascii=False) if drift else "7 项计数相符")))

    type_ok = all(isinstance(hx.get(k, False), bool) for hx in hexes for k in FEATURE_BOOL_KEYS) and \
        all(isinstance(v, int) and v in (1, 2) for v in apt.values())
    res.append((("OK" if type_ok else "FAIL"), "地物字段类型有效", "bool×6 + airport∈{1,2}"))
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
    """
    返回 (elevations, source, note)。优先级：显式缓存 → 本地真实栅格 → 在线 → 合成。
    任何一环失败都降级，绝不抛异常打断生成（无网/无 GDAL 也要能开局）。
    """
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

    if args.dem in ("local", "auto") and args.dem_tif:
        try:
            import rasters
            vals, info = rasters.dem_by_hex(args.dem_tif, grid)
            elev = [vals[c] for c in grid.iter_cells()]
            src = "%s:%s" % (SOURCE_LOCAL_DEM, Path(args.dem_tif).name)
            return elev, src, ("本地栅格面均值取样 %s（分辨率 %.8f°）"
                               % (info["samples"], (info["res_deg"][0] or 0)))
        except Exception as exc:
            print("[dem] 本地栅格取样失败（%s）⇒ 继续下一档" % exc, file=sys.stderr)
            if args.dem == "local":
                return synthetic_dem(grid, args.seed), SOURCE_SYNTH, "--dem local 失败 ⇒ 合成降级"

    if args.dem == "synthetic" or args.offline:
        return synthetic_dem(grid, args.seed), SOURCE_SYNTH, "forced synthetic DEM"
    if args.dem == "open-meteo":
        pass
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


def get_landcover(grid: HexGrid, args):
    """
    返回 (per-cell 覆盖 dict 或 None, info dict 或 None)。
    auto：本地 GDAL 可用 ⇒ 拉真实 WorldCover；失败/离线 ⇒ None（由调用方用噪声代理）。
    """
    if args.landuse == "off":
        return None, {"kind": "off", "note": "未启用土地覆盖层"}
    if args.landuse == "proxy":
        return None, {"kind": "noise-proxy", "note": "无土地覆盖数据，用确定性噪声代理"}
    try:
        import rasters
        return rasters.landcover_by_hex(grid, tile_deg=args.lc_tile_deg)
    except Exception as exc:
        print("[landuse] 真实土地覆盖取样失败（%s）⇒ 退回噪声代理" % exc, file=sys.stderr)
        return None, {"kind": "noise-proxy", "note": "真实取样失败：%s" % str(exc)[:160]}


def get_water(grid: HexGrid, args):
    """
    真实水系矢量（Natural Earth 10m gpkg）→ (river_cells, lake_share, info) 或 (None, None, info)。
    auto：本地 gpkg 存在且 geopandas 可用 ⇒ 叠加真实河湖；缺文件/缺库/失败 ⇒ None（由调用方沿用
    DEM 谷底河带 + WorldCover 水体，玩法不受影响）。绝不抛异常打断生成。
    """
    if args.water == "off":
        return None, None, {"kind": "off", "note": "未启用真实水系矢量层"}
    gpkg = args.water_gpkg or str(resolve_repo_dir() / "data" / "water" / "global_water.gpkg")
    if not Path(gpkg).is_file():
        return None, None, {"kind": "unavailable",
                            "note": "无水系 gpkg（%s），水文沿用 DEM 派生" % Path(gpkg).name}
    try:
        import rasters
        river_cells, lake_share, info = rasters.water_by_hex(gpkg, grid)
        if not river_cells and not lake_share:
            return None, None, {"kind": "natural-earth-10m-water", "gpkg": info.get("gpkg"),
                                "note": "该范围内 Natural Earth 无河湖要素，水文沿用 DEM 派生"}
        return river_cells, lake_share, info
    except Exception as exc:
        print("[water] 真实水系叠加失败（%s）⇒ 沿用 DEM 派生水文" % exc, file=sys.stderr)
        return None, None, {"kind": "unavailable", "note": "真实水系叠加失败：%s" % str(exc)[:160]}



ROADS_GDB_HINT = ('7zr x data/gis/World_Roads.lpk "v10/roads.gdb/*" -odata/gis/roads_pkg -y')


def get_road_layer(grid: HexGrid, args):
    """
    真实路网矢量（Natural Earth World_Roads，解包后的 ESri File Geodatabase）
      → (road_cells, tunnel_cells, info) 或 (None, None, info)。
    auto：本地 FGDB 存在且 pyogrio 可用 ⇒ 叠加真实干线公路（同时带来真实隧道/桥位计数）；
    缺文件/缺库/该范围无要素 ⇒ None（道路退回派生走廊，玩法不受影响）。绝不抛异常打断生成。
    """
    if args.network == "off":
        return None, None, {"kind": "off", "note": "未启用真实路网矢量层"}
    gdb = args.roads_gdb or str(resolve_repo_dir() / "data" / "gis" / "roads_pkg" / "v10" / "roads.gdb")
    if not Path(gdb).exists():
        return None, None, {"kind": "unavailable",
                            "note": "无路网 FGDB（%s）⇒ 道路退回派生走廊；先解包：%s"
                                    % (Path(gdb).name, ROADS_GDB_HINT)}
    try:
        import rasters
        road, tun, br, info = rasters.roads_by_hex(gdb, grid)
        if not road:
            return None, None, {"kind": info.get("kind", "world-roads"),
                                "note": "该范围内 World_Roads 无干线要素 ⇒ 道路退回派生走廊"}
        return road, tun, dict(info, real_bridge_cells=len(br))
    except Exception as exc:
        print("[network] 真实路网叠加失败（%s）⇒ 道路退回派生走廊" % exc, file=sys.stderr)
        return None, None, {"kind": "unavailable", "note": "真实路网叠加失败：%s" % str(exc)[:160]}


def get_airport_layer(grid: HexGrid, args):
    """真实机场点（Natural Earth 10m airports）→ ({cell: 等级}, info) 或 (None, info)；失败不打断生成。"""
    if args.network == "off":
        return None, {"kind": "off", "note": "未启用真实机场矢量层"}
    shp = args.airports_shp or str(resolve_repo_dir() / "data" / "airports" / "ne_10m_airports.shp")
    if not Path(shp).is_file():
        return None, {"kind": "unavailable",
                      "note": "无机场矢量（%s）⇒ 机场按地形派生或不出" % Path(shp).name}
    try:
        import rasters
        marks, info = rasters.airports_by_hex(shp, grid)
        if not marks:
            return None, {"kind": info.get("kind", "natural-earth-10m-airports"),
                          "note": "该范围内 Natural Earth 无机场要素 ⇒ 机场改按地形派生"}
        return marks, info
    except Exception as exc:
        print("[airport] 真实机场叠加失败（%s）⇒ 机场改按地形派生" % exc, file=sys.stderr)
        return None, {"kind": "unavailable", "note": "真实机场叠加失败：%s" % str(exc)[:160]}


def city_marks_from_landcover(grid, lc, terr):
    """
    真实建筑区 → 聚落：占比达阈的格按占比从高到低取，受「聚落上限」截断
    （现实城区会连绵几十格，游戏里聚落是目标点，必须留野地）。
    """
    cap = max(CITY_MIN, min(CITY_ABS_MAX, int(round(LC_CITY_MAX_FRACTION * grid.width * grid.height))))
    cands = []
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        if terr[i] in ("mountain", "hills", "river", "water", "swamp"):
            continue
        share = lc[(q, r)]["shares"].get(50, 0.0)
        if share < LC_CITY_SHARE:
            continue
        cands.append((-share, i, (q, r)))
    cands.sort()
    marks = {}
    for neg, _, cell in cands:
        if len(marks) >= cap:
            break
        marks[cell] = 2 if -neg >= LC_CITY_LEVEL2_SHARE else 1
    return marks


def apply_landcover_water(grid, lc, terr, river_set):
    """
    真实水体/湿地 → water/swamp。规则：河带格保持 river（河是玩法主线），
    其余按占比判定；water 总数受上限截断，防异常数据把地图变成湖。
    """
    n_swamp = n_water = 0
    cap = max(MAX_LAKE_CELLS, int(0.08 * grid.width * grid.height))
    wat = sorted((( -lc[(q, r)]["shares"].get(60, 0.0), grid.index(q, r), (q, r))
                  for (q, r) in grid.iter_cells()
                  if (q, r) not in river_set and terr[grid.index(q, r)] not in ("mountain", "hills")
                  and lc[(q, r)]["shares"].get(60, 0.0) >= LC_WATER_SHARE))
    for _, i, cell in wat[:cap]:
        terr[i] = "water"
        n_water += 1
    for (q, r) in grid.iter_cells():
        i = grid.index(q, r)
        if (q, r) in river_set or terr[i] in ("water", "mountain", "hills", "city"):
            continue
        sh = lc[(q, r)]["shares"]
        if (sh.get(80, 0.0) + sh.get(110, 0.0)) >= LC_SWAMP_SHARE:
            terr[i] = "swamp"
            n_swamp += 1
    return n_water, n_swamp


def build_payload(args):
    grid = (grid_from_bbox(args.parsed_bbox, args.cell_km) if args.parsed_bbox
            else HexGrid(args.lon, args.lat, args.width, args.height, args.cell_km))
    args.width, args.height = grid.width, grid.height
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

    lc, lc_info = get_landcover(grid, args)
    if lc is not None:
        dom = {}
        for v in lc.values():
            dom[v["class"]] = dom.get(v["class"], 0) + 1
        print("[landuse] 真实土地覆盖：%d 格，众数类别 %s"
              % (len(lc), json.dumps({str(k): dom[k] for k in sorted(dom)}, ensure_ascii=False)))
    else:
        print("[landuse] %s" % (lc_info or {}).get("note", "噪声代理"))

    terr, aux = classify_base(grid, elev, slopes, relief, th, args.seed, lc=lc)

    # DEM 谷底脊线：保证河带纵向贯通 + 单连通锚点（玩法主线，永远保留）
    band = extract_river_band(grid, elev)
    river_set = set(band)

    # 真实水系矢量（Natural Earth 10m）：真实河线并入河网，真实湖面按占比判 water。
    # 必须在 build_roads 之前完成，桥位 = 道路 ∩ 河网 才与自校验一致。
    real_river, lake_share, water_info = get_water(grid, args)
    n_real_river = n_lake = 0
    if real_river:
        added = set(real_river) - river_set
        river_set |= added
        n_real_river = len(added)
    for (q, r) in river_set:
        terr[grid.index(q, r)] = "river"
    if lake_share:
        lake_cap = max(MAX_LAKE_CELLS, int(REAL_WATER_MAX_FRACTION * grid.width * grid.height))
        lakes = sorted((( -sh, grid.index(*c), c) for c, sh in lake_share.items()
                        if sh >= LAKE_SHARE_MIN and c not in river_set),
                       key=lambda t: (t[0], t[1]))
        for _, i, _cell in lakes[:lake_cap]:
            terr[i] = "water"
            n_lake += 1
    if real_river or lake_share:
        print("[water] 真实水系叠加：Natural Earth 河格 +%d（脊线外）、湖面 %d 格 ⇒ water（%s）"
              % (n_real_river, n_lake, ", ".join((water_info or {}).get("layers_used", []))))
    else:
        print("[water] %s" % (water_info or {}).get("note", "沿用 DEM 派生水文"))

    river_dist = grid.distance_field(river_set)

    if lc is not None:
        apply_landcover_water(grid, lc, terr, river_set)
    else:
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

    # ── 道路：派生走廊（保证渡河点）∪ 真实路网矢量（能真则真）──
    # 桥位一律按「道路 ∩ 河带的渡河段」重算：真实高速过河处自然落进桥位集合，
    # 而派生走廊保证即使战区没有真实河桥也有渡河点。
    real_road, real_tunnel, net_info = get_road_layer(grid, args)
    real_airports, apt_info = get_airport_layer(grid, args)

    if args.roads == "off":
        road = set()
    else:
        # 传入合并后的完整河网（脊线 + 真实河），桥位 = 道路 ∩ 河网，与自校验同源
        road, _ = build_roads(grid, elev, terr, sorted(river_set), args.seed)
    if real_road:
        road |= real_road
    bridge_set, cross_note = bridges_at_crossings(grid, road, river_set)
    n_real_road = len(real_road) if real_road else 0
    if lc is not None:
        city_marks = city_marks_from_landcover(grid, lc, terr)
    elif args.roads == "off" and not road:
        city_marks = {}
    else:
        city_marks = place_cities(grid, terr, road, river_dist, bridge_set, args.seed)
    for (q, r) in city_marks:
        terr[grid.index(q, r)] = "city"

    feat, feat_note = build_features(grid, elev, terr, slopes, relief, th, road, bridge_set,
                                     city_marks, river_dist, real_tunnel, real_airports, args)
    if feat_note.get("airports") != "off":
        print("[feature] 地物层：铁路 %d 格 / 车站 %d / 隧道 %d（真实 %d）/ 电厂 %d / 变电站 %d / 线路 %d / 机场 %d（%s）"
              % (len(feat["railway"]), len(feat["rail_station"]), len(feat["tunnel"]),
                 feat_note["tunnels"].get("real", 0), len(feat["power_plant"]),
                 len(feat["substation"]), len(feat["powerline"]), len(feat["airport"]),
                 feat_note["airports"]))

    hexes = build_hexes(grid, elev, slopes, terr, road, river_set, bridge_set, city_marks,
                        lc, feat)

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
        "center_lonlat": [grid.center_lon, grid.center_lat],
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
            "real_river_cells": n_real_river,
            "real_lake_cells": n_lake,
            "road_cells": len(road),
            "real_road_cells": n_real_road,
            "bridge_count": len(bridge_set),
            "crossing_count": cross_note["crossings"],
            "bank_road_runs": cross_note["bank_runs"],
            "city_cells": len(city_marks),
            "railway_cells": len(feat["railway"]),
            "rail_station_cells": len(feat["rail_station"]),
            "tunnel_cells": len(feat["tunnel"]),
            "powerline_cells": len(feat["powerline"]),
            "power_plant_cells": len(feat["power_plant"]),
            "substation_cells": len(feat["substation"]),
            "airport_cells": len(feat["airport"]),
        },
        "projection": "EPSG:3857 (Web Mercator); pointy-top odd-r offset hexes (brick board), cx=ox+W*(q+(r&1)/2), cy=oy-1.5*radius*r",
        "hex_order": "r outer loop, q inner loop; index = r*width + q",
        "seed": args.seed,
        "cell_row_spacing_m": grid.row_step_m,
        "dem_points": len(e_all),
        "effective_thresholds": th,
        "bridge_hexes": [{"q": q, "r": r} for (q, r) in sorted(bridge_set, key=lambda c: (c[1], c[0]))],
        # ── 本次生成用了哪些数据（可核对，不粉饰）──
        "mercator_scale": grid.scale,
        "cell_ground_m": grid.cell_ground_m,
        "ground_km_east_west": round((grid.hex_width_m * (grid.width - 1)) / grid.scale / 1000.0, 1),
        "ground_km_north_south": round((grid.row_step_m * (grid.height - 1)) / grid.scale / 1000.0, 1),
        "layers": {
            "elevation": {"source": source, "note": note},
            "landcover": lc_info or {"kind": "noise-proxy"},
            "roads": _roads_layer(args, net_info, cross_note),
            "hydrology": _hydrology_layer(real_river, lake_share, lc, water_info),
            "settlements": {"kind": "worldcover-builtup" if lc is not None else
                            ("derived-junctions" if args.roads != "off" or road else "off")},
            "railway": {"kind": feat_note["railway"],
                        "note": "无公开真实铁路矢量 ⇒ 按首府/次级聚落 + 河谷低地 + 既有公路走廊派生；"
                                "渡河只走既有桥位" if feat_note["railway"] == "derived-route"
                                else "未生成铁路"},
            "power": {"kind": feat_note["power"],
                      "note": "无公开真实电力设施矢量 ⇒ 变电站进城镇、电厂挑近水平地、"
                              "线路按最近节点逐步成网" if feat_note["power"] == "derived-grid"
                              else "未生成电网"},
            "tunnels": {"kind": ("natural-earth-road-tunnels+derived-relief"
                                  if feat_note["tunnels"].get("real")
                                  else "derived-relief")
                                  if feat_note["tunnels"] != "off" else "off",
                        "note": feat_note["tunnels"]},
            "airports": dict(apt_info or {"kind": "off"},
                             derived=bool(feat_note["airports"] == "derived")),
        },
    }
    if real_river or lake_share:
        meta["water"] = water_info       # 真实水系溯源：来源、图层、缓冲、河/湖格数
    if net_info and net_info.get("kind") == "natural-earth-world-roads":
        meta["road_network"] = net_info  # 真实路网溯源：库路径、RANK、要素数、命名干线
    meta["nodes"] = [
        {"q": c[0], "r": c[1], "kind": "power_plant"} for c in sorted(feat["power_plant"])
    ] + [
        {"q": c[0], "r": c[1], "kind": "substation"} for c in sorted(feat["substation"])
    ] + [
        {"q": c[0], "r": c[1], "kind": "rail_station"} for c in sorted(feat["rail_station"])
    ] + [
        {"q": c[0], "r": c[1], "kind": "airport", "grade": g}
        for c, g in sorted(feat["airport"].items())
    ]
    return grid, {"meta": meta, "hexes": hexes}


def _roads_layer(args, net_info, cross_note=None):
    """道路层来源：真实路网矢量优先，派生走廊兜底保证渡河点；两者都不开 = off。"""
    real = net_info or {}
    is_real = real.get("kind") == "natural-earth-world-roads"
    if args.roads == "off" and not is_real:
        return {"kind": "off", "note": "未生成道路"}
    kinds = ([real.get("kind")] if is_real else []) + (
        ["derived-corridors"] if args.roads != "off" else [])
    note = []
    if is_real:
        note.append("真实干线公路 %d 格（%d/%d 条要素，RANK %s）"
                    % (real.get("road_cells", 0), real.get("features_used", 0),
                       real.get("features_total", 0), real.get("ranks")))
        if real.get("named_routes"):
            note.append("命名干线：%s" % ", ".join(real["named_routes"][:8]))
    if args.roads != "off":
        note.append("派生走廊补齐渡河点")
    if cross_note:
        note.append("渡河点 %d 处（另有 %d 段沿河公路贴水不跨水，不记桥）"
                    % (cross_note["crossings"], cross_note["bank_runs"]))
    return {"kind": "+".join(kinds), "note": "；".join(note) or real.get("note", "")}



def _hydrology_layer(real_river, lake_share, lc, water_info):
    """诚实标注水文来源：真实矢量优先，DEM 脊线保贯通，WorldCover 补湖沼。"""
    if real_river or lake_share:
        parts = []
        if real_river:
            parts.append("natural-earth-rivers")
        if lake_share:
            parts.append("natural-earth-lakes")
        parts.append("dem-spine")                    # 纵向贯通的谷底脊线始终保留
        if lc is not None:
            parts.append("worldcover-water")
        used = ", ".join((water_info or {}).get("layers_used", []))
        return {"kind": "+".join(parts),
                "note": "河湖来自 Natural Earth 10m 真实矢量（图层 %s）；另保留 DEM 谷底脊线保证河带纵向贯通与渡河作战主线" % used}
    return {"kind": "derived-river-band" + ("+worldcover-water" if lc is not None else ""),
            "note": "无真实水系矢量叠加：河带由 DEM 谷底最短路派生；湖泊/湿地在有真实覆盖时按占比判定。%s"
                    % ((water_info or {}).get("note", ""))}


def resolve_repo_dir() -> Path:
    """仓库根（含 data/ 或 hexfront/ 的祖先目录）；不依赖 CWD。"""
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "data").is_dir() or (parent / "hexfront").is_dir():
            return parent
    return here.parent.parent


def capabilities_payload(dem_tif=None):
    """服务端探测：栅格依赖是否可用、本地 DEM 在不在、读写范围在哪。"""
    root = resolve_repo_dir()
    tif = Path(dem_tif) if dem_tif else root / "data" / "dem" / "global_dem_30s.tif"
    try:
        import rasters
        caps = dict(rasters.capabilities())
    except Exception as exc:
        rasters = None
        caps = {"rasterio": False, "reason": "rasters 模块导入失败：%s" % str(exc)[:160]}
    caps.update({
        "python": sys.version.split()[0],
        "repo_dir": str(root),
        "dem_tif": str(tif),
        "dem_present": tif.is_file(),
        "gis_dir": str(root / "data" / "gis"),
    })
    if rasters is not None and tif.is_file():
        try:
            caps["dem_info"] = rasters.raster_info(str(tif))
        except Exception as exc:
            caps["dem_info"] = {"error": str(exc)[:160]}
    # 真实水系矢量（Natural Earth gpkg）可用性：需要 geopandas/shapely + 文件
    gpkg = root / "data" / "water" / "global_water.gpkg"
    try:
        import geopandas  # noqa: F401
        import shapely    # noqa: F401
        vec_ok = True
        vec_reason = None
    except Exception as exc:
        vec_ok = False
        vec_reason = "geopandas/shapely 未安装：%s" % str(exc)[:120]
    caps["water_gpkg"] = str(gpkg)
    caps["water_present"] = gpkg.is_file()
    caps["water_vector_ok"] = bool(vec_ok and gpkg.is_file())
    caps["water_reason"] = vec_reason or (None if gpkg.is_file() else "水系 gpkg 不存在")
    # 真实交通矢量：路网 FGDB（从 World_Roads.lpk 解包）+ 机场 shapefile
    gdb = root / "data" / "gis" / "roads_pkg" / "v10" / "roads.gdb"
    apt = root / "data" / "airports" / "ne_10m_airports.shp"
    caps["roads_gdb"] = str(gdb)
    caps["roads_gdb_present"] = gdb.is_dir()
    caps["airports_shp"] = str(apt)
    caps["airports_present"] = apt.is_file()
    caps["vector_ok"] = bool(vec_ok)
    caps["vector_reason"] = vec_reason
    return caps


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
        description="生成兵棋引擎的 GIS 六角格地形数据包："
                    "本地真实 DEM + ESA WorldCover 土地覆盖；任一环节失败自动降级。",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
        epilog="示例: python hexgen.py --bbox 6.0 50.9 7.9 51.9 --cell-km 2.5 "
               "--out ../../data/gis/rhine_2p5km.json",
    )
    p.add_argument("--lon", type=float, default=DEFAULT_LON, help="格网中心经度（WGS84）")
    p.add_argument("--lat", type=float, default=DEFAULT_LAT, help="格网中心纬度（WGS84）")
    p.add_argument("--bbox", type=float, nargs=4, metavar=("西", "南", "东", "北"), default=None,
                   help="按范围框生成（格心范围）：给了它就由对边距反推行列数")
    p.add_argument("--width", type=int, default=DEFAULT_WIDTH, help="列数（q ∈ [0,width-1]）")
    p.add_argument("--height", type=int, default=DEFAULT_HEIGHT, help="行数（r ∈ [0,height-1]）")
    p.add_argument("--cell-km", dest="cell_km", type=float, default=DEFAULT_CELL_KM,
                   help="六角格对边距（实地 km）；墨卡托侧按 1/cos(中心纬度) 放大")
    p.add_argument("--dem", choices=("auto", "local", "open-meteo", "synthetic"), default="auto",
                   help="高程数据源档位；auto = 本地栅格 → 在线 → 合成")
    p.add_argument("--dem-tif", dest="dem_tif", default=None,
                   help="本地 DEM GeoTIFF（默认 <repo>/data/dem/global_dem_30s.tif）")
    p.add_argument("--landuse", choices=("auto", "proxy", "off"), default="auto",
                   help="土地覆盖层：auto=真实 WorldCover，proxy=噪声代理，off=不参与判类")
    p.add_argument("--lc-tile-deg", dest="lc_tile_deg", type=int, default=3,
                   help="WorldCover 瓦片边长（度），官方发布为 3")
    p.add_argument("--water", choices=("auto", "off"), default="auto",
                   help="真实水系矢量层（Natural Earth 10m gpkg）：auto=有则叠加真实河湖，off=只用 DEM 派生水文")
    p.add_argument("--water-gpkg", dest="water_gpkg", default=None,
                   help="水系 GeoPackage（默认 <repo>/data/water/global_water.gpkg）")
    p.add_argument("--roads", choices=("on", "off"), default="on",
                   help="道路层（派生走廊，负责补齐渡河点）；off 则无派生走廊")
    p.add_argument("--network", choices=("auto", "off"), default="auto",
                   help="真实交通矢量层（World_Roads 路网 + NE 机场）：auto=有则用真实道路/隧道/机场")
    p.add_argument("--roads-gdb", dest="roads_gdb", default=None,
                   help="真实路网 ESri File Geodatabase（默认 <repo>/data/gis/roads_pkg/v10/roads.gdb）")
    p.add_argument("--airports-shp", dest="airports_shp", default=None,
                   help="真实机场矢量（默认 <repo>/data/airports/ne_10m_airports.shp）")
    p.add_argument("--features", choices=("on", "off"), default="on",
                   help="地物玩法层（铁路/车站/电网/电厂/变电站/隧道/派生机场）；off 只留地形+道路+水文")
    p.add_argument("--out", default=None, help="输出 JSON（默认 <repo>/data/gis/rhine_5km.json）")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED, help="合成 DEM / 噪声场种子（确定性）")
    p.add_argument("--batch", type=int, default=API_BATCH_SIZE, help="高程 API 每批点数（1..100）")
    p.add_argument("--offline", action="store_true", help="不联网：本地 DEM 仍可用，覆盖层转代理")
    p.add_argument("--no-requests", dest="no_requests", action="store_true",
                   help="只用 urllib（默认允许优先尝试已安装的 requests，urllib 始终兜底）")
    p.add_argument("--save-dem", default=None, metavar="PATH", help="把抓到的真实高程缓存到 JSON，便于离线复现")
    p.add_argument("--load-dem", default=None, metavar="PATH", help="优先从缓存读取高程（跳过网络）")
    p.add_argument("--only-validate", dest="only_validate", action="store_true",
                   help="只校验 --out 已存在的数据包，不生成")
    p.add_argument("--plan", action="store_true",
                   help="只输出格网方案（推荐对边距 + 各档行列数），不生成：##PLAN## {json}")
    p.add_argument("--capabilities", dest="capabilities", action="store_true",
                   help="输出栅格依赖与本地 DEM 可用性：##CAP## {json}")
    p.add_argument("--print-meta", dest="print_meta", action="store_true",
                   help="结束时输出一行 ##META## {json}，供服务端解析（作业进度用）")
    p.add_argument("--allow-invalid", dest="allow_invalid", action="store_true",
                   help="即使自校验 FAIL 也写文件（默认不产出半成品）")
    p.add_argument("--quiet", action="store_true", help="减少进度输出")
    args = p.parse_args(argv)
    args.parsed_bbox = tuple(args.bbox) if args.bbox else None

    if not 1 <= args.batch <= 100:
        p.error("--batch 必须在 1..100")
    if args.parsed_bbox is None and (args.width < 6 or args.height < 6):
        p.error("--width/--height 至少为 6（或改用 --bbox 由对边距反推）")
    if args.cell_km <= 0:
        p.error("--cell-km 必须为正")
    if args.dem_tif is None:
        args.dem_tif = str(resolve_repo_dir() / "data" / "dem" / "global_dem_30s.tif")
    if args.capabilities:
        print("##CAP## " + json.dumps(capabilities_payload(args.dem_tif), ensure_ascii=False, default=str))
        return 0
    if args.plan:
        if args.parsed_bbox is None:
            p.error("--plan 需要 --bbox 西 南 东 北")
        try:
            print("##PLAN## " + json.dumps(plan_payload(args.parsed_bbox, args.cell_km), ensure_ascii=False))
        except Exception as exc:
            print("##PLAN## " + json.dumps({"ok": False, "error": str(exc)[:200]}, ensure_ascii=False))
            return 1
        return 0
    if args.offline:
        if args.landuse == "auto":
            args.landuse = "proxy"
            print("[landuse] --offline ⇒ 用噪声代理", file=sys.stderr)
        if args.dem == "open-meteo":
            args.dem = "local"
    if args.out is None:
        args.out = resolve_default_out()
    out_path = Path(args.out)

    if args.only_validate:
        data = json.loads(out_path.read_text(encoding="utf-8"))
        m = data["meta"]
        legacy_scale = m.get("mercator_scale")
        g = HexGrid(m["center_lonlat"][0], m["center_lonlat"][1], m["width"], m["height"], m["cell_km"],
                    scale=1.0 if legacy_scale is None else legacy_scale)
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
    print("[meta] 对边距 %.1f km（实地）= %.1f km（墨卡托，纬度放大 %.3f）"
          % (args.cell_km, grid.cell_m / 1000.0, grid.scale))
    print("[meta] elev %.1f–%.1f m / slope %.1f–%.1f deg / river %d / road %d / bridge %d（渡河 %d 处，沿河段 %d）/ city %d"
          % (st["elev_min"], st["elev_max"], st["slope_min"], st["slope_max"],
             st["river_cells"], st["road_cells"], st["bridge_count"],
             st.get("crossing_count", 0), st.get("bank_road_runs", 0), st["city_cells"]))
    print("[meta] 地物 铁路 %d / 车站 %d / 隧道 %d / 线路 %d / 电厂 %d / 变电站 %d / 机场 %d"
          % (st["railway_cells"], st["rail_station_cells"], st["tunnel_cells"],
             st["powerline_cells"], st["power_plant_cells"], st["substation_cells"],
             st["airport_cells"]))
    print("[meta] terrain = %s" % json.dumps(st["terrain_counts"], ensure_ascii=False))

    if fails and not args.allow_invalid:
        print("\n[abort] 自校验有 %d 项 FAIL ⇒ 未写出文件（不产出半成品）。"
              "确需强行写出请加 --allow-invalid。" % fails, file=sys.stderr)
        if args.print_meta:
            print("##META## " + json.dumps({"file": str(out_path), "ok": False,
                                            "fails": fails, "warns": warns,
                                            "errors": [n for lv, n, _ in res if lv == "FAIL"]},
                                           ensure_ascii=False))
        return 1

    out_path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(payload, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    with open(str(out_path), "w", encoding="utf-8", newline="\n") as fp:
        fp.write(text)
    print("\n[write] %s" % Path(out_path).resolve())
    print("[write] %d 格 / %d 字节 / sha256[:16]=%s"
          % (len(payload["hexes"]), len(text.encode("utf-8")),
             hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]))
    if args.print_meta:
        m = payload["meta"]
        print("##META## " + json.dumps({
            "ok": True, "file": str(out_path), "name": Path(out_path).name,
            "source": m["source"], "cell_km": m["cell_km"], "width": m["width"],
            "height": m["height"], "bbox": m["bbox"], "fails": fails, "warns": warns,
            "layers": {k: (v or {}).get("kind") or (v or {}).get("source")
                       for k, v in m["layers"].items()},
            "terrain_counts": m["stats"]["terrain_counts"],
            "generated_at": m["generated_at"],
        }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
