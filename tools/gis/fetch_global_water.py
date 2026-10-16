"""全球河流 + 湖泊矢量数据（≤100MB 方案）：Natural Earth 10m 水系。

选型记录（都是本机实测，不是猜的）：
  HydroRIVERS/HydroLAKES v10  3.6GB   —— 属性最全、密度最高，但超预算，已否决
  OSM 全球 planet             88.8GB  —— osm-pds 只有整块文件，Geofabrik 本机 connection refused
  HydroSHEDS 30s acc 栅格     196.5MB —— 可阈值化出高密度河网，但单个文件就超 100MB
  Natural Earth 10m 水系      ~7MB    —— 本脚本采用：河名/湖名 + scalerank + level 属性齐全

输出 data/water/global_water.gpkg，图层：
  rivers                 全球河流中心线（含湖内中心线），带 name/scalerank/level/多语言名
  rivers_scale_rank      同上的分级版（level 字段更完整）
  lakes / lakes_europe / lakes_north_america / lakes_historic  湖泊多边形
  lakes_all              上面几个湖泊图层合并（列取并集，来源记在 src_layer）

本机坑：requests/GDAL 走 schannel 会报证书链不完整，下载统一用 urllib.request；
Windows 控制台默认 GBK，日志重定向到文件前先切 stdout 到 UTF-8。

用法：
  python fetch_global_water.py               # 全流程：下载→转换→校验
  python fetch_global_water.py --step download
"""
import argparse
import glob
import json
import os
import shutil
import sys
import time
import urllib.request
import zipfile

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

UA = "wargame-water/1.0"
ROOT_DEFAULT = r"D:\Project\wargame\data\water"
MIRROR = "https://naturalearth.s3.amazonaws.com/10m_physical/%s.zip"
LAYERS = [
    # (输出图层名, Natural Earth 文件名)
    ("rivers", "ne_10m_rivers_lake_centerlines"),
    ("rivers_scale_rank", "ne_10m_rivers_lake_centerlines_scale_rank"),
    ("lakes", "ne_10m_lakes"),
    ("lakes_europe", "ne_10m_lakes_europe"),
    ("lakes_north_america", "ne_10m_lakes_north_america"),
    ("lakes_historic", "ne_10m_lakes_historic"),
]
LAKE_LAYERS = ["lakes", "lakes_europe", "lakes_north_america"]


def log(msg):
    print("[%s] %s" % (time.strftime("%m-%d %H:%M:%S"), msg), flush=True)


def download(url, dest, timeout=120):
    """整包下载；本地大小与远端一致则跳过（服务器支持 Range，长度可信）。"""
    req = urllib.request.Request(url, headers=dict({"User-Agent": UA}, Range="bytes=0-0"))
    with urllib.request.urlopen(req, timeout=60) as r:
        total = int((r.headers.get("Content-Range") or "/0").split("/")[-1])
    if os.path.isfile(dest) and os.path.getsize(dest) == total:
        log("[跳过] %s 已在本地（%.2f MB）" % (os.path.basename(dest), total / 1048576))
        return total
    with urllib.request.urlopen(
            urllib.request.Request(url, headers={"User-Agent": UA}), timeout=timeout) as r, \
            open(dest, "wb") as fh:
        got = 0
        while True:
            buf = r.read(1 << 18)
            if not buf:
                break
            fh.write(buf)
            got += len(buf)
    if got != total:
        raise SystemExit("%s 下载不完整：%d/%d 字节" % (dest, got, total))
    log("[下载] %s %.2f MB" % (os.path.basename(dest), got / 1048576))
    return total


def extract(zpath, outdir):
    os.makedirs(outdir, exist_ok=True)
    stem = os.path.splitext(os.path.basename(zpath))[0]
    hits = glob.glob(os.path.join(outdir, "**", stem + ".shp"), recursive=True)
    if hits:
        return hits[0]
    with zipfile.ZipFile(zpath) as zf:
        if zf.testzip():
            raise SystemExit("压缩包损坏：%s" % zpath)
        zf.extractall(outdir)
    hits = glob.glob(os.path.join(outdir, "**", stem + ".shp"), recursive=True)
    if not hits:
        raise SystemExit("解压后找不到 .shp：%s" % outdir)
    return hits[0]


def step_download(root):
    raw = os.path.join(root, "raw")
    os.makedirs(raw, exist_ok=True)
    total = 0
    for _layer, name in LAYERS:
        total += download(MIRROR % name, os.path.join(raw, name + ".zip"))
    log("[下载] 合计 %.2f MB" % (total / 1048576))


