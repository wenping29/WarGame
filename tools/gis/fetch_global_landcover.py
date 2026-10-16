#!/usr/bin/env python3
"""全球土地覆盖（ESA WorldCover 2021 v200）概览装配：远程 COG -> 全球 ~1km 栅格。

数据源：s3://esa-worldcover/v200/2021/map/ 的 2651 个 3°x3° 原生 10m COG（全量 115.5GB）。
本脚本不下载原图，只用 HTTP Range 读每块瓦片的内部概览（最大 decimation=64，约 640m），
再抽稀到目标网格，拼成全球镶嵌。类别码在概览里保持整数，不做均值混合。

  python fetch_global_landcover.py --probe 5     # 只处理 5 块瓦片，验证链路与耗时
  python fetch_global_landcover.py               # 全量装配（可中断续跑）
  python fetch_global_landcover.py --step validate

TLS 说明：本机 GDAL 的 curl(schannel) 与 requests 对 *.s3.amazonaws.com 证书链校验都失败，
只有标准库 urllib 能过，所以远程随机读走 RangeFile。

分类码：10 林地 20 灌木 30 草地 40 耕地 50 建设用地 60 裸土/稀疏植被 70 冰雪
        80 永久水体 90 草本湿地 95 红树林 97 苔藓地衣 0 无数据（海洋/未覆盖）
"""

import argparse
import concurrent.futures
import io
import json
import os
import re
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

import numpy as np
import rasterio
from rasterio.transform import from_bounds
from rasterio.windows import Window

LIST_URL = ("https://esa-worldcover.s3.amazonaws.com/?list-type=2&max-keys=1000&prefix={prefix}")
OBJ_URL = "https://esa-worldcover.s3.amazonaws.com/{key}"
PREFIX = "v200/2021/map/"
CHUNK = 1 << 20  # RangeFile 每次预取 1MB，避免 GDAL 逐小块读造成海量请求
TILE_DEG = 3.0

CLASS_NAMES = {
    10: "Tree cover", 20: "Shrubland", 30: "Grassland", 40: "Cropland", 50: "Built-up",
    60: "Bare/sparse vegetation", 70: "Snow and ice", 80: "Permanent water bodies",
    90: "Herbaceous wetland", 95: "Mangroves", 97: "Moss and lichen",
}

_print_lock = threading.Lock()


def log(msg):
    with _print_lock:
        sys.stdout.write(msg + "\n")
        sys.stdout.flush()


