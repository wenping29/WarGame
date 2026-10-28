using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core.GisTools;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace HexFront.Api.Services;

/// <summary>
/// 真实 GIS 矢量叠层：按 bbox 裁剪 data/gis 下的矢量源，输出 EPSG:4326 GeoJSON 给前端画叠加层。
///
/// 为什么要单独一条路由：六角格网络包（/ws 的 view、data/hexgrids）只有格级布尔量
/// （road/river/railway…），画出来是「格心连成的折线」，形状是六角格自己造出来的。
/// 叠加真实矢量走的是另一条路：不进网络包、不动 /ws 协议，前端每次换图按当前格网 bbox 取一次。
///
/// 覆盖范围是实打实的边界：
///   · 水系/机场 = Natural Earth 10m 全球幅
///   · 路网 = roads.gdb 裁剪过（只存东亚 + 中欧两片，见 roads_pkg/roads_coverage.json），
///     范围外返回空图层并在 meta 里说明，不静默画一张没有路的图。
/// </summary>
public sealed class GisVectorService
{
    /// <summary>单层要素数上限：一个 5°×5° 的大 bbox 能命中上万条路网，护住浏览器与响应体。</summary>
    private const int MaxFeaturesPerLayer = 3000;
    /// <summary>单层坐标点上限，与要素数上限二选一先到为准。</summary>
    private const int MaxPointsPerLayer = 200_000;
    /// <summary>GeoJSON 坐标小数位：5 位 ≈ 1.1 m，比屏幕一个像素还细，再多是白占带宽。</summary>
    private const int CoordinatePrecision = 5;

    private readonly string _waterGpkg;
    private readonly string _roadsGpkg;
    private readonly string _airportsShp;
    private readonly List<double[]> _roadClips;

    /// <summary>图层键 → (中文名, 数据源相对路径)。键名即前端请求与 GeoJSON 图层名。</summary>
    public static readonly string[] LayerKeys = ["rivers", "lakes", "roads", "airports"];

    public GisVectorService(RoomService rooms)
    {
        var gis = rooms.GisDirectory;
        _waterGpkg = Path.Combine(gis, "water", "global_water.gpkg");
        _roadsGpkg = Path.Combine(gis, "roads_pkg", "roads.gpkg");
        _airportsShp = Path.Combine(gis, "airports", "ne_10m_airports.shp");
        _roadClips = ReadRoadClips(Path.Combine(gis, "roads_pkg", "roads_coverage.json"));
    }

    /// <summary>能力探测用：各图层数据源是否就位（缺文件不算错误，前端据此隐藏图层开关）。</summary>
    public JsonObject Capabilities()
    {
        var layers = new JsonObject
        {
            ["rivers"] = LayerInfo("rivers", "真实水系（Natural Earth 10m 河流中心线）", _waterGpkg, "全球幅"),
            ["lakes"] = LayerInfo("lakes", "真实湖泊（Natural Earth 10m 湖面多边形）", _waterGpkg, "全球幅"),
            ["roads"] = LayerInfo("roads", "真实路网（裁剪幅面）", _roadsGpkg, _roadClips.Count > 0 ? "裁剪幅面" : "全球幅"),
            ["airports"] = LayerInfo("airports", "真实机场点位（Natural Earth 10m）", _airportsShp, "全球幅"),
        };
        return new JsonObject { ["ok"] = true, ["layers"] = layers };
    }

    private JsonObject LayerInfo(string key, string label, string path, string extent)
    {
        var exists = File.Exists(path);
        return new JsonObject
        {
            ["available"] = exists,
            ["label"] = JsonValue.Create(label),
            ["source"] = JsonValue.Create(exists ? Path.GetRelativePath(Path.GetDirectoryName(path)!, path) : null),
            ["extent"] = JsonValue.Create(extent),
        };
    }

