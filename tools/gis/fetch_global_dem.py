#!/usr/bin/env python3
"""全球 DEM 下载器：AWS 开放数据 terrarium 瓦片 -> EPSG:3857 镶嵌 -> EPSG:4326 栅格。

数据源：s3://elevation-tiles-prod/terrarium（免登录，陆地 SRTM + 海洋 GEBCO 水深，
编码 elevation = R*256 + G + B/256 - 32768）。

  python fetch_global_dem.py --probe 200        # 只测吞吐，估算全量耗时
  python fetch_global_dem.py --z 7              # 全流程：下载->镶嵌->重采样->校验
  python fetch_global_dem.py --z 7 --step mosaic   # 只重跑镶嵌（瓦片已缓存）

瓦片缓存在 data/dem/tiles_z{z}/，已存在则跳过，可随时中断续跑。
依赖：numpy、Pillow、rasterio（下载用标准库 urllib）。
"""

import argparse
import concurrent.futures
import json
import math
import os
import sys
import threading
import time

import numpy as np
import rasterio
from PIL import Image
from rasterio.transform import from_bounds
from rasterio.windows import Window

TILE_URL = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"
TILE_PX = 256
NODATA = np.int16(-32768)
ORIGIN_SHIFT = 20037508.342789244

_tls = threading.local()
_print_lock = threading.Lock()


def log(msg):
    with _print_lock:
        sys.stdout.write(msg + "\n")
        sys.stdout.flush()


