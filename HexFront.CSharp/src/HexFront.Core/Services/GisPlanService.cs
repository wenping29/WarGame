using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using HexFront.Core.Geometry;

namespace HexFront.Core.Services;

/// <summary>
/// 格网方案：推荐对边距 + 档位阶梯。与 tools/gis/hexgen.py 的 plan_payload/grid_from_bbox 同源口径，
/// 生成侧（BuildGrid）也走 GridSize，保证「向导显示的格数」＝「真正生成的格数」。
/// </summary>
public static class GisPlanService
{
    /// <summary>单次生成的格数上限：超过就把前端和结算都拖垮（设计文档 §6.2 格距与规模）。</summary>
    public const int MaxCells = 30000;

    public const int TargetCells = 2400;   // 一次交战的可视战场
    public const int MinSide = 12;         // 短边至少 12 格，否则机动没有纵深
    public const double DemFloorKm = 1.0;  // 本地 DEM 30″≈0.93 km，格距再小也不会更「真实」
    public const int MaxBboxSpanDeg = 8;   // 单边跨度上限
    public const int LadderMinSide = 8;    // 生成侧的兜底短边（与 hexgen grid_from_bbox 一致）

    public static readonly double[] CellLadderKm =
        { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 5.0, 7.5, 10.0, 15.0, 20.0 };

    private const double HalfCircumferenceM = Mercator.HalfCircumference;

    public static double MercatorScale(double latDeg)
    {
        var clamped = Math.Clamp(latDeg, -85.0, 85.0) * Math.PI / 180.0;
        return 1.0 / Math.Max(0.05, Math.Cos(clamped));
    }

    /// <summary>WGS84 四至 → 实地东西/南北公里（墨卡托放大的逆运算）。</summary>
    public static (double WidthKm, double HeightKm) BboxGroundKm(double[] bbox)
    {
        var clat = (bbox[1] + bbox[3]) / 2.0 * Math.PI / 180.0;
        var w = (bbox[2] - bbox[0]) * 111.320 * Math.Cos(clat);
        var h = (bbox[3] - bbox[1]) * 110.574;
        return (Math.Max(w, 1e-6), Math.Max(h, 1e-6));
    }

    public static string FmtKm(double v) => F(v >= 10 ? "F0" : "F1", v);

    /// <summary>
    /// 格网中心：经度取算术中点，纬度取**墨卡托中点**（与 hexgen.grid_from_bbox 一致）。
    /// 51°N 处两者差几十米，但 meta.center_lonlat 要和生成侧逐位对得上，不能各算各的。
    /// </summary>
    public static (double CenterLon, double CenterLat) GridCenter(double[] bbox)
    {
        var (x0, y0) = Mercator.LonLatToMercator(bbox[0], bbox[1]);
        var (x1, y1) = Mercator.LonLatToMercator(bbox[2], bbox[3]);
        var (lon, lat) = Mercator.MercatorToLonLat((x0 + x1) / 2.0, (y0 + y1) / 2.0);
        return (lon, lat);
    }

    /// <summary>
    /// 范围框（指格心范围）+ 对边距 → 行列数：列 = Δx/格宽 + 1，行 = Δy/行距 + 1，至少 minSide。
    /// 尖顶格格宽 = cell_m，行距 = 1.5×半径 = 0.866×cell_m（全部按墨卡托米计）。
    /// </summary>
    public static (int Width, int Height) GridSize(double[] bbox, double cellKm, int minSide = LadderMinSide)
    {
        var (x0, y0) = Mercator.LonLatToMercator(bbox[0], bbox[1]);
        var (x1, y1) = Mercator.LonLatToMercator(bbox[2], bbox[3]);
        if (!(x1 > x0 && y1 > y0))
            throw new GisPlanException("bbox 需满足 西<东 且 南<北");
        var cellM = cellKm * 1000.0 * MercatorScale(GridCenter(bbox).CenterLat);
        var radiusM = cellM / Math.Sqrt(3.0);
        var hexWidthM = Math.Sqrt(3.0) * radiusM;
        var rowStepM = 1.5 * radiusM;
        var width = (int)Math.Round((x1 - x0) / hexWidthM) + 1;
        var height = (int)Math.Round((y1 - y0) / rowStepM) + 1;
        return (Math.Max(minSide, width), Math.Max(minSide, height));
    }

