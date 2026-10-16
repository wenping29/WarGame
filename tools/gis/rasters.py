#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
rasters.py —— 真实栅格按六角格聚合取样（可选依赖：rasterio + numpy）。

hexgen.py 本体保持零第三方依赖；只有需要**真实栅格**的数据层才走这里，
导入失败或文件缺失时调用方自行降级，绝不抛异常打断生成。

两个取样器：
  · dem_by_hex       —— 高程：格内像素均值（本地 GeoTIFF，EPSG:4326 30 弧秒 ≈ 926 m）
  · landcover_by_hex —— 土地覆盖：格内像素众数（ESA WorldCover 10 m，含各类占比）

为什么按「面」而不是按格心取值：格心采样会让 10 m 栅格在 5 km 格上退化成单点噪声
（一格 96% 城区可能因格心落在街上而判成非城区），众数/均值才是面积统计。
像素→格用格网逆变换 + cube 取整，与引擎 HexGeo.nearest 同一套约定。
"""

from __future__ import annotations

import math
import os
from collections import Counter

try:
    import numpy as np
    import rasterio
    from rasterio.transform import Affine
    from rasterio.windows import Window, from_bounds, intersection

    HAVE_GDAL = True
except Exception:  # pragma: no cover - 无 GDAL 环境时由调用方降级
    HAVE_GDAL = False

HALF_CIRCUMFERENCE_M = 20037508.342789244
R_M = HALF_CIRCUMFERENCE_M / math.pi

# ESA WorldCover v200 分类 → 引擎地形白名单。
# 70 永久积雪 / 90 苔原 / 20 稀疏植被归入 rough：引擎没有对应类别，而这三类的共同点
# 是「稀疏覆盖的裸地」，不是可机动的草地。
WORLDCOVER_TERRAIN = {
    10: "woods",   # 树覆盖
    20: "rough",   # 稀疏灌木/草本
    30: "clear",   # 草地
    40: "clear",   # 耕地
    50: "city",    # 建筑区
    60: "water",   # 水体
    70: "rough",   # 永久积雪/冰
    80: "swamp",   # 草本沼泽
    90: "rough",   # 苔原/地衣
    100: "swamp",  # 红树林
    110: "swamp",  # 盐沼
}
WORLDCOVER_LEGEND = {
    10: "树覆盖", 20: "稀疏植被", 30: "草地", 40: "耕地", 50: "建筑区", 60: "水体",
    70: "永久积雪", 80: "草本沼泽", 90: "苔原/地衣", 100: "红树林", 110: "盐沼",
}
# 10 m 原生分辨率太大（一个 3°×3° 瓦片 36000² px）；按「每格约 3 个采样」降采样，
# GDAL 自动选金字塔层，一次战区范围的下载量在 MB 级。
SAMPLES_PER_CELL = 3.0
MAX_PIXELS = 1_200_000

# WorldCover 瓦片：3°×3°，命名取左下角（N51E006 = 北纬 51..54 / 东经 6..9）。
# 走 http:// 而非 https://：本机 GDAL 用 schannel，对 S3 报
# "certificate chain is incomplete"；该桶为公开只读栅格，无敏感数据。
WORLDCOVER_URL = ("http://esa-worldcover.s3.amazonaws.com/v200/2021/map/"
                  "ESA_WorldCover_10m_2021_v200_{tag}_Map.tif")


def capabilities() -> dict:
    return {
        "rasterio": HAVE_GDAL,
        "version": (getattr(rasterio, "__version__", "?") if HAVE_GDAL else None),
        "reason": None if HAVE_GDAL else "rasterio/numpy 未安装",
    }


def raster_info(path: str) -> dict:
    """栅格元信息（能力探测/界面展示用）；打不开返回 error。"""
    if not HAVE_GDAL:
        return {"error": "rasterio 未安装"}
    try:
        os.environ.setdefault("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR")
        with rasterio.open(path) as src:
            return {
                "width": src.width, "height": src.height, "crs": str(src.crs),
                "res_deg": [round(v, 8) for v in src.res],
                "bounds": [round(v, 4) for v in src.bounds],
                "dtype": src.dtypes[0], "nodata": src.nodata,
            }
    except Exception as exc:
        return {"error": "%s: %s" % (type(exc).__name__, str(exc)[:180])}


def _target_shape(bbox, cell_km):
    """每格约 SAMPLES_PER_CELL 个采样点，且总像元数受限。"""
    mid_lat = math.radians((bbox[1] + bbox[3]) * 0.5)
    w_km = (bbox[2] - bbox[0]) * 111.32 * math.cos(mid_lat)
    h_km = (bbox[3] - bbox[1]) * 110.57
    step = max(cell_km / SAMPLES_PER_CELL, 0.05)
    ow = int(max(2, min(round(w_km / step), 4000)))
    oh = int(max(2, min(round(h_km / step), 4000)))
    while ow * oh > MAX_PIXELS:
        if ow >= oh:
            ow = int(ow * 0.85)
        else:
            oh = int(oh * 0.85)
    return oh, ow


def _read_window(path, bbox, out_shape):
    """按 bbox（lon/lat）读第一波段 → (掩码数组, meta, 像元中心经度数组, 纬度数组)。"""
    os.environ.setdefault("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR")
    with rasterio.open(path) as src:
        full = Window(0, 0, src.width, src.height)
        w = intersection(from_bounds(bbox[0], bbox[1], bbox[2], bbox[3], src.transform), full)
        if w.width <= 0 or w.height <= 0:
            raise ValueError("范围 %s 与该栅格无交集" % (bbox,))
        w = w.round_offsets().round_lengths().crop(src.height, src.width)
        arr = src.read(1, window=w, masked=True, out_shape=out_shape)
        meta = dict(src.meta)
        meta["res"] = list(src.res)                 # src.meta 里没有 res，显式补上分辨率供溯源标注
        tf = rasterio.windows.transform(w, src.transform)
        etf = Affine(tf.a * (w.width / out_shape[1]), tf.b, tf.xoff,
                     tf.d, tf.e * (w.height / out_shape[0]), tf.yoff)
        lon = etf.xoff + (np.arange(out_shape[1]) + 0.5) * etf.a
        lat = etf.yoff + (np.arange(out_shape[0]) + 0.5) * etf.e
        return arr, meta, lon, lat


def _coords_to_cells(grid, lon, lat):
    """
    WGS84 经纬度数组（任意形状，1-D 点列或 2-D 栅格）→ 六角格 index（越界为 -1）。
    向量化 cube 取整；栅格与矢量共用同一套约定，二者不会各自漂开。
    """
    lg = np.asarray(lon, dtype=float)
    la = np.asarray(lat, dtype=float)
    mx = lg * HALF_CIRCUMFERENCE_M / 180.0
    my = R_M * np.log(np.tan((90.0 + la) * math.pi / 360.0))
    fr = (grid.mercator_oy - my) / grid.row_step_m
    fq = (mx - grid.mercator_ox) / grid.hex_width_m - fr / 2.0
    fs = -fq - fr
    rq, rr, rs = np.rint(fq), np.rint(fr), np.rint(fs)
    dq, dr, ds = np.abs(rq - fq), np.abs(rr - fr), np.abs(rs - fs)
    rq = np.where((dq > dr) & (dq > ds), -rr - rs, rq)
    rr = np.where((dr >= dq) & (dr > ds), -rq - rs, rr)
    idx = (rr * grid.width + rq).astype(np.int64)
    inside = (rq >= 0) & (rq < grid.width) & (rr >= 0) & (rr < grid.height)
    return idx, inside


def _pixel_cells(grid, lon, lat):
    """像元中心（1-D 经度数组 × 1-D 纬度数组张成的栅格）→ 每像元的格 index。"""
    lg, la = np.meshgrid(lon, lat)
    return _coords_to_cells(grid, lg, la)


def _cells_from_index(grid, idx, inside):
    """index 数组 → {(q,r)} 集合（q = i % width，r = i // width，与格网 index 约定一致）。"""
    keep = idx[inside]
    return {(int(i) % grid.width, int(i) // grid.width) for i in keep}


def _fill_gaps(grid, values):
    """采样过稀导致空格时，用最近非空格补齐（不产出缺高程/缺类别的格）。"""
    from hexgen import axial_distance

    known = [c for c, v in values.items() if v is not None]
    if not known:
        raise ValueError("栅格在该范围内没有有效像元")
    for c in grid.iter_cells():
        if values.get(c) is None:
            values[c] = values[min(known, key=lambda k: (axial_distance(c, k), k[1], k[0]))]
    return values


def dem_by_hex(path: str, grid, bbox=None):
    """真实 DEM → 每格高程均值（米）。返回 (values, info)。"""
    if not HAVE_GDAL:
        raise RuntimeError("rasterio 未安装")
    bbox = tuple(bbox or grid.bbox())
    out = _target_shape(bbox, grid.cell_km)
    arr, meta, lon, lat = _read_window(path, bbox, out)
    idx, inside = _pixel_cells(grid, lon, lat)
    ok = inside & (~arr.mask)
    sums = np.bincount(idx[ok], weights=arr.data[ok].astype(float), minlength=grid.width * grid.height)
    cnts = np.bincount(idx[ok], minlength=grid.width * grid.height)
    values = {}
    for cell in grid.iter_cells():
        i = grid.index(*cell)
        values[cell] = float(sums[i] / cnts[i]) if cnts[i] else None
    _fill_gaps(grid, values)
    return values, {
        "kind": "local-geotiff", "path": path.replace("\\", "/"), "crs": str(meta.get("crs")),
        "res_deg": [round(v, 8) for v in meta.get("res", (0, 0))],
        "nodata": meta.get("nodata"),
        "samples": {"out_shape": list(out), "pixels": int(out[0] * out[1]),
                    "used_pixels": int(ok.sum())},
    }


def landcover_by_hex(grid, bbox=None, url=WORLDCOVER_URL, tile_deg=3):
    """
    ESA WorldCover → 每格众数类别 + 占比。跨瓦片分别读取再合并计数。
    values[cell] = {"class": int, "share": float, "shares": {class: pct}}
    """
    if not HAVE_GDAL:
        raise RuntimeError("rasterio 未安装")
    from hexgen import axial_distance

    bbox = tuple(bbox or grid.bbox())
    tallies = Counter()
    tiles = []
    la = int(math.floor(bbox[1] / tile_deg) * tile_deg)
    le = int(math.floor(bbox[3] / tile_deg) * tile_deg)
    lo = int(math.floor(bbox[0] / tile_deg) * tile_deg)
    ln = int(math.floor(bbox[2] / tile_deg) * tile_deg)
    out = _target_shape(bbox, grid.cell_km)
    n = grid.width * grid.height
    for tlat in range(la, le + 1, tile_deg):
        for tlon in range(lo, ln + 1, tile_deg):
            tag = "%s%02d%s%03d" % ("N" if tlat >= 0 else "S", abs(tlat),
                                    "E" if tlon >= 0 else "W", abs(tlon))
            try:
                arr, _meta, lon, lat = _read_window(url.format(tag=tag), bbox, out)
            except Exception:
                continue                          # 该瓦片与范围无交集（或服务端无此瓦片）
            tiles.append(tag)
            idx, inside = _pixel_cells(grid, lon, lat)
            ok = inside & (~arr.mask)
            key = idx[ok] * 256 + arr.data[ok].astype(np.int64)
            uniq, cnts = np.unique(key, return_counts=True)
            for k, c in zip(uniq.tolist(), cnts.tolist()):
                tallies[(k // 256, k % 256)] = tallies.get((k // 256, k % 256), 0) + c
    by_cell = {}
    for (i, c), tot in tallies.items():
        by_cell.setdefault(i, []).append((int(c), tot))
    values = {}
    for cell in grid.iter_cells():
        cls = by_cell.get(grid.index(*cell))
        if not cls:
            values[cell] = None
            continue
        total = sum(tot for _, tot in cls)
        top, tot = max(cls, key=lambda t: (t[1], -t[0]))
        values[cell] = {"class": int(top), "share": tot / total,
                        "shares": {int(c): round(t / total, 3) for c, t in sorted(cls)}}
    known = [c for c in grid.iter_cells() if values[c] is not None]
    if not known:
        raise RuntimeError("WorldCover 在该范围无有效像元")
    for cell in grid.iter_cells():
        if values[cell] is None:
            values[cell] = values[min(known, key=lambda k: (axial_distance(cell, k), k[1], k[0]))]
    return values, {
        "kind": "esa-worldcover-v200", "product": "ESA WorldCover 10m 2021",
        "url_sample": url.format(tag="N51E006"), "tiles": tiles, "cells_with_data": len(known),
        "grid_cells": n,
        "legend": {str(k): v for k, v in WORLDCOVER_LEGEND.items()},
        "terrain_map": {str(k): v for k, v in WORLDCOVER_TERRAIN.items()},
        "samples": {"out_shape": list(out), "pixels": int(out[0] * out[1])},
    }


# =====================================================================================
# 真实水系矢量（Natural Earth 10m，GeoPackage）→ 每格 河/湖 判定
# 与栅格取样不同：这里是矢量叠加，用每格的尖顶六角多边形与河线/湖面求交。
# 依赖 geopandas + shapely（可选）；缺库或缺文件由调用方降级，绝不打断生成。
# =====================================================================================

WATER_RIVER_LAYERS = ("rivers", "rivers_scale_rank")
WATER_LAKE_LAYERS = ("lakes_all", "lakes")
# 河线是零面积的线：按对边距给一点缓冲宽度，代表"这条河占了一格"，
# 否则贴着格边流过的河会因数值相切而漏判。
RIVER_BUFFER_RADIUS_FRAC = 0.35
RIVER_BUFFER_MIN_M = 20.0


def _hex_polygons(grid):
    """每格的尖顶六角多边形（EPSG:3857，与格网同坐标系）。返回 (cells, polys, hex_area)。"""
    from shapely.geometry import Polygon
    r = grid.radius_m
    cells = list(grid.iter_cells())
    polys = []
    for c in cells:
        cx, cy = grid.center(c)
        pts = [(cx + r * math.cos(math.radians(60 * i + 30)),
                cy + r * math.sin(math.radians(60 * i + 30))) for i in range(6)]
        polys.append(Polygon(pts))
    hex_area = 1.5 * math.sqrt(3.0) * r * r          # 正六边形面积 = (3√3/2)R²
    return cells, polys, hex_area


def water_by_hex(gpkg: str, grid, bbox=None):
    """
    Natural Earth 水系 gpkg → (river_cells:set, lake_share:{cell:pct}, info)。
      · river_cells：河线（带缓冲）穿过的格
      · lake_share：湖面与该格相交的面积占比（0..1）
    矢量按格网 bbox 裁剪后转 EPSG:3857，与每格六角多边形求交；无要素则相应集合为空。
    """
    try:
        import geopandas as gpd
        from shapely.ops import unary_union
    except Exception as exc:                          # 缺 geopandas/shapely ⇒ 调用方降级
        raise RuntimeError("geopandas/shapely 未安装：%s" % str(exc)[:160])
    if not os.path.isfile(gpkg):
        raise RuntimeError("水系 GeoPackage 不存在：%s" % gpkg)

    bbox = tuple(bbox or grid.bbox())                 # WGS84 (西, 南, 东, 北)
    cells, polys, hex_area = _hex_polygons(grid)
    hexes = gpd.GeoDataFrame({"cid": range(len(cells)), "geometry": polys}, crs="EPSG:3857")

    river_cells, lake_share = set(), {}
    info = {"kind": "natural-earth-10m-water", "gpkg": gpkg.replace("\\", "/"),
            "source": "Natural Earth 10m 水系（rivers/lakes）", "crs": "EPSG:4326→3857",
            "river_features": 0, "lake_features": 0, "layers_used": [],
            "river_buffer_m": round(max(RIVER_BUFFER_MIN_M, grid.radius_m * RIVER_BUFFER_RADIUS_FRAC), 1)}

    # --- 河流（线）：缓冲后半格相交即判河 ---
    riv = None
    for layer in WATER_RIVER_LAYERS:
        try:
            g = gpd.read_file(gpkg, layer=layer, bbox=bbox)
        except Exception:
            continue
        if g is not None and len(g):
            riv = g.to_crs("EPSG:3857")
            info["layers_used"].append(layer)
            info["river_features"] = int(len(riv))
            break
    if riv is not None:
        buf = max(RIVER_BUFFER_MIN_M, grid.radius_m * RIVER_BUFFER_RADIUS_FRAC)
        riv_geom = unary_union(list(riv.geometry.values)).buffer(buf)
        for cid in hexes.index[hexes.intersects(riv_geom)]:
            river_cells.add(cells[cid])

    # --- 湖泊（面）：按相交面积占比 ---
    lak = None
    for layer in WATER_LAKE_LAYERS:
        try:
            g = gpd.read_file(gpkg, layer=layer, bbox=bbox)
        except Exception:
            continue
        if g is not None and len(g):
            lak = g.to_crs("EPSG:3857")
            info["layers_used"].append(layer)
            info["lake_features"] = int(len(lak))
            break
    if lak is not None:
        lak_geom = unary_union(list(lak.geometry.values))
        cand = sorted(set(int(c) for c in hexes.index[hexes.intersects(lak_geom)]))
        for cid in cand:
            inter = polys[cid].intersection(lak_geom)
            if not inter.is_empty and inter.area > 0:
                lake_share[cells[cid]] = round(min(1.0, inter.area / hex_area), 3)

    info["river_cells"] = len(river_cells)
    info["lake_cells"] = len(lake_share)
    return river_cells, lake_share, info


# =====================================================================================
# 真实交通/设施矢量：路网（ESri File Geodatabase）+ 机场（Natural Earth shapefile）
# =====================================================================================

# World_Roads（Natural Earth 10m，打包在 ESri 的 LPK 里）的 RANK：1 = 高速/国际干线（A、E 路），
# 2 = 主要公路。默认只取 1：在 2.5 km 格上，鲁尔-莱茵这类都会区取到 RANK 2 会把 43% 的图涂成公路，
# 「机动脉络」就失去意义；RANK 1 覆盖 28%，仍是真实的高速网。
ROAD_RANKS = (1,)
ROAD_COLUMNS = ("ISO_CC", "Name1", "Shield", "TYPE", "RANK", "RD_FEATURE")
# 线要素按「半个格宽」离散成点再落格：只取端点会漏掉斜穿中间的格。
LINE_SAMPLE_FRAC = 0.5
RD_TUNNEL = "tunnel"
RD_BRIDGE = "bridge"
AIRPORT_MAJOR = ("major", "large", "international", "primary")
AIRPORT_COLUMNS = ("name", "type", "scalerank", "iata")


def _read_vector(path, bbox, columns):
    """按 bbox（WGS84 lon/lat）读矢量。File Geodatabase 自带空间索引 ⇒ 全球库取一区是秒级。"""
    import pyogrio
    try:
        return pyogrio.read_dataframe(path, bbox=bbox, columns=list(columns) + ["geometry"])
    except Exception:
        return pyogrio.read_dataframe(path, bbox=bbox)   # 源里缺某条列 ⇒ 全列读，调用方按需取


def _col(df, name, default=""):
    """安全取列（字符串化 + 填缺）；源数据的属性完整性不作保证。"""
    import pandas as pd
    if name not in df.columns:
        return pd.Series([default] * len(df), index=df.index)
    return df[name].fillna(default).astype(str).str.strip()


def _line_cells(grid, geoms):
    """线要素 → 被穿过的格集合。"""
    import numpy as np
    import shapely
    if len(geoms) == 0:
        return set()
    tol = grid.hex_width_m * LINE_SAMPLE_FRAC * 180.0 / HALF_CIRCUMFERENCE_M
    seg = shapely.segmentize(np.asarray(geoms, dtype=object), tol)
    coords = shapely.get_coordinates(seg)
    if len(coords) == 0:
        return set()
    idx, inside = _coords_to_cells(grid, coords[:, 0], coords[:, 1])
    return _cells_from_index(grid, idx, inside)


def roads_by_hex(gdb: str, grid, bbox=None, ranks=ROAD_RANKS):
    """
    真实路网（ESri File Geodatabase，Natural Earth World_Roads）
      → (road_cells, tunnel_cells, bridge_cells, info)

      · road_cells   ：RANK ∈ ranks 的公路穿过的格
      · tunnel_cells ：RD_FEATURE = Tunnel 的路段穿过的格（真实隧道）
      · bridge_cells ：RD_FEATURE = Bridge 的路段穿过的格。仅作溯源计数 —— 引擎的桥位
        仍以「道路 ∩ 河网」为准，两套口径混用会破坏 bridge ⊆ river 与 道路∩河==桥位 契约。
    """
    bbox = tuple(bbox or grid.bbox())
    if not (os.path.isdir(gdb) or os.path.isfile(gdb)):
        raise RuntimeError("路网数据不存在：%s" % gdb)
    df = _read_vector(gdb, bbox, ROAD_COLUMNS)
    info = {"kind": "natural-earth-world-roads", "db": gdb.replace("\\", "/"),
            "source": "Natural Earth 10m World_Roads（ESri File Geodatabase, WGS84）",
            "features_total": int(len(df)), "ranks": list(ranks),
            "line_sample_m": round(grid.hex_width_m * LINE_SAMPLE_FRAC, 1)}
    if len(df) == 0:
        info.update(road_cells=0, note="该范围内无路网要素")
        return set(), set(), set(), info
    try:
        import pandas as pd
        rk = pd.to_numeric(df["RANK"], errors="coerce").fillna(9).astype(int) \
            if "RANK" in df.columns else None
    except Exception:
        rk = None
    sub = df if rk is None else df[rk.isin(list(ranks))]
    feat = _col(df, "RD_FEATURE").str.lower()
    road_cells = _line_cells(grid, list(sub.geometry.values))
    tun_cells = _line_cells(grid, list(df.geometry.values[(feat == RD_TUNNEL).values]))
    br_cells = _line_cells(grid, list(df.geometry.values[(feat == RD_BRIDGE).values]))
    info.update({
        "features_used": int(len(sub)),
        "road_cells": len(road_cells),
        "real_tunnel_cells": len(tun_cells),
        "real_bridge_cells": len(br_cells),
        "named_routes": sorted({v for v in _col(sub, "Name1") if v})[:12],
        "countries": sorted({v for v in _col(sub, "ISO_CC") if v})[:8],
    })
    return road_cells, tun_cells, br_cells, info


def airports_by_hex(shp: str, grid, bbox=None):
    """
    真实机场点（Natural Earth 10m airports）→ ({(q,r): 等级}, info)。
    2 = 干线机场（type ∈ AIRPORT_MAJOR），1 = 普通机场；同格多点取高等级。
    """
    import numpy as np
    import shapely
    bbox = tuple(bbox or grid.bbox())
    if not os.path.isfile(shp):
        raise RuntimeError("机场矢量不存在：%s" % shp)
    df = _read_vector(shp, bbox, AIRPORT_COLUMNS)
    info = {"kind": "natural-earth-10m-airports", "shp": shp.replace("\\", "/"),
            "source": "Natural Earth 10m airports（WGS84 点要素）",
            "features_total": int(len(df))}
    if len(df) == 0:
        info.update(airports=0, note="该范围内无真实机场要素")
        return {}, info
    coords = shapely.get_coordinates(np.asarray(df.geometry.values, dtype=object))
    idx, inside = _coords_to_cells(grid, coords[:, 0], coords[:, 1])
    names, types = list(_col(df, "name")), list(_col(df, "type").str.lower())
    marks, points = {}, []
    for k, (i, ok) in enumerate(zip(idx, inside)):
        if not ok:
            continue
        grade = 2 if types[k] in AIRPORT_MAJOR else 1
        cell = (int(i) % grid.width, int(i) // grid.width)
        if marks.get(cell, 0) < grade:
            marks[cell] = grade
        hit = next((p for p in points if (p["q"], p["r"]) == cell), None)
        if hit is None:
            points.append({"q": cell[0], "r": cell[1], "grade": grade,
                           "name": names[k][:40], "type": types[k],
                           "lon": round(float(coords[k][0]), 5), "lat": round(float(coords[k][1]), 5)})
        elif grade > hit["grade"]:
            hit.update(grade=grade, name=names[k][:40], type=types[k])
    info.update({
        "airports": len(marks),
        "major": sum(1 for v in marks.values() if v == 2),
        "points": sorted(points, key=lambda p: (-p["grade"], p["r"], p["q"]))[:8],
    })
    return marks, info