    /// <summary>
    /// 取 bbox 内的矢量叠层。simplify 是「按像素抽稀」的档位（0=原始几何，1 默认，越大越粗）：
    /// NE 10m 的线在 5km/格的视野下顶点密度远超屏幕分辨率，不抽稀时一条长江就几十 KB。
    /// </summary>
    public JsonObject Query(double west, double south, double east, double north, IReadOnlyList<string> layers, double simplifyPx)
    {
        var bbox = new Envelope(west, east, south, north);
        // 抽稀容差按「1 像素 ≈ bbox 跨度 / 1200」换算成度；0 表示不抽稀
        var span = Math.Max(bbox.Width, bbox.Height);
        var tolerance = simplifyPx > 0 ? span / 1200.0 * simplifyPx : 0.0;
        var clipBox = GeometryFactory.Default.CreatePolygon(
            new[]
            {
                new Coordinate(west, south), new Coordinate(east, south),
                new Coordinate(east, north), new Coordinate(west, north),
                new Coordinate(west, south),
            });

        var outLayers = new JsonObject();
        var meta = new JsonObject();
        foreach (var key in layers)
        {
            var (features, m) = key switch
            {
                "rivers" => LinesFromGpkg(_waterGpkg, "rivers", bbox, clipBox, tolerance, NameOf),
                "lakes" => LinesFromGpkg(_waterGpkg, "lakes_all", bbox, clipBox, tolerance, NameOf),
                "roads" => LinesFromGpkg(_roadsGpkg, "roads", bbox, clipBox, tolerance, RoadNameOf),
                "airports" => PointsFromShapefile(_airportsShp, bbox),
                _ => (null, Missing(key)),
            };
            // 路网是裁剪幅面，范围外的空结果必须说清楚：否则前端看起来像「这一带真没路」。
            // 有要素返回时不报——跨裁剪框边界的道路会保留框外那一段顶点，那是真实几何不是越界。
            if (key == "roads" && m["available"]?.GetValueKind() == JsonValueKind.True
                && m["matched"]?.GetValue<int>() == 0 && !RoadsCovered(west, south, east, north))
                m["note"] = JsonValue.Create("该范围不在真实路网裁剪幅面内（本机只存东亚 + 中欧两片）");
            outLayers[key] = features;
            meta[key] = m;
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["crs"] = "EPSG:4326",
            ["bbox"] = new JsonArray(west, south, east, north),
            ["simplify_px"] = simplifyPx,
            ["layers"] = outLayers,
            ["meta"] = meta,
        };
    }

    // ==================== 各几何类型 ====================

    private static JsonObject Missing(string key) => new()
    {
        ["available"] = false,
        ["note"] = $"未知图层 '{key}'（可用：{string.Join(",", LayerKeys)}）",
    };

