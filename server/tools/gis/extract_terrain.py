#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
extract_terrain.py — 从 GeoTIFF 提取地形数据，输出 C# 可直接读取的 .ter 二进制瓦片。

设计目标
--------
* 不依赖 GDAL / rasterio：仅用 tifffile（纯 Python，pip 可装）+ numpy + 标准库。
  地理参考通过解析 GeoTIFF 标签（ModelTiepointTag 33922 / ModelPixelScaleTag 33550 /
  ModelTransformationTag 34264）得到；像素解码对「瓦片 + DEFLATE + predictor 2」做
  手写解码（已与 tifffile 原生解码逐字节校验一致）。
* 输出紧凑、固定布局的 .ter 文件，C# 用 BinaryReader 即可按 WGS84 经纬度取样，
  运行时完全不依赖 Python / GDAL。

.ter 文件格式（小端 / little-endian，固定 80 字节头）
-------------------------------------------------
  offset  size  field
  0       4     magic  ASCII "TER1"
  4       4     int32  version (=1)
  8       4     int32  crs    (EPSG，例如 4326)
  12      8     float64 west   (°E)
  20      8     float64 south  (°N)
  28      8     float64 east   (°E)
  36      8     float64 north  (°N)
  44      4     int32  cols    (列数 = 经度方向采样数)
  48      4     int32  rows    (行数 = 纬度方向采样数，row 0 在 north)
  52      8     float64 resLon (°/列)
  60      8     float64 resLat (°/行，正值)
  68      4     int32  hasLandcover (0/1)
  72      2     int16  elevNoData
  74      1     uint8  lcNoData
  75      5     (padding, 0)
  --- 头部结束（80 字节） ---
  80            int16[rows*cols]  高程数组（行优先，row0=north）
  +             uint8[rows*cols]  土地覆盖（仅当 hasLandcover=1）

坐标换算（C# 端）：
  col = floor((lon - west) / resLon)
  row = floor((north - lat) / resLat)
  elev = int16[col + row*cols];  lc = uint8[col + row*cols]  (若有)

用法
----
  # 小区域（调试 / 单测）
  python extract_terrain.py --mode region --bbox 4 48 9 51.5 \
      --out D:/Project/wargame/server/data/terrain_test --name rhein

  # 全球切瓦片（一次性离线生成，按 10°×10°）
  python extract_terrain.py --mode global --tile-deg 10 \
      --out D:/Project/wargame/server/data/terrain

输出目录：
  <out>/<name>.ter               区域模式单文件
  <out>/dem_<z>/NxxExxx.ter      全球模式高程+土地覆盖合并瓦片（每瓦片含两层）
  <out>/terrain_manifest.json    瓦片索引（C# 用它定位坐标→文件）
