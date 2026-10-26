using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexFront.Core.GisTools;

/// <summary>
/// 按经纬度从一组 .ter 瓦片采样高程 / 土地覆盖。瓦片清单由 terrain_manifest.json 给出
/// （extract_terrain.py / ExtractTerrainCommand 的产物）。
/// 找不到覆盖瓦片或清单缺失时 Sample 返回 found=false（调用方降级合成 DEM）。
/// 完全无 GDAL 依赖，运行时只解析紧凑二进制 .ter + JSON 清单。
/// </summary>
public sealed class TerrainSampler
{
    private readonly string _dir;
    private readonly List<TileRef> _tiles = new();
    private readonly Dictionary<string, TerrainTile> _cache = new();

    private sealed class TileRef
    {
        public string File = "";
        public double West, South, East, North;
        public bool HasLandcover;
    }

    public TerrainSampler(string terrainDir)
    {
        _dir = terrainDir;
        LoadManifest();
    }

    /// <summary>是否有可用瓦片（清单存在且至少解析出一块）。</summary>
    public bool Available => _tiles.Count > 0;

    /// <summary>所有瓦片中是否至少有一块含土地覆盖层。</summary>
    public bool HasLandcover => _tiles.Any(t => t.HasLandcover);

    /// <summary>瓦片目录（调试/能力查询用）。</summary>
    public string Directory => _dir;

    private void LoadManifest()
    {
        var manifest = Path.Combine(_dir, "terrain_manifest.json");
        if (!File.Exists(manifest)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(manifest));
            if (node?["tiles"] is not JsonArray arr) return;
            foreach (var t in arr)
            {
                if (t?["file"]?.GetValue<string>() is not { } file) continue;
                _tiles.Add(new TileRef
                {
                    File = file,
                    West = (double)t["west"]!,
                    South = (double)t["south"]!,
                    East = (double)t["east"]!,
                    North = (double)t["north"]!,
                    HasLandcover = t["hasLandcover"]?.GetValue<bool>() ?? false,
                });
            }
        }
        catch
        {
            _tiles.Clear();
        }
    }

    private TerrainTile? Resolve(double lat, double lon)
    {
        foreach (var tr in _tiles)
        {
            if (lon >= tr.West && lon <= tr.East && lat >= tr.South && lat <= tr.North)
            {
                if (_cache.TryGetValue(tr.File, out var cached)) return cached;
                var path = Path.Combine(_dir, tr.File);
                if (!File.Exists(path)) return null;
                var tile = TerrainTile.Load(path);
                _cache[tr.File] = tile;
                return tile;
            }
        }
        return null;
    }

    /// <summary>
    /// 按经纬度采样。found=false 表示无覆盖瓦片（应降级合成 DEM）。
    /// 高程 nodata（-32768）会被如实返回，由调用方决定是否降级。
    /// </summary>
    public bool Sample(double lat, double lon, out short elevation, out byte landcover)
    {
        elevation = 0;
        landcover = 0;
        var tile = Resolve(lat, lon);
        if (tile == null) return false;
        if (!tile.TrySample(lat, lon, out elevation, out landcover)) return false;
        return true;
    }
}
