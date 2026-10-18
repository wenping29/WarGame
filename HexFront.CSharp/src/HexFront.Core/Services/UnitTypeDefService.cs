using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// 兵种属性覆盖（副本向导第 4 步）：白名单/区间校验、按偏离度自动计价、把补丁合入定义库。
/// 设计文档 §6.4「改了就要付钱」：补丁没显式标价就按相对出厂值的偏离度收点，
/// 否则「把坦克火力从 24 调到 48」就能绕过点数预算。
/// </summary>
public static class UnitTypeDefService
{
    /// <summary>创建者能改的键（JSON 名）。cls/is_hq/supply_source/air_role 动的是规则语义，一律拒绝。</summary>
    public static readonly HashSet<string> PatchKeys = new(StringComparer.Ordinal)
    {
        "label", "mobility", "firepower", "protection", "recon", "load", "consumption",
        "personnel", "range", "anti_air", "points",
    };

    /// <summary>数值属性的允许区间（下限, 上限）。</summary>
    public static readonly Dictionary<string, (double Min, double Max)> PatchRanges = new(StringComparer.Ordinal)
    {
        ["mobility"] = (0.0, 60.0),
        ["firepower"] = (0.0, 120.0),
        ["protection"] = (0.05, 8.0),
        ["recon"] = (0.0, 20.0),
        ["load"] = (0.05, 8.0),
        ["consumption"] = (0.0, 10.0),
        ["personnel"] = (1.0, 5000.0),
        ["range"] = (0.0, 12.0),
        ["anti_air"] = (0.0, 10.0),
    };

    /// <summary>战力与防护最贵，机动次之；负荷与消耗是「负担」，调高反而便宜——结算里真的会扣 MP 和补给。</summary>
    public static readonly Dictionary<string, double> PriceWeights = new(StringComparer.Ordinal)
    {
        ["firepower"] = 0.55,
        ["protection"] = 0.45,
        ["mobility"] = 0.35,
        ["recon"] = 0.25,
        ["personnel"] = 0.30,
        ["load"] = -0.12,
        ["consumption"] = -0.18,
    };

    /// <summary>射程类是整数格，按加数量计（每格 / 每点强度）。</summary>
    public static readonly Dictionary<string, double> PriceAdditive = new(StringComparer.Ordinal)
    {
        ["range"] = 0.08,
        ["anti_air"] = 0.06,
    };

    public const double PriceMin = 0.35;
    public const double PriceMax = 2.6;

