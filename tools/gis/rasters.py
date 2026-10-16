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


def _pixel_cells(grid, lon, lat):
    """像元中心 → 六角格 index（越界的为 -1）。向量化 cube 取整。"""
    lg, la = np.meshgrid(lon, lat)
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
