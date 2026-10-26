# 地形 TIF 提取工具（无 GDAL）

把 GeoTIFF（高程 DEM / 土地覆盖 landcover）提取为 C# 项目可直接读取的紧凑二进制
地形瓦片（`.ter`），运行时**完全不依赖 Python / GDAL**。

> 设计目标：服务端 C# 按 WGS84 经纬度实时取样高程与土地覆盖，避免每次请求都调 Python
> 脚本（原 `hexgen.py` + `rasters.py` 依赖 rasterio/GDAL）。本工具只用
> `tifffile` + `numpy`（纯 Python，pip 可装，无原生 GDAL 依赖）。

## 依赖

```bash
pip install tifffile numpy
```

`tifffile` 负责解析 GeoTIFF 地理参考标签（ModelTiepointTag / ModelPixelScaleTag /
ModelTransformationTag）；像素解码对「256×256 瓦片 + DEFLATE + predictor 2」做了手写
解码（`GeoBand._decode_block`），已与 tifffile 原生解码逐字节校验一致。

## 用法

```bash
# 小区域（调试 / 单测），bbox 为 WGS84 度：西 南 东 北
python extract_terrain.py --mode region --bbox 4 48 9 51.5 \
    --out ../data/terrain_test --name rhein

# 全球切瓦片（一次性离线生成，默认按 10°×10°）
python extract_terrain.py --mode global --tile-deg 10 \
    --out ../data/terrain
```

源文件默认指向 `server/data/gis/dem/global_dem_30s.tif` 与
`server/data/gis/landcover/global_landcover_0p01.tif`；可用 `--dem` / `--landcover`
覆盖。

输出：
- `region`：`{out}/{name}.ter` + `terrain_manifest.json`
- `global`：`{out}/NxxExxx.ter`（每瓦片含高程 + 土地覆盖两层）+ `terrain_manifest.json`

## `.ter` 文件格式（小端 / little-endian，固定 80 字节头）

| offset | size | field |
|--------|------|-------|
| 0  | 4  | magic  ASCII `TER1` |
| 4  | 4  | int32  version (=1) |
| 8  | 4  | int32  crs (EPSG，如 4326) |
| 12 | 8  | float64 west (°E) |
| 20 | 8  | float64 south (°N) |
| 28 | 8  | float64 east (°E) |
| 36 | 8  | float64 north (°N) |
| 44 | 4  | int32  cols (经度方向列数) |
| 48 | 4  | int32  rows (纬度方向行数，row 0 在 north) |
| 52 | 8  | float64 resLon (°/列) |
| 60 | 8  | float64 resLat (°/行，正值) |
| 68 | 4  | int32  hasLandcover (0/1) |
| 72 | 2  | int16  elevNoData |
| 74 | 1  | uint8  lcNoData |
| 75 | 5  | padding (0) |

头部之后：
- `int16[rows*cols]` 高程数组（行优先，row0=north）
- 若 `hasLandcover=1`：`uint8[rows*cols]` 土地覆盖（已按最邻近重采样到与高程同一网格）

坐标换算（C# / Python 端一致）：
```
col = floor((lon - west) / resLon)
row = floor((north - lat) / resLat)
elev = int16[col + row*cols];  lc = uint8[col + row*cols]
```

## C# 消费方式

读取器位于 `server/HexFront.Api/src/Services/TerrainTileReader.cs`：

```csharp
var tile = TerrainTile.Load(@"data/terrain/N50E000.ter");
if (tile.TrySample(lat, lon, out short elev, out byte lc))
{
    // elev: 高程（米），lc: ESA 土地覆盖类别
}
```

- `Contains(lat, lon)`：判断坐标是否落在瓦片范围内
- 运行时先用 `terrain_manifest.json` 按经纬度定位对应的 `.ter` 文件再 `Load`
- 端到端校验：`HexFront.Tests/src/Gis/TerrainTileReaderTests.cs`
  （golden 固件 `rhein.ter` 由本工具生成，C# 回读与 Python 提取逐点一致）

## 备注

- 高程 nodata = `-32768`，土地覆盖 nodata = `0`，C# 端据此判定「无数据」
- 全球 10° 瓦片约 648 块；每块 1200×1200（DEM 0.00833° ≈ 926m），单块约 4MB
- 本工具与既有 `hexgen.py` 流水线互不冲突，可作为 C# 直读地形的替代数据源