    /// <summary>补丁没给 points 时的成交价：倍率 = 1 + Σ 权重 ×（新值 − 出厂值）/ 标尺，夹在 0.35–2.6。</summary>
    public static double PricedPoints(UnitTypeDef baseDef, IReadOnlyDictionary<string, JsonElement> patch)
    {
        var mult = 1.0;
        foreach (var (key, w) in PriceWeights)
        {
            if (Num(patch, key) is not { } raw) continue;
            var b = BaseValue(baseDef, key);
            var denom = Math.Abs(b) < 0.5 ? 1.0 : Math.Abs(b);   // 基准过小时用统一标尺，避免 0.05→0.1 记成 +100%
            mult += w * ((raw - b) / denom);
        }
        foreach (var (key, w) in PriceAdditive)
        {
            if (Num(patch, key) is not { } raw) continue;
            mult += w * (raw - BaseValue(baseDef, key));
        }
        return Math.Round(baseDef.Points * Math.Clamp(mult, PriceMin, PriceMax), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>把单个补丁合入定义（未显式标价的按偏离度计价；显式 points 原样采用，不二次计价）。</summary>
    public static UnitTypeDef Apply(UnitTypeDef baseDef, IReadOnlyDictionary<string, JsonElement> patch)
    {
        var label = baseDef.Label;
        var mobility = baseDef.Mobility;
        var firepower = baseDef.Firepower;
        var protection = baseDef.Protection;
        var recon = baseDef.Recon;
        var load = baseDef.Load;
        var consumption = baseDef.Consumption;
        var personnel = baseDef.Personnel;
        var range = baseDef.Range;
        var antiAir = baseDef.AntiAir;
        var points = baseDef.Points;

        if (patch.TryGetValue("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String)
            label = labelEl.GetString() ?? label;
        if (Num(patch, "mobility") is { } mb) mobility = mb;
        if (Num(patch, "firepower") is { } fp) firepower = fp;
        if (Num(patch, "protection") is { } pt) protection = pt;
        if (Num(patch, "recon") is { } rc) recon = rc;
        if (Num(patch, "load") is { } ld) load = ld;
        if (Num(patch, "consumption") is { } cp) consumption = cp;
        if (Num(patch, "personnel") is { } ps) personnel = (int)Math.Round(ps);
        if (Num(patch, "range") is { } rg) range = (int)Math.Round(rg);
        if (Num(patch, "anti_air") is { } aa) antiAir = aa;
        points = Num(patch, "points") ?? PricedPoints(baseDef, patch);

        return new UnitTypeDef
        {
            Id = baseDef.Id,
            Label = label,
            Cls = baseDef.Cls,
            Mobility = mobility,
            Firepower = firepower,
            Protection = protection,
            Recon = recon,
            Load = load,
            Consumption = consumption,
            Personnel = personnel,
            IsHQ = baseDef.IsHQ,
            SupplySource = baseDef.SupplySource,
            AirRole = baseDef.AirRole,
            AirRange = baseDef.AirRange,
            AntiAir = antiAir,
            Range = range,
            TrainingDefault = baseDef.TrainingDefault,
            ExpDefault = baseDef.ExpDefault,
            Counters = new Dictionary<string, double>(baseDef.Counters),
            Points = points,
            Equipment = new List<EquipmentItem>(baseDef.Equipment),
        };
    }

    /// <summary>剧本 + 覆盖 → 有效定义库：校验、预算、计价都必须用它，否则覆盖后的兵种按出厂价算。</summary>
    public static Dictionary<string, UnitTypeDef> EffectiveLibrary(
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? patches)
    {
        var types = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES);
        if (patches == null) return types;
        foreach (var patch in patches)
        {
            if (IdOf(patch) is not { } id || !types.TryGetValue(id, out var baseDef)) continue;
            types[id] = Apply(baseDef, patch);
        }
        return types;
    }

    /// <summary>向导展示用的成交价（前端不自己算钱）。</summary>
    public static Dictionary<string, double> TypePoints(
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? patches,
        Dictionary<string, UnitTypeDef> effective)
    {
        var outMap = new Dictionary<string, double>();
        if (patches == null) return outMap;
        foreach (var patch in patches)
        {
            if (IdOf(patch) is not { } id) continue;
            if (effective.TryGetValue(id, out var t)) outMap[id] = t.Points;
        }
        return outMap;
    }

    /// <summary>覆盖补丁的合法性：只允许改既有兵种的白名单字段，数值还要落在区间里。</summary>
    public static List<string> Validate(IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? patches)
    {
        var errs = new List<string>();
        if (patches == null) return errs;
        foreach (var patch in patches)
        {
            var id = IdOf(patch);
            if (id == null || !LibraryService.UNIT_TYPES.ContainsKey(id))
            {
                errs.Add($"兵种属性覆盖只允许改既有兵种，未知兵种 {id}");
                continue;
            }
            foreach (var key in patch.Keys)
            {
                if (key != "id" && !PatchKeys.Contains(key)) errs.Add($"兵种 {id} 不允许覆盖字段 {key}");
            }
            foreach (var (key, bound) in PatchRanges)
            {
                if (!patch.TryGetValue(key, out var el)) continue;
                if (el.ValueKind != JsonValueKind.Number)
                {
                    errs.Add($"兵种 {id} 的 {key} 必须是数值");
                    continue;
                }
                var v = el.GetDouble();
                if (v < bound.Min || v > bound.Max)
                    errs.Add($"兵种 {id} 的 {key}={Raw(el)} 超出允许区间 {Fmt(bound.Min)}–{Fmt(bound.Max)}");
            }
            if (Num(patch, "points") is { } pts && pts <= 0) errs.Add($"兵种 {id} 的显式点数必须大于 0");
        }
        return errs;
    }

    private static string? IdOf(IReadOnlyDictionary<string, JsonElement> patch) =>
        patch.TryGetValue("id", out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static double? Num(IReadOnlyDictionary<string, JsonElement> patch, string key) =>
        patch.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

    private static double BaseValue(UnitTypeDef d, string key) => key switch
    {
        "firepower" => d.Firepower,
        "protection" => d.Protection,
        "mobility" => d.Mobility,
        "recon" => d.Recon,
        "personnel" => d.Personnel,
        "load" => d.Load,
        "consumption" => d.Consumption,
        "range" => d.Range,
        "anti_air" => d.AntiAir,
        _ => 0.0,
    };

    private static string Raw(JsonElement el) => el.GetRawText();

    /// <summary>区间端点按 Dart 的 double 打印口径（60 → 60.0），让错误文案可直接对拍。</summary>
    private static string Fmt(double v) =>
        v == Math.Truncate(v) && Math.Abs(v) < 1e15
            ? v.ToString("0", CultureInfo.InvariantCulture) + ".0"
            : v.ToString(CultureInfo.InvariantCulture);
}