def step_convert(root, keep_src=False):
    import geopandas as gpd
    import pandas as pd
    from pyogrio import write_dataframe

    out = os.path.join(root, "global_water.gpkg")
    if os.path.isfile(out):
        os.remove(out)
    frames = {}
    for layer, name in LAYERS:
        shp = extract(os.path.join(root, "raw", name + ".zip"),
                      os.path.join(root, "src", name))
        gdf = gpd.read_file(shp)
        gdf["src_layer"] = layer
        write_dataframe(gdf, out, layer=layer, driver="GPKG")
        frames[layer] = gdf
        log("[转换] %-22s %6d 要素 %-14s %s"
            % (layer, len(gdf), gdf.crs.to_epsg(),
               dict(gdf.geometry.geom_type.value_counts())))
    merged = gpd.GeoDataFrame(
        pd.concat([frames[k] for k in LAKE_LAYERS], ignore_index=True),
        geometry="geometry", crs=frames["lakes"].crs)
    write_dataframe(merged, out, layer="lakes_all", driver="GPKG")
    log("[转换] %-22s %6d 要素（%s 合并）"
        % ("lakes_all", len(merged), "+".join(LAKE_LAYERS)))
    log("[转换] 输出 %s（%.2f MB）" % (out, os.path.getsize(out) / 1048576))
    if not keep_src:
        # 解压出的 shapefile 是纯中间产物（65MB），raw/*.zip 可随时重新解出
        shutil.rmtree(os.path.join(root, "src"), ignore_errors=True)
        log("[清理] 已删除中间 shapefile 目录 src/（保留 raw/*.zip）")
    return out


def step_validate(root):
    from pyogrio import list_layers, read_dataframe, read_info

    gpkg = os.path.join(root, "global_water.gpkg")
    meta = {"generated_at": time.strftime("%Y-%m-%d %H:%M:%S"), "file": gpkg,
            "size_mb": round(os.path.getsize(gpkg) / 1048576, 2),
            "source": "Natural Earth 10m 水系（naturalearth.s3.amazonaws.com 镜像）",
            "layers": {}}
    for name, geom in list_layers(gpkg):
        info = read_info(gpkg, layer=name, force_feature_count=True, force_total_bounds=True)
        count = info["features"]
        meta["layers"][name] = {
            "features": int(count), "geometry": geom, "crs": str(info["crs"]),
            "bounds": [round(v, 3) for v in info["total_bounds"]], "fields": list(info["fields"]),
        }
        log("[校验] %-22s %8d 要素 %-16s %s" % (name, count, geom, info["crs"]))

    rivers = read_dataframe(gpkg, layer="rivers")
    if "name" in rivers.columns:
        named = int(rivers["name"].notna().sum())
        log("[校验] 河流有名字：%d/%d（%.0f%%）" % (named, len(rivers), 100.0 * named / len(rivers)))
    for col in ("scalerank", "level"):
        if col in rivers.columns:
            log("[校验] 河流 %s 分布：%s" % (col, json.dumps(
                {str(k): int(v) for k, v in rivers[col].value_counts().sort_index().items()})))
    lakes = read_dataframe(gpkg, layer="lakes_all")
    if "scalerank" in lakes.columns:
        log("[校验] 湖泊 scalerank 分布：%s" % json.dumps(
            {str(k): int(v) for k, v in lakes["scalerank"].value_counts().sort_index().items()}))
    if "name" in lakes.columns:
        sample = lakes[lakes["name"].astype(str).str.contains(
            "Baikal|Victoria|Superior|Caspian", na=False, regex=True)]
        for _, row in sample.iterrows():
            log("[校验] 抽样湖泊：%-22s scalerank=%-3s 来源=%s"
                % (str(row["name"])[:22], row.get("scalerank"), row.get("src_layer")))
    meta["notes"] = {
        "usage": "gpd.read_file('global_water.gpkg', layer='rivers')；也可用 bbox 局部读取",
        "fields": "rivers: name/多语言名/scalerank/level/featurecla/diss_me；"
                  "lakes: name/admin/scalerank/featurecla",
        "density": "10m 比例尺=全球主要水系，细小河溪没有；要更密的河网需 "
                   "HydroSHEDS 30s acc 栅格（196.5MB，阈值化）或 HydroRIVERS v10（3.6GB）",
        "caveat": "lakes_historic 为干涸/季节性湖床，做通行性判断时不要当水面；"
                  "南极冰盖上的融水湖未收录",
    }
    with open(os.path.join(root, "metadata.json"), "w", encoding="utf-8") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=2)
    log("[校验] metadata.json 已写入")


def main():
    ap = argparse.ArgumentParser(description="全球河流湖泊矢量（Natural Earth 10m，≤100MB）")
    ap.add_argument("--root", default=ROOT_DEFAULT)
    ap.add_argument("--step", default="all", choices=["all", "download", "convert", "validate"])
    ap.add_argument("--keep-src", action="store_true",
                    help="保留解压出的 shapefile（约 65MB），默认转换完即删")
    args = ap.parse_args()
    os.makedirs(args.root, exist_ok=True)
    t0 = time.time()
    log("[开始] root=%s step=%s" % (args.root, args.step))
    if args.step in ("all", "download"):
        step_download(args.root)
    if args.step in ("all", "convert"):
        step_convert(args.root, keep_src=args.keep_src)
    if args.step in ("all", "validate"):
        step_validate(args.root)
    log("[结束] 用时 %.1f 秒" % (time.time() - t0))


if __name__ == "__main__":
    sys.exit(main())
