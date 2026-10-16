"""一次性探针：本地 30 弧秒 DEM 与 ESA WorldCover COG 能否按六角格中心取样。"""
import json
import sys
import time
from collections import Counter

sys.path.insert(0, r"D:\Project\wargame\tools\gis")
import numpy as np
import rasterio
from rasterio.windows import from_bounds
from hexgen import HexGrid, compute_slopes, percentile

DEM = r"D:\Project\wargame\data\dem\global_dem_30s.tif"
WC = "https://esa-worldcover.s3.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{t}_Map.tif"

pkg = json.load(open(r"D:\Project\wargame\data\gis\rhine_5km.json", encoding="utf-8"))
old_elev = [h["elev"] for h in pkg["hexes"]]
g = HexGrid(6.95, 51.42, 26, 20, 5.0)
cells = list(g.iter_cells())

t = time.time()
with rasterio.open(DEM) as src:
    pts = [(g.center_lonlat(c)[1], g.center_lonlat(c)[0]) for c in cells]
    dem = np.array([v[0] for v in src.sample(pts)], dtype=float)
print("DEM  %.1fs  n=%d  min %.0f max %.0f median %.0f nodata %d"
      % (time.time() - t, len(dem), dem.min(), dem.max(), np.median(dem), int((dem < -30000).sum())))
print("     open-meteo 包: min %.0f max %.0f median %.0f" % (min(old_elev), max(old_elev), np.median(old_elev)))
sl = compute_slopes(g, list(dem))
print("     30s-DEM 坡度: %.2f-%.2f deg, median %.2f, p93 %.2f" % (min(sl), max(sl), percentile(sl, .5), percentile(sl, .93)))
sl_old = compute_slopes(g, old_elev)
print("     OM   -DEM 坡度: %.2f-%.2f deg, median %.2f, p93 %.2f" % (min(sl_old), max(sl_old), percentile(sl_old, .5), percentile(sl_old, .93)))

bb = g.bbox()
print("bbox", [round(v, 3) for v in bb])
for tile in ["N51E006", "N48E006"]:
    try:
        t = time.time()
        with rasterio.open(WC.format(t=tile)) as src:
            w = from_bounds(bb[0], bb[1], bb[2], bb[3], src.transform)
            w = w.round_lengths_op().crop(0, 0, src.width, src.height)
            a = src.read(1, window=w, masked=True)
            cnt = Counter(int(v) for v in np.asarray(a).compressed())
        print("WC %s %.1fs shape=%s classes=%s" % (tile, time.time() - t, a.shape, dict(sorted(cnt.items()))))
    except Exception as e:
        print("WC %s FAIL %s: %s" % (tile, type(e).__name__, str(e)[:160]))