    /// <summary>返回 (推荐对边距, 理由)。尖顶格行距 = 0.866×对边距 ⇒ 格数 ≈ 面积/(0.866·c²)。</summary>
    public static (double CellKm, List<string> Reasons) Recommend(double[] bbox)
    {
        var (w, h) = BboxGroundKm(bbox);
        var ideal = Math.Sqrt(w * h / (0.866 * TargetCells));
        var floorKm = Math.Max(ideal, DemFloorKm);
        var cap = Math.Min(w, h) / (MinSide - 1.0);   // 保证短边铺得下 MinSide 格
        var reasons = new List<string>
        {
            $"目标 {TargetCells} 格左右（实地 {FmtKm(w)}×{FmtKm(h)} km）⇒ 理论格距 {F("F2", ideal)} km",
        };
        var pick = CellLadderKm.Length;
        for (var i = 0; i < CellLadderKm.Length; i++)
        {
            if (CellLadderKm[i] >= floorKm - 1e-12) { pick = i; break; }
        }
        var cellKm = CellLadderKm[Math.Min(pick, CellLadderKm.Length - 1)];
        if (cellKm > cap)
        {
            var smaller = -1;
            for (var i = 0; i < CellLadderKm.Length; i++)
            {
                if (CellLadderKm[i] <= cap) smaller = i;
            }
            if (smaller >= 0)
            {
                cellKm = CellLadderKm[smaller];
                reasons.Add($"为在短边铺够 {MinSide} 格，改取更小格距（格数会超过目标）");
            }
            else
            {
                cellKm = CellLadderKm[0];
                reasons.Add("范围过小：已取最小格距，建议扩大范围框");
            }
        }
        if (ideal < DemFloorKm)
            reasons.Add("格距不低于高程栅格分辨率（1.0 km），更细只是插值");
        reasons.Add($"对边距按实地计，中心纬度墨卡托放大 {F("F2", MercatorScale((bbox[1] + bbox[3]) / 2.0))} 倍");
        return (cellKm, reasons);
    }

    /// <summary>向导/CLI 共用的方案体（字段名即线上契约，与 hexgen.py plan_payload 一致）。</summary>
    public static JsonObject PlanPayload(double[] bbox, double? cellKm)
    {
        var (wKm, hKm) = BboxGroundKm(bbox);
        var (rec, reasons) = Recommend(bbox);
        var clat = (bbox[1] + bbox[3]) / 2.0;
        var ladder = new JsonArray();
        foreach (var c in CellLadderKm)
        {
            var (gw, gh) = GridSize(bbox, c);
            var cells = gw * gh;
            var label = Math.Abs(c - rec) < 1e-9 ? "recommended"
                : cells > TargetCells * 2.5 ? "heavy"
                : Math.Min(gw, gh) < MinSide ? "coarse"
                : "ok";
            ladder.Add(new JsonObject
            {
                ["cell_km"] = JsonValue.Create(c),
                ["width"] = JsonValue.Create(gw),
                ["height"] = JsonValue.Create(gh),
                ["cells"] = JsonValue.Create(cells),
                ["label"] = JsonValue.Create(label),
            });
        }
        JsonObject? current = null;
        if (cellKm is { } ck && ck > 0)
        {
            var (gw, gh) = GridSize(bbox, ck);
            current = new JsonObject
            {
                ["cell_km"] = JsonValue.Create(ck),
                ["width"] = JsonValue.Create(gw),
                ["height"] = JsonValue.Create(gh),
                ["cells"] = JsonValue.Create(gw * gh),
                ["mercator_scale"] = JsonValue.Create(MercatorScale(GridCenter(bbox).CenterLat)),
            };
        }
        return new JsonObject
        {
            ["ok"] = JsonValue.Create(true),
            ["bbox"] = Arr(bbox[0], bbox[1], bbox[2], bbox[3]),
            ["center_lonlat"] = Arr((bbox[0] + bbox[2]) / 2.0, clat),
            ["ground_km"] = Arr(Round2(wKm), Round2(hKm)),
            ["mercator_scale"] = JsonValue.Create(MercatorScale(clat)),
            ["target_cells"] = JsonValue.Create(TargetCells),
            ["min_side"] = JsonValue.Create(MinSide),
            ["dem_floor_km"] = JsonValue.Create(DemFloorKm),
            ["max_cells"] = JsonValue.Create(MaxCells),
            ["recommended"] = new JsonObject
            {
                ["cell_km"] = JsonValue.Create(rec),
                ["reasons"] = Arr(reasons),
            },
            ["current"] = current,
            ["ladder"] = ladder,
        };
    }

    private static JsonArray Arr(params object[] items)
    {
        var a = new JsonArray();
        foreach (var it in items) a.Add(JsonValue.Create(it));
        return a;
    }

    private static JsonArray Arr(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var it in items) a.Add(JsonValue.Create(it));
        return a;
    }

    private static string F(string format, double v) => v.ToString(format, CultureInfo.InvariantCulture);
    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>GIS 方案/作业入参错误（对外统一成 {"error": …}）。</summary>
public sealed class GisPlanException : Exception
{
    public GisPlanException(string message) : base(message) { }
}