    /// <summary>线/面图层：rtree 粗筛 → 按 bbox 切边 → 抽稀 → GeoJSON。</summary>
    private (JsonObject?, JsonObject) LinesFromGpkg(
        string path, string layer, Envelope bbox, NtsGeometry clipBox, double tolerance,
        Func<GpkgFeature, Dictionary<string, JsonNode?>> attrsOf)
    {
        if (!File.Exists(path))
            return (null, new JsonObject { ["available"] = false, ["note"] = "数据文件不存在" });

        var features = new JsonArray();
        int matched = 0, emitted = 0, points = 0;
        bool truncated = false;
        try
        {
            using var gp = GeoPackageReader.Open(path);
            // rtree 粗筛在库里做，命中数与渲染数分开报：便于判断「这个范围真没数据」还是「被上限截断」
            foreach (var f in gp.ReadLayer(layer, bbox).Features)
            {
                matched++;
                if (emitted >= MaxFeaturesPerLayer || points >= MaxPointsPerLayer) { truncated = true; continue; }
                var geom = Prepare(f.Geometry, clipBox, tolerance);
                if (geom == null || geom.NumGeometries == 0) continue;
                var coords = CoordArrays(geom, CoordinatePrecision);
                if (coords == null) continue;
                points += geom.NumPoints;
                emitted++;
                features.Add(new JsonObject
                {
                    ["type"] = "Feature",
                    ["geometry"] = new JsonObject
                    {
                        ["type"] = JsonValue.Create(GeoJsonTypeOf(geom)),
                        ["coordinates"] = coords,
                    },
                    ["properties"] = PropertiesNode(attrsOf(f)),
                });
            }
        }
        catch (Exception ex)
        {
            return (null, new JsonObject { ["available"] = false, ["note"] = $"读取失败：{ex.Message}" });
        }

        var fc = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features };
        return (fc, new JsonObject
        {
            ["available"] = true,
            ["matched"] = matched,
            ["features"] = emitted,
            ["points"] = points,
            ["truncated"] = truncated,
        });
    }

    /// <summary>点图层（机场）：逐点判定是否在 bbox 内，不做切边。</summary>
    private static (JsonObject?, JsonObject) PointsFromShapefile(string path, Envelope bbox)
    {
        if (!File.Exists(path))
            return (null, new JsonObject { ["available"] = false, ["note"] = "数据文件不存在" });

        var features = new JsonArray();
        int matched = 0, emitted = 0;
        try
        {
            var shp = ShapefileReader.Open(Path.ChangeExtension(path, null)!);
            var fields = shp.Fields;
            int iName = fields.IndexOf("name"), iNameEn = fields.IndexOf("name_en"),
                iNameZh = fields.IndexOf("name_zh"), iType = fields.IndexOf("type"),
                iIata = fields.IndexOf("iata_code"), iClass = fields.IndexOf("featurecla");
            foreach (var f in shp.Features)
            {
                if (f.Geometry is not Point p) continue;
                if (!bbox.Intersects(p.EnvelopeInternal)) continue;
                matched++;
                if (emitted >= MaxFeaturesPerLayer) break;
                emitted++;
                var attrs = new Dictionary<string, JsonNode?>
                {
                    ["name"] = Pick(f, iNameZh) ?? Pick(f, iName) ?? Pick(f, iNameEn),
                    ["type"] = Pick(f, iType),
                    ["iata"] = Pick(f, iIata),
                    ["feature"] = Pick(f, iClass),
                };
                features.Add(new JsonObject
                {
                    ["type"] = "Feature",
                    ["geometry"] = new JsonObject
                    {
                        ["type"] = "Point",
                        ["coordinates"] = Pos(p.Coordinate, CoordinatePrecision),
                    },
                    ["properties"] = PropertiesNode(attrs),
                });
            }
        }
        catch (Exception ex)
        {
            return (null, new JsonObject { ["available"] = false, ["note"] = $"读取失败：{ex.Message}" });
        }

        var fc = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features };
        return (fc, new JsonObject { ["available"] = true, ["matched"] = matched, ["features"] = emitted });
    }

    // ==================== 几何 → GeoJSON ====================

    /// <summary>切到 bbox 内 + 按容差抽稀。NE 数据里有自相交面，交集运算偶尔抛拓扑异常，退化成不切边。</summary>
    private static NtsGeometry? Prepare(NtsGeometry geom, NtsGeometry clipBox, double tolerance)
    {
        NtsGeometry g = geom;
        try
        {
            if (!clipBox.EnvelopeInternal.Intersects(g.EnvelopeInternal)) return null;
            g = g.Intersection(clipBox);
        }
        catch
        {
            g = geom.Copy();
        }
        if (g.IsEmpty) return null;
        if (tolerance > 0 && g.NumPoints > 8)
        {
            try
            {
                var s = DouglasPeuckerSimplifier.Simplify(g, tolerance);
                if (s != null && !s.IsEmpty && s.NumGeometries > 0) g = s;
            }
            catch { /* 抽稀失败就用原几何，宁可大一点也不要少画 */ }
        }
        return g;
    }

    private static string GeoJsonTypeOf(NtsGeometry g) => g switch
    {
        Point => "Point",
        LineString => "LineString",
        Polygon => "Polygon",
        MultiPoint => "MultiPoint",
        MultiLineString => "MultiLineString",
        MultiPolygon => "MultiPolygon",
        GeometryCollection => "GeometryCollection",
        _ => "GeometryCollection",
    };

    /// <summary>
    /// 按 GeoJSON 规格搭 coordinates 嵌套数组。这里不复用 NTS 的 Coordinates 平铺结果，
    /// 因为线/面的层级（ring、polygon、multi）必须由几何类型决定，否则前端连笔画会跨环跳线。
    /// </summary>
    private static JsonNode? CoordArrays(NtsGeometry g, int precision) => g switch
    {
        Point p => Pos(p.Coordinate, precision),
        MultiPoint mp => new JsonArray(mp.Geometries.Select(x => (JsonNode)Pos(x.Coordinate, precision)).ToArray()),
        LineString => Round(g.Coordinates, precision),
        Polygon poly => Rings(poly, precision),
        MultiLineString ml => new JsonArray(ml.Geometries.Select(x => (JsonNode)Round(x.Coordinates, precision)!).ToArray()),
        MultiPolygon mpoly => new JsonArray(mpoly.Geometries.Select(x => (JsonNode)Rings((Polygon)x, precision)).ToArray()),
        GeometryCollection gc => new JsonArray(gc.Geometries.Select(x => CoordArrays(x, precision)!).ToArray()),
        _ => null,
    };

    private static JsonArray Rings(Polygon poly, int precision)
    {
        var rings = new JsonArray { Round(poly.Shell.Coordinates, precision) };
        for (int i = 0; i < poly.NumInteriorRings; i++)
            rings.Add(Round(poly.GetInteriorRingN(i).Coordinates, precision));
        return rings;
    }

    /// <summary>
    /// 坐标数组 → GeoJSON 位置列表 [[经,纬],…]。必须是「成对的嵌套数组」：
    /// 把 x、y 平铺进同一个数组不合法（解析方会当成 N 维坐标），前端按 coordinates[i][0]
    /// 取值时会拿到数字而不是数组。
    /// </summary>
    private static JsonArray Round(Coordinate[] cs, int precision)
    {
        var arr = new JsonArray();
        foreach (var c in cs) arr.Add(Pos(c, precision));
        return arr;
    }

    private static JsonArray Pos(Coordinate c, int precision) => new()
    {
        Math.Round(c.X, precision, MidpointRounding.AwayFromZero),
        Math.Round(c.Y, precision, MidpointRounding.AwayFromZero),
    };

    private static JsonObject PropertiesNode(Dictionary<string, JsonNode?> attrs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in attrs)
            if (v != null) o[k] = v;
        return o;
    }

    // ==================== 属性取值 ====================

    private static Dictionary<string, JsonNode?> NameOf(GpkgFeature f) => new()
    {
        ["name"] = Str(f, "name_zh") ?? Str(f, "name_en") ?? Str(f, "name"),
        ["scalerank"] = Num(f, "scalerank"),
        ["feature"] = Str(f, "featurecla"),
    };

    private static Dictionary<string, JsonNode?> RoadNameOf(GpkgFeature f) => new()
    {
        ["name"] = Str(f, "Name1"),
        ["type"] = Str(f, "TYPE"),
        ["rank"] = Num(f, "RANK"),
    };

    private static JsonNode? Str(GpkgFeature f, string key) =>
        f.Attributes.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s)
            ? JsonValue.Create(s) : null;

    private static JsonNode? Num(GpkgFeature f, string key)
    {
        if (!f.Attributes.TryGetValue(key, out var v) || v == null) return null;
        try { return JsonValue.Create(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
        catch { return JsonValue.Create(v.ToString()); }
    }

    private static string? Pick(ShpFeature f, int index) =>
        index >= 0 && index < f.Attributes.Length && !string.IsNullOrWhiteSpace(f.Attributes[index])
            ? f.Attributes[index] : null;

    // ==================== 覆盖范围 ====================

    /// <summary>读 roads_coverage.json 里的裁剪幅面；文件缺失就当全球幅（不做假告警）。</summary>
    private static List<double[]> ReadRoadClips(string path)
    {
        var list = new List<double[]>();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["clips"] is JsonArray arr)
                foreach (var c in arr)
                    if (c is JsonArray box && box.Count == 4)
                        list.Add(box.Select(x => x!.GetValue<double>()).ToArray());
        }
        catch
        {
            /* 没有清单就当作全幅可用 */
        }
        return list;
    }

    /// <summary>bbox 是否落在路网裁剪幅面内（有一点相交即算覆盖）。</summary>
    public bool RoadsCovered(double west, double south, double east, double north)
    {
        if (_roadClips.Count == 0) return true;
        return _roadClips.Any(c => west <= c[2] && east >= c[0] && south <= c[3] && north >= c[1]);
    }
}