"""

import argparse
import json
import math
import os
import struct
import sys
import zlib

import numpy as np
import tifffile

# ---------------------------------------------------------------------------
# GeoTIFF 读取（无 GDAL）
# ---------------------------------------------------------------------------


class GeoBand:
    """封装一个单波段 GeoTIFF：地理参考 + 按经纬度窗口读取像素。"""

    def __init__(self, path):
        self.path = path
        with tifffile.TiffFile(path) as tif:
            pg = tif.pages[0]
            self.height, self.width = pg.shape
            self.dtype = np.dtype(pg.dtype)
            self.nodata = pg.nodata
            self._tile_w = getattr(pg, "tilewidth", 0) or 0
            self._tile_h = getattr(pg, "tilelength", 0) or 0
            self._rows_per_strip = pg.rowsperstrip or 0
            self._compression = int(pg.compression)
            self._predictor = int(pg.predictor)
            # 缓存偏移/长度（瓦片或条带）
            self._offsets = list(pg.dataoffsets)
            self._bytecounts = list(pg.databytecounts)
            # 地理参考：优先 ModelTransformationTag，否则 tiepoint+scale
            self._parse_geotiff(pg)

    def _parse_geotiff(self, pg):
        tags = pg.tags
        tr = tags.get(34264)  # ModelTransformationTag
        if tr is not None:
            m = list(tr.value)
            # 行优先 4x4：X = m0 + col*m1 + row*m2 ; Y = m4 + col*m5 + row*m6
            self.a, self.c, self.f = m[0], m[1], m[2]
            self.b, self.d, self.e = m[4], m[5], m[6]
            return
        tp = tags.get(33922)  # ModelTiepointTag
        ps = tags.get(33550)  # ModelPixelScaleTag
        if tp is not None and ps is not None:
            t = list(tp.value)
            s = list(ps.value)
            i, j = t[0], t[1]
            X0, Y0 = t[3], t[4]
            sx, sy = s[0], s[1]
            # X = X0 + (col - i)*sx ; Y = Y0 - (row - j)*sy  (北向上)
            self.a, self.c, self.f = X0 - i * sx, sx, 0.0
            self.b, self.d, self.e = Y0 + j * sy, 0.0, -sy
            return
        raise ValueError(f"{self.path}: 缺少 GeoTIFF 地理参考标签")

    def _pixel_to_geo(self, col, row):
        x = self.a + col * self.c + row * self.f
        y = self.b + col * self.d + row * self.e
        return x, y

    def geo_to_colrow(self, lon, lat):
        """返回覆盖该经纬度的源像素索引（浮点，用于范围计算）。"""
        # 逆变换（要求仿射可解；tiepoint+scale 与 transformation 都线性）
        # 解 [c f; d e]·[col;row] = [lon-a; lat-b]
        det = self.c * self.e - self.f * self.d
        if abs(det) < 1e-15:
            raise ValueError("地理参考仿射不可逆")
        col = (self.e * (lon - self.a) - self.f * (lat - self.b)) / det
        row = (-self.d * (lon - self.a) + self.c * (lat - self.b)) / det
        return col, row

    def _decode_block(self, idx, shape):
        with open(self.path, "rb") as fh:
            fh.seek(self._offsets[idx])
            raw = zlib.decompress(fh.read(self._bytecounts[idx]))
        arr = np.frombuffer(raw, dtype=self.dtype).reshape(shape)
        if self._predictor == 2:  # 水平差分预测
            if self.dtype == np.int16:
                arr = np.cumsum(arr.astype(np.int32), axis=1).astype(np.int16)
            else:
                arr = (np.cumsum(arr.astype(np.int32), axis=1) % 256).astype(self.dtype)
        return arr

    def read_window(self, lon_w, lat_s, lon_e, lat_n):
        """读取 WGS84 窗口 [lon_w, lon_e]×[lat_s, lat_n] 的原生分辨率矩形。

        返回 (array[rows,cols], actual_west, actual_south, actual_east, actual_north)
        其中 actual_* 为该矩形四个角的真实地理坐标（像素角点约定）。
        """
        # 计算源像素索引范围（角点约定：col 覆盖 [a+col*c, a+(col+1)*c]）
        c0f, r0f = self.geo_to_colrow(lon_w, lat_n)  # 西北角
        c1f, r1f = self.geo_to_colrow(lon_e, lat_s)  # 东南角
        col0 = int(math.floor(c0f))
        col1 = int(math.ceil(c1f))
        row0 = int(math.floor(r0f))
        row1 = int(math.ceil(r1f))
        col0 = max(0, min(self.width, col0))
        col1 = max(0, min(self.width, col1))
        row0 = max(0, min(self.height, row0))
        row1 = max(0, min(self.height, row1))
        if col1 <= col0 or row1 <= row0:
            return (np.zeros((0, 0), dtype=self.dtype), 0.0, 0.0, 0.0, 0.0)
        cols = col1 - col0
        rows = row1 - row0
        out = np.zeros((rows, cols), dtype=self.dtype)

        if self._tile_w > 0 and self._compression == 8:
            tw, th = self._tile_w, self._tile_h
            ntx = (self.width + tw - 1) // tw
            for ty in range(row0 // th, (row1 + th - 1) // th):
                for tx in range(col0 // tw, (col1 + tw - 1) // tw):
                    idx = ty * ntx + tx
                    sr0 = ty * th
                    sc0 = tx * tw
                    sr1 = min(sr0 + th, self.height)
                    sc1 = min(sc0 + tw, self.width)
                    arr = self._decode_block(idx, (sr1 - sr0, sc1 - sc0))
                    dr0 = max(sr0, row0) - row0
                    dr1 = min(sr1, row1) - row0
                    dc0 = max(sc0, col0) - col0
                    dc1 = min(sc1, col1) - col0
                    out[dr0:dr1, dc0:dc1] = arr[
                        max(sr0, row0) - sr0 : min(sr1, row1) - sr0,
                        max(sc0, col0) - sc0 : min(sc1, col1) - sc0,
                    ]
        else:
            # 非瓦片/非 deflate：退回 tifffile 全页解码再裁剪（源较小时可接受）
            with tifffile.TiffFile(self.path) as tif:
                full = tif.pages[0].asarray()
            out = full[row0:row1, col0:col1].copy()

        west = self.a + col0 * self.c + row0 * self.f
        north = self.b + col0 * self.d + row0 * self.e
        east = self.a + col1 * self.c + row1 * self.f
        south = self.b + col1 * self.d + row1 * self.e
        return out, west, south, east, north


# ---------------------------------------------------------------------------
# .ter 写出
# ---------------------------------------------------------------------------

HEADER_FMT = "<4s i i d d d d i i d d i h B 5x"
HEADER_SIZE = 80
MAGIC = b"TER1"
VERSION = 1


def write_ter(path, west, south, east, north, res_lon, res_lat,
              elev, landcover, elev_nodata, lc_nodata, crs=4326):
    rows, cols = elev.shape
    has_lc = landcover is not None
    with open(path, "wb") as f:
        f.write(struct.pack(HEADER_FMT, MAGIC, VERSION, crs,
                            west, south, east, north, cols, rows,
                            res_lon, res_lat, 1 if has_lc else 0,
                            int(elev_nodata) if elev_nodata is not None else -32768,
                            int(lc_nodata) if lc_nodata is not None else 0))
        f.write(np.ascontiguousarray(elev.astype("<i2")).tobytes())
        if has_lc:
            f.write(np.ascontiguousarray(landcover.astype("<u1")).tobytes())


def resample_nearest(src, src_west, src_north, src_res,
                     dst_cols, dst_rows, dst_west, dst_north, dst_res):
    """把分类数据（土地覆盖）按最邻近重采样到目标网格。"""
    # 目标每个像素中心经度/纬度
    c_idx = np.arange(dst_cols)
    r_idx = np.arange(dst_rows)
    lon = dst_west + (c_idx + 0.5) * dst_res
    lat = dst_north - (r_idx + 0.5) * dst_res
    # 目标 → 源像素索引
    sc = np.clip(((lon - src_west) / src_res).astype(np.int64), 0, src.shape[1] - 1)
    sr = np.clip(((src_north - lat) / src_res).astype(np.int64), 0, src.shape[0] - 1)
    return src[np.ix_(sr, sc)]


# ---------------------------------------------------------------------------
# 生成逻辑
# ---------------------------------------------------------------------------


def make_tile(dem, landcover, lon_w, lat_s, lon_e, lat_n, out_dir, name):
    elev, ew, es, ee, en = dem.read_window(lon_w, lat_s, lon_e, lat_n)
    if elev.size == 0:
        print(f"  ! 跳过空瓦片 {name}（窗口在数据源范围外）")
        return None
    res_lon = (ee - ew) / elev.shape[1]
    res_lat = (en - es) / elev.shape[0]
    if landcover is not None:
        lc, lw, ls, le, ln = landcover.read_window(lon_w, lat_s, lon_e, lat_n)
        if lc.size == 0:
            lc = None
        else:
            lc = resample_nearest(lc, lw, ln, (le - lw) / lc.shape[1],
                                 elev.shape[1], elev.shape[0], ew, en, res_lon)
    else:
        lc = None
    path = os.path.join(out_dir, name + ".ter")
    write_ter(path, ew, es, ee, en, res_lon, res_lat, elev, lc,
              dem.nodata, landcover.nodata if landcover else 0)
    return {
        "name": name,
        "file": os.path.relpath(path, out_dir).replace("\\", "/"),
        "west": round(ew, 6), "south": round(es, 6),
        "east": round(ee, 6), "north": round(en, 6),
        "cols": elev.shape[1], "rows": elev.shape[0],
        "resLon": res_lon, "resLat": res_lat,
        "hasLandcover": lc is not None,
    }


def tile_name(lon_w, lat_n):
    ns = "N" if lat_n >= 0 else "S"
    ew = "E" if lon_w >= 0 else "W"
    return f"{ns}{abs(int(round(lat_n))):02d}{ew}{abs(int(round(lon_w))):03d}"


def run_region(args, dem, landcover):
    os.makedirs(args.out, exist_ok=True)
    w, s, e, n = args.bbox
    rec = make_tile(dem, landcover, w, s, e, n, args.out, args.name)
    manifest = {"version": VERSION, "crs": 4326, "mode": "region",
                "tileDeg": None, "tiles": [rec] if rec else []}
    with open(os.path.join(args.out, "terrain_manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    print(f"区域瓦片已生成：{os.path.join(args.out, args.name + '.ter')}")


def run_global(args, dem, landcover):
    os.makedirs(args.out, exist_ok=True)
    td = args.tile_deg
    tiles = []
    for n in range(90, -90, -td):
        south = n - td
        if south < -90:
            south = -90
        if n <= south:
            continue
        for w in range(-180, 180, td):
            east = w + td
            if east > 180:
                east = 180
            if east <= w:
                continue
            name = tile_name(w, n)
            rec = make_tile(dem, landcover, w, south, east, n, args.out, name)
            if rec:
                tiles.append(rec)
                print(f"  + {name}  ({rec['cols']}x{rec['rows']})")
    manifest = {"version": VERSION, "crs": 4326, "mode": "global",
                "tileDeg": td, "tiles": tiles}
    with open(os.path.join(args.out, "terrain_manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    print(f"全球瓦片已生成：{len(tiles)} 块 -> {args.out}")


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_dem = os.path.join(here, "..", "..", "data", "gis", "dem", "global_dem_30s.tif")
    default_lc = os.path.join(here, "..", "..", "data", "gis", "landcover", "global_landcover_0p01.tif")
    ap = argparse.ArgumentParser(description="GeoTIFF → .ter 地形瓦片（无 GDAL）")
    ap.add_argument("--dem", default=os.path.normpath(default_dem))
    ap.add_argument("--landcover", default=os.path.normpath(default_lc))
    ap.add_argument("--mode", choices=["region", "global"], default="region")
    ap.add_argument("--bbox", nargs=4, type=float, metavar=("W", "S", "E", "N"),
                    help="region 模式窗口（度）")
    ap.add_argument("--tile-deg", type=float, default=10.0, help="global 模式瓦片边长（度）")
    ap.add_argument("--out", default=os.path.join(here, "..", "..", "data", "terrain"))
    ap.add_argument("--name", default="region", help="region 模式输出文件名")
    args = ap.parse_args()

    if args.mode == "region" and not args.bbox:
        ap.error("region 模式需要 --bbox W S E N")

    print(f"DEM : {args.dem}")
    print(f"LC  : {args.landcover}")
    dem = GeoBand(args.dem)
    landcover = GeoBand(args.landcover) if os.path.exists(args.landcover) else None
    print(f"DEM 尺寸 {dem.width}x{dem.height}  nodata={dem.nodata}")
    if landcover:
        print(f"LC  尺寸 {landcover.width}x{landcover.height}  nodata={landcover.nodata}")

    if args.mode == "region":
        run_region(args, dem, landcover)
    else:
        run_global(args, dem, landcover)


if __name__ == "__main__":
    main()