class RangeFile(io.RawIOBase):
    """HTTPS 随机读文件对象：GDAL 的 https/vsicurl 在本机证书链校验失败，改用 urllib 做 Range 请求。"""

    def __init__(self, url, timeout=90):
        self.url = url
        self.pos = 0
        self.cache_off = None
        self.cache = b""
        req = urllib.request.Request(url, headers={"Range": "bytes=0-0", "User-Agent": "wargame-lulc/1.0"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            rng = r.headers.get("Content-Range", "")
            self.size = int(rng.split("/")[-1]) if "/" in rng else 0
        if not self.size:
            raise RuntimeError("服务器不支持 Range 请求：%s" % url)

    def readable(self):
        return True

    def seekable(self):
        return True

    def seek(self, off, whence=0):
        self.pos = {0: off, 1: self.pos + off, 2: self.size + off}[whence]
        return self.pos

    def tell(self):
        return self.pos

    def read(self, k=-1):
        if self.pos >= self.size:
            return b""
        k = self.size - self.pos if k is None or k < 0 else min(k, self.size - self.pos)
        if self.cache_off is None or not (self.cache_off <= self.pos < self.cache_off + len(self.cache)):
            end = min(self.size - 1, self.pos + CHUNK - 1)
            req = urllib.request.Request(
                self.url, headers={"Range": "bytes=%d-%d" % (self.pos, end),
                                   "User-Agent": "wargame-lulc/1.0"})
            with urllib.request.urlopen(req, timeout=90) as r:
                self.cache = r.read()
            self.cache_off = self.pos
        avail = self.cache_off + len(self.cache) - self.pos
        data = self.cache[:min(k, avail)]
        self.pos += len(data)
        return data


def list_tiles(prefix=PREFIX):
    keys, token = [], None
    while True:
        u = LIST_URL.format(prefix=prefix)
        if token:
            u += "&continuation-token=" + urllib.parse.quote(token, safe="")
        with urllib.request.urlopen(u, timeout=90) as r:
            x = r.read().decode()
        keys += [k for k in re.findall(r"<Key>([^<]+)</Key>", x) if k.endswith("_Map.tif")]
        m = re.search(r"<NextContinuationToken>([^<]+)</NextContinuationToken>", x)
        if not m:
            return keys
        token = m.group(1)


def fetch_tile(key, cell_deg):
    """读一块瓦片的目标网格抽稀结果；失败时重试，返回 (row0, col0, rows, cols, array)。"""
    url = OBJ_URL.format(key=key)
    last = None
    for attempt in range(4):
        try:
            rf = RangeFile(url)
            with rasterio.open(rf) as d:
                b = d.bounds
                cols = int(round((b.right - b.left) / cell_deg))
                rows = int(round((b.top - b.bottom) / cell_deg))
                arr = d.read(1, out_shape=(rows, cols))
                col0 = int(round((b.left + 180.0) / cell_deg))
                row0 = int(round((90.0 - b.top) / cell_deg))
            return row0, col0, rows, cols, np.asarray(arr, dtype=np.uint8)
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                log("[跳过] 瓦片不存在 %s" % key)
                return None
            last = exc
            time.sleep(2.0 * (attempt + 1))
        except Exception as exc:  # noqa: BLE001 - 远端任何抖动都退避重试
            last = exc
            time.sleep(2.0 * (attempt + 1))
    raise RuntimeError("瓦片 %s 读取失败：%s" % (key, last))


def fetch_job(args):
    key, cell_deg = args
    return key, fetch_tile(key, cell_deg)


def state_path(root):
    return os.path.join(root, "landcover_progress.json")


def step_build(root, out_tif, cell_deg, workers, total):
    tiles = list_tiles()
    log("[列表] %s 共 %d 块 3°x3° 瓦片" % (PREFIX, len(tiles)))
    if total:
        tiles = tiles[:total]
    grid_w = int(round(360.0 / cell_deg))
    grid_h = int(round(180.0 / cell_deg))
    st = state_path(root)
    done = set()
    resume = os.path.isfile(out_tif) and os.path.isfile(st)
    if resume:
        done = set(json.load(open(st, encoding="utf-8")).get("done", []))
        log("[续跑] 已完成 %d 块，跳过" % len(done))
    else:
        opts = dict(driver="GTiff", width=grid_w, height=grid_h, count=1, dtype="uint8",
                    crs=rasterio.CRS.from_epsg(4326),
                    transform=from_bounds(-180.0, -90.0, 180.0, 90.0, grid_w, grid_h),
                    nodata=0, tiled=True, blockxsize=256, blockysize=256,
                    compress="DEFLATE", predictor=2)
        with rasterio.open(out_tif, "w", **opts) as dst:
            pass
        log("[创建] 目标栅格 %dx%d（%.3f°/像素）→ %s" % (grid_w, grid_h, cell_deg, out_tif))

    todo = [k for k in tiles if k not in done]
    t0 = time.time()
    nbytes = 0
    with rasterio.open(out_tif, "r+") as dst, \
            concurrent.futures.ProcessPoolExecutor(max_workers=workers) as pool:
        for i, (key, res) in enumerate(pool.map(fetch_job, [(k, cell_deg) for k in todo])):
            if res is not None:
                row0, col0, rows, cols, arr = res
                r1 = max(0, row0)
                c1 = max(0, col0)
                sub = arr[r1 - row0: min(rows, grid_h - row0), c1 - col0: min(cols, grid_w - col0)]
                if sub.size:
                    dst.write(sub, 1, window=Window(c1, r1, sub.shape[1], sub.shape[0]))
                nbytes += sub.nbytes
            done.add(key)
            if (i + 1) % 100 == 0 or i + 1 == len(todo):
                el = time.time() - t0
                rate = (i + 1) / el if el else 0
                eta = (len(todo) - i - 1) / rate if rate else 0
                log("[装配] %d/%d 累计 %.1f MB 速率%.2f块/秒 预计剩余%.1f分钟"
                    % (len(done), len(tiles), nbytes / 1048576.0, rate, eta / 60))
                json.dump({"done": sorted(done)}, open(st, "w", encoding="utf-8"))
    json.dump({"done": sorted(done)}, open(st, "w", encoding="utf-8"))
    log("[装配] 完成 %d/%d 块，用时 %.1f 分钟" % (len(done), len(tiles), (time.time() - t0) / 60))


def step_validate(root, out_tif, cell_deg):
    counts = np.zeros(256, dtype=np.int64)
    with rasterio.open(out_tif) as d:
        for j0 in range(0, d.height, 512):
            j1 = min(j0 + 512, d.height)
            band = d.read(1, window=Window(0, j0, d.width, j1 - j0))
            bc = np.bincount(band.ravel(), minlength=256)
            counts += bc
            if (j0 // 512) % 6 == 0:
                log("[校验] 行 %d/%d" % (j1, d.height))
        crs, bounds = str(d.crs), list(d.bounds)
    covered = int(counts[1:].sum())
    classes = {}
    for c, name in CLASS_NAMES.items():
        classes[str(c)] = {"name": name, "pixels": int(counts[c]),
                           "pct_of_classified": round(100.0 * counts[c] / covered, 2) if covered else 0.0}
    log("[校验] 分类像素 %d，无数据 %d" % (covered, int(counts[0])))
    for c in sorted(CLASS_NAMES):
        log("   %2d %-24s %12d px  %5.2f%%" % (c, CLASS_NAMES[c], counts[c], classes[str(c)]["pct_of_classified"]))
    mp = os.path.join(root, "metadata.json")
    meta = {}
    if os.path.isfile(mp):
        try:
            meta = json.load(open(mp, encoding="utf-8"))
        except Exception:
            meta = {}
    meta["global_landcover"] = {
        "file": os.path.abspath(out_tif), "crs": crs, "bounds": bounds,
        "cell_deg": cell_deg, "approx_resolution_km": round(111.32 * cell_deg, 2),
        "width": int((bounds[2] - bounds[0]) / cell_deg), "height": int((bounds[3] - bounds[1]) / cell_deg),
        "nodata": 0, "classes": classes,
        "source": {"name": "ESA WorldCover 10m 2021 v200", "prefix": PREFIX,
                   "license": "CC BY 4.0 (open, attribution required)",
                   "note": "概览抽稀装配，非原生 10m；原生全量 115.5GB 未下载"},
    }
    json.dump(meta, open(mp, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    log("[校验] 元数据写入 → %s" % mp)


def main():
    ap = argparse.ArgumentParser(description="全球土地覆盖概览装配（ESA WorldCover v200）")
    ap.add_argument("--root", default=r"D:\Project\wargame\data\landcover")
    ap.add_argument("--cell-deg", type=float, default=0.01, help="目标网格（度），0.01≈1.1km")
    ap.add_argument("--workers", type=int, default=12)
    ap.add_argument("--probe", type=int, default=0)
    ap.add_argument("--step", default="all", choices=["all", "build", "validate"])
    args = ap.parse_args()
    os.makedirs(args.root, exist_ok=True)
    out = os.path.join(args.root, "global_landcover_%s.tif" % ("%g" % args.cell_deg).replace(".", "p"))
    if args.step in ("all", "build"):
        step_build(args.root, out, args.cell_deg, args.workers, args.probe)
    if args.probe:
        log("[探测] 前 %d 块已装配，用时另计；正式跑请去掉 --probe" % args.probe)
        return
    if args.step in ("all", "validate"):
        step_validate(args.root, out, args.cell_deg)


if __name__ == "__main__":
    main()