def http_get(url, timeout=30):
    """返回 bytes；404 返回 None。用 urllib：本机 requests/certifi 的 CA 链对 *.s3.amazonaws.com 校验失败。"""
    import urllib.error
    import urllib.request

    req = urllib.request.Request(url, headers={"User-Agent": "wargame-dem/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read()
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise


def tile_key(z, x, y):
    return os.path.join("tiles_z%d" % z, str(x), "%d_%d.png" % (x, y))


def fetch_tile(root, z, x, y, timeout=30, retries=4):
    """返回本地路径；服务端无此瓦片（极区等）返回 None 并记录。"""
    path = os.path.join(root, tile_key(z, x, y))
    if os.path.isfile(path) and os.path.getsize(path) > 0:
        return path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    url = TILE_URL.format(z=z, x=x, y=y)
    tmp = path + ".part"
    last = None
    for attempt in range(retries):
        try:
            data = http_get(url, timeout=timeout)
            if data is None:
                return None
            with open(tmp, "wb") as fh:
                fh.write(data)
            os.replace(tmp, path)
            return path
        except Exception as exc:  # noqa: BLE001 - 网络层任何异常都要退避重试
            last = exc
            time.sleep(1.5 * (attempt + 1))
    raise RuntimeError("tile %d/%d/%d 下载失败：%s" % (z, x, y, last))


def decode_tile(path):
    with Image.open(path) as im:
        a = np.asarray(im.convert("RGB"), dtype=np.int32)
    elev = a[:, :, 0] * 256 + a[:, :, 1] + a[:, :, 2] / 256.0 - 32768
    return np.clip(np.rint(elev), -32768, 32767).astype(np.int16)


def step_download(root, z, workers, total):
    n = 2 ** z
    jobs = [(x, y) for y in range(n) for x in range(n)]
    if total:
        jobs = jobs[:total]
    done = 0
    missing = 0
    t0 = time.time()

    def work(job):
        x, y = job
        p = fetch_tile(root, z, x, y)
        return job, p

    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        for (x, y), p in pool.map(work, jobs):
            done += 1
            if p is None:
                missing += 1
            if done % 200 == 0 or done == len(jobs):
                el = time.time() - t0
                rate = done / el if el else 0
                eta = (len(jobs) - done) / rate if rate else 0
                log("[下载] %d/%d (%.1f%%) 无瓦片%d 速率%.1f/秒 预计剩余%.1f分钟"
                    % (done, len(jobs), 100.0 * done / len(jobs), missing, rate, eta / 60))
    if total:
        log("[探测] %d 张瓦片用时 %.1f 秒（%.1f 张/秒）；全量 %d 张约 %.1f 分钟"
            % (done, time.time() - t0, done / max(time.time() - t0, 1e-6), 4 ** z,
               (4 ** z) / max(done / max(time.time() - t0, 1e-6), 1e-6) / 60))
    return missing


def step_mosaic(root, z):
    n = 2 ** z
    size = n * TILE_PX
    out = os.path.join(root, "global_dem_z%d_webmercator.tif" % z)
    if os.path.isfile(out):
        log("[镶嵌] 已存在，跳过 → %s" % out)
        return out
    width = height = size
    crs = rasterio.CRS.from_epsg(3857)
    transform = from_bounds(-ORIGIN_SHIFT, -ORIGIN_SHIFT, ORIGIN_SHIFT, ORIGIN_SHIFT, width, height)
    opts = dict(
        driver="GTiff", width=width, height=height, count=1, dtype="int16",
        crs=crs, transform=transform, nodata=int(NODATA), tiled=True,
        blockxsize=TILE_PX, blockysize=TILE_PX, compress="DEFLATE", predictor=2,
    )
    filled = skipped = 0
    t0 = time.time()
    with rasterio.open(out, "w", **opts) as dst:
        for y in range(n):
            for x in range(n):
                p = os.path.join(root, tile_key(z, x, y))
                if not os.path.isfile(p):
                    dst.write(np.full((TILE_PX, TILE_PX), NODATA, dtype=np.int16), 1,
                              window=Window(x * TILE_PX, y * TILE_PX, TILE_PX, TILE_PX))
                    skipped += 1
                    continue
                arr = decode_tile(p)
                dst.write(arr, 1, window=Window(x * TILE_PX, y * TILE_PX, TILE_PX, TILE_PX))
                filled += 1
            if (y + 1) % 8 == 0 or y == n - 1:
                log("[镶嵌] 行 %d/%d 已写 %d 块 用时%.0f秒" % (y + 1, n, filled, time.time() - t0))
    log("[镶嵌] 完成：%d 块写入，%d 块缺失留空 → %s (%.1f MB)"
        % (filled, skipped, out, os.path.getsize(out) / 1048576))
    return out


def step_resample(root, src_tif, cell_arcsec):
    """EPSG:3857 镶嵌 -> EPSG:4326 等经纬网格（双线性，逐行分块，内存占用小）。"""
    dx = cell_arcsec / 3600.0
    out_w = int(round(360.0 / dx))
    out_h = int(round(180.0 / dx))
    out = os.path.join(root, "global_dem_%ds.tif" % cell_arcsec)
    if os.path.isfile(out):
        log("[重采样] 已存在，跳过 → %s" % out)
        return out
    with rasterio.open(src_tif) as s:
        w, h, nod = s.width, s.height, s.nodata
        opts = dict(
            driver="GTiff", width=out_w, height=out_h, count=1, dtype="int16",
            crs=rasterio.CRS.from_epsg(4326),
            transform=from_bounds(-180.0, -90.0, 180.0, 90.0, out_w, out_h),
            nodata=int(NODATA), tiled=True, blockxsize=256, blockysize=256,
            compress="DEFLATE", predictor=2,
        )
        row_cache = {}
        cols = np.arange(out_w, dtype=np.float64) * dx - 180.0
        colf = (cols + 180.0) / 360.0 * w - 0.5
        c0 = np.clip(np.floor(colf).astype(np.int64), 0, w - 1)
        c1 = np.clip(c0 + 1, 0, w - 1)
        fc = (colf - c0).astype(np.float32)
        t0 = time.time()
        with rasterio.open(out, "w", **opts) as dst:
            for j0 in range(0, out_h, 256):
                j1 = min(j0 + 256, out_h)
                buf = np.full((j1 - j0, out_w), int(NODATA), dtype=np.int16)
                for bi, j in enumerate(range(j0, j1)):
                    lat = 90.0 - (j + 0.5) * dx
                    if not -85.05 < lat < 85.05:
                        continue
                    merc_y = 6378137.0 * math.log(math.tan(math.pi / 4.0 + math.radians(lat) / 2.0))
                    sy = (ORIGIN_SHIFT - merc_y) / (2 * ORIGIN_SHIFT) * h - 0.5
                    r0 = int(math.floor(sy))
                    fr = float(sy - r0)
                    a0 = _row(s, row_cache, r0, nod)
                    a1 = _row(s, row_cache, r0 + 1, nod)
                    val = (a0[c0] * (1 - fc) + a0[c1] * fc) * (1 - fr) \
                        + (a1[c0] * (1 - fc) + a1[c1] * fc) * fr
                    bad = (a0[c0] == nod) | (a0[c1] == nod) | (a1[c0] == nod) | (a1[c1] == nod)
                    val[bad] = int(NODATA)
                    buf[bi, :] = np.clip(np.rint(val), -32768, 32767).astype(np.int16)
                dst.write(buf, 1, window=Window(0, j0, out_w, j1 - j0))
                if (j0 // 256) % 8 == 0:
                    log("[重采样] 输出行 %d/%d 用时%.0f秒" % (j1, out_h, time.time() - t0))
        log("[重采样] 完成 %dx%d（%s弧秒）→ %s (%.1f MB)"
            % (out_w, out_h, cell_arcsec, out, os.path.getsize(out) / 1048576))
    return out


def _row(src, cache, r, nod):
    if r < 0 or r >= src.height:
        return np.full(src.width, nod, dtype=np.int16)
    if len(cache) > 3:
        cache.clear()
    if r not in cache:
        cache[r] = src.read(1, window=Window(0, r, src.width, 1))[0]
    return cache[r]


def step_validate(root, out_tif):
    vmin, vmax, vsum, vn, vbad, vpos = None, None, 0.0, 0, 0, 0
    with rasterio.open(out_tif) as d:
        nod = d.nodata
        crs = str(d.crs)
        shape = (int(d.width), int(d.height))
        for j0 in range(0, d.height, 512):
            j1 = min(j0 + 512, d.height)
            band = d.read(1, window=Window(0, j0, d.width, j1 - j0))
            good = band[band != nod].astype(np.float64)
            vbad += int(band.size - good.size)
            if good.size:
                lo, hi = float(good.min()), float(good.max())
                vmin = lo if vmin is None else min(vmin, lo)
                vmax = hi if vmax is None else max(vmax, hi)
                vsum += float(good.sum())
                vn += int(good.size)
                vpos += int((good > 0).sum())
            log("[校验] 行 %d/%d 已统计" % (j1, d.height))
    if not vn:
        raise SystemExit("[校验] 输出栅格全是无数据，检查上游瓦片")
    stats = {
        "file": os.path.abspath(out_tif),
        "width": shape[0], "height": shape[1],
        "cell_arcsec": 3600.0 * 180.0 / shape[1],
        "crs": crs,
        "valid_pixels": vn,
        "nodata_pixels": vbad,
        "min_m": vmin, "max_m": vmax,
        "mean_m": vsum / vn,
        "above_sea_pct": 100.0 * vpos / vn,
    }
    log("[校验] 有效像素 %d / 无数据 %d，高程范围 %.0f ~ %.0f m，均值 %.0f m，陆地占比 %.1f%%"
        % (stats["valid_pixels"], stats["nodata_pixels"], stats["min_m"], stats["max_m"],
           stats["mean_m"], stats["above_sea_pct"]))
    mp = os.path.join(root, "metadata.json")
    meta = {}
    if os.path.isfile(mp):
        try:
            meta = json.loads(open(mp, encoding="utf-8").read())
        except Exception:
            meta = {}
    meta["global_dem"] = stats
    with open(mp, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=2)
    log("[校验] 元数据写入 → %s" % mp)
    return stats


def main():
    ap = argparse.ArgumentParser(description="下载全球 DEM（terrarium 瓦片 -> GeoTIFF）")
    ap.add_argument("--root", default=r"D:\Project\wargame\data\dem")
    ap.add_argument("--z", type=int, default=7, help="瓦片金字塔层级，7≈1.2km，6≈2.4km")
    ap.add_argument("--workers", type=int, default=16)
    ap.add_argument("--cell-arcsec", type=int, default=30, help="输出网格分辨率（弧秒）")
    ap.add_argument("--probe", type=int, default=0, help="只下载 N 张瓦片测吞吐")
    ap.add_argument("--step", default="all", choices=["all", "download", "mosaic", "resample", "validate"])
    args = ap.parse_args()

    os.makedirs(args.root, exist_ok=True)
    t0 = time.time()
    if args.probe:
        step_download(args.root, args.z, args.workers, args.probe)
        return
    if args.step in ("all", "download"):
        step_download(args.root, args.z, args.workers, 0)
    src = os.path.join(args.root, "global_dem_z%d_webmercator.tif" % args.z)
    if args.step in ("all", "mosaic"):
        src = step_mosaic(args.root, args.z)
    out = src
    if args.step in ("all", "resample"):
        out = step_resample(args.root, src, args.cell_arcsec)
    if args.step in ("all", "validate"):
        step_validate(args.root, out)
    log("[完成] 总耗时 %.1f 分钟" % ((time.time() - t0) / 60))


if __name__ == "__main__":
    main()
