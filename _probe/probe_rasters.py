"""一次性探针：rasters.py 的真实栅格取样是否正确（对照现有 Open-Meteo 数据包）。"""
import json
import sys
import time
from collections import Counter

sys.path.insert(0, r"D:\Project\wargame\tools\gis")
from hexgen import HexGrid
import rasters

DEM = r"D:\Project\wargame\data\dem\global_dem_30s.tif"
pkg = json.load(open(r"D:\Project\wargame\data\gis\rhine_5km.json", encoding="utf-8"))
old = {(h["q"], h["r"]): h["elev"] for h in pkg["hexes"]}
g = HexGrid(6.95, 51.42, 26, 20, 5.0)

print("caps:", rasters.capabilities())
print("dem info:", rasters.raster_info(DEM))
t = time.time()
vals, info = rasters.dem_by_hex(DEM, g)
d = [vals[c] - old[c] for c in g.iter_cells()]
print("DEM  %.1fs info=%s" % (time.time() - t, info))
print("     均值差: mean %.1f m  max|d| %.0f m  本地 min %.0f max %.0f"
      % (sum(d) / len(d), max(abs(x) for x in d), min(vals.values()), max(vals.values())))

t = time.time()
lc, li = rasters.landcover_by_hex(g)
print("LC   %.1fs tiles=%s samples=%s" % (time.time() - t, li["tiles"], li["samples"]))
print("     众数分布:", dict(sorted(Counter(v["class"] for v in lc.values()).items())))
print("     平均占比:", round(sum(v["share"] for v in lc.values()) / len(lc), 3))
for c in [(0, 10), (13, 10), (18, 9), (7, 5)]:
    print("     hex", c, "lc=", lc[c], "elev local=%.0f om=%.0f" % (vals[c], old[c]))
