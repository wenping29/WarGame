using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Supply lines, power grid, objective nodes — ported from mechanics.dart.
/// Handles multi-source BFS for supply distances, retention-based supply coefficients,
/// power factor calculation, and node holder visibility for fog of war.
/// </summary>
public static class MechanicsService
{
    /// <summary>
    /// 季节消耗系数：剧本 clock.season 写中文（冬/寒、秋/雨）时加重战损，其余为 1。
    /// 系数在 rules.combat 里可改，不在代码里写死。
    /// </summary>
    public static double SeasonAttrition(GameState g)
    {
        var cf = g.Rules.Combat;
        var season = g.Season ?? "";
        if (season.Contains('冬') || season.Contains('寒')) return cf.SeasonWinterAttrition;
        if (season.Contains('秋') || season.Contains('雨')) return cf.SeasonAutumnAttrition;
        return 1.0;
    }

    /// <summary>
    /// 天气演变：fixed/director 由剧本或导演部锁定不动，其余按 WEATHER_CHAIN 逐回合转移一次。
    /// 只抽一列随机数（nextDouble），抽数固定才能让重放落在同一条链上。
    /// </summary>
    public static string EvolveWeather(GameState g)
    {
        var mode = g.WeatherMode ?? "director";
        if (mode is "fixed" or "director") return g.Weather ?? "clear";

        var current = g.Weather ?? "clear";
        var row = LibraryService.WEATHER_CHAIN.GetValueOrDefault(current)
                  ?? LibraryService.WEATHER_CHAIN["clear"];

        var roll = g.Rng.NextDouble();
        foreach (var (id, p) in row)
        {
            roll -= p;
            if (roll <= 0) return id;
        }
        return row[^1].Id;
    }

    /// <summary>
    /// 多源加权松弛（SPFA）：每格到最近补给源的路径代价。
    /// 补给源 = 辎重/仓库类（SupplySource）与一切固定设施（cls=structure）；
    /// 夺下的机场是前进补给节点 —— 须该方仍有存活航空兵维持空运、且场站格由己方地面部队占据，距离记 0。
    /// 敌占格断链；水系格（含河流）在无桥且无己方部队渡过时按 3 倍代价绕行，而不是硬阻断。
    /// 不可达格不入表：调用方按宽松默认 0.7 处理，不能写死下限让无源阵营整体挨饿。
    /// </summary>
    /// <returns>side → hexKey → 路径代价（仅红/蓝/中立）</returns>
    public static Dictionary<string, Dictionary<string, int>> SupplyDistances(GameState g)
    {
        var ground = GroundHolders(g);
        var outMap = new Dictionary<string, Dictionary<string, int>>();

        foreach (var side in new[] { "red", "blue", "neutral" })
        {
            var dist = new Dictionary<string, int>();
            var frontier = new List<Hex>();

            foreach (var u in g.Units)
            {
                if (!u.Alive || u.Side != side) continue;
                var t = g.TypeOf(u);
                if (!t.SupplySource && t.Cls != "structure") continue;
                if (dist.ContainsKey(u.Pos.Key)) continue;
                dist[u.Pos.Key] = 0;
                frontier.Add(u.Pos);
            }

            if (g.Units.Any(u => u.Alive && u.Side == side && g.TypeOf(u).IsAir))
            {
                foreach (var (key, cell) in g.Cells)
                {
                    if (cell.Airport < 1 || dist.ContainsKey(key)) continue;
                    if (ground.GetValueOrDefault(key) != side) continue;
                    dist[key] = 0;
                    frontier.Add(Hex.Parse(key));
                }
            }

            var i = 0;
            while (i < frontier.Count)
            {
                var cur = frontier[i++];
                var d = dist[cur.Key];
                foreach (var n in cur.Neighbors())
                {
                    if (!g.Cells.ContainsKey(n.Key)) continue;
                    var occ = ground.GetValueOrDefault(n.Key);
                    if (occ != null && occ != side) continue; // 敌占格断链
                    var nd = d + (g.TerrainOf(n).Water && !Crossable(g, n, side) ? 3 : 1);
                    if (dist.TryGetValue(n.Key, out var old) && old <= nd) continue;
                    dist[n.Key] = nd;
                    frontier.Add(n);
                }
            }

            outMap[side] = dist;
        }

        return outMap;
    }

    /// <summary>补给系数 R = retention^dist × 电网系数，保底 0.05。</summary>
    public static Dictionary<string, Dictionary<string, double>> ComputeSupply(GameState g)
    {
        var strict = FogService.FlagBool(g, "strict_supply", false);
        var dist = SupplyDistances(g);
        var ret = strict ? g.Rules.SupplyRetention : 0.95;
        var outMap = new Dictionary<string, Dictionary<string, double>>();

        foreach (var (side, m) in dist)
        {
            var pf = PowerFactor(g, side);
            var r = new Dictionary<string, double>();
            foreach (var (key, d) in m)
                r[key] = Math.Clamp(Math.Pow(ret, d) * pf, 0.05, 1.0);
            outMap[side] = r;
        }

        return outMap;
    }

    /// <summary>
    /// Compute air control per hex from air units, anti-air and structures.
    /// Positive = red-leaning, negative = blue-leaning, in [-1,1]. Contested (|v|&lt;0.2) omitted.
    /// Ported from mechanics.dart computeAirControl.
    /// </summary>
    public static Dictionary<string, double> ComputeAirControl(GameState g)
    {
        var acc = new Dictionary<string, Dictionary<string, double>>
        {
            ["red"] = new(),
            ["blue"] = new(),
        };

        void Add(string side, Hex h, double v, int radius)
        {
            if (side != "red" && side != "blue") return;
            var m = acc[side];
            foreach (var c in h.Range(Math.Max(0, radius)))
            {
                if (!g.Cells.ContainsKey(c.Key)) continue;
                m[c.Key] = m.GetValueOrDefault(c.Key, 0.0) + v;
            }
        }

        var roleWeight = new Dictionary<string, double>
        {
            ["fighter"] = 1.0, ["strike"] = 0.45, ["recon"] = 0.25,
        };

        foreach (var u in g.Units)
        {
            if (!u.Alive) continue;
            var t = g.TypeOf(u);
            if (t.IsAir)
            {
                var zone = u.Airborne ? (u.MissionZone ?? u.Pos) : u.Pos;
                var w = roleWeight.GetValueOrDefault(t.AirRole, 0.1);
                Add(u.Side, zone, u.Mission == "patrol" ? w : w * 0.4, t.AirRange);
            }
            else if (t.AntiAir > 0)
            {
                Add(u.Side, u.Pos, t.AntiAir * 0.25, 3);
            }
            else if (t.Cls == "structure")
            {
                Add(u.Side, u.Pos, 0.2, 4);
            }
        }

        var keys = new HashSet<string>(acc["red"].Keys);
        keys.UnionWith(acc["blue"].Keys);
        var outMap = new Dictionary<string, double>();
        foreach (var k in keys)
        {
            var a = acc["red"].GetValueOrDefault(k, 0.0);
            var b = acc["blue"].GetValueOrDefault(k, 0.0);
            var tot = a + b;
            if (tot < 0.05) continue;
            var v = (a - b) / tot;
            if (Math.Abs(v) < 0.2) continue; // contested → no owner
            outMap[k] = Math.Max(-1.0, Math.Min(1.0, v));
        }
        return outMap;
    }

    /// <summary>
    /// 电网系数：必须同时掌握「电厂」和「至少一处变电站」才通电（只夺半边不发电），
    /// 掌握的电网节点越多越稳，红利 1.0 + 0.15 × held/nodes（自然封顶 +15%）。
    /// </summary>
    public static double PowerFactor(GameState g, string side)
    {
        var holders = HoldersByHex(g);
        var nodes = 0;
        var held = 0;
        var plantHeld = false;
        var subHeld = false;

        foreach (var (key, cell) in g.Cells)
        {
            if (!cell.PowerPlant && !cell.Substation) continue;
            nodes++;
            if (holders.GetValueOrDefault(key) != side) continue;
            held++;
            if (cell.PowerPlant) plantHeld = true;
            else subHeld = true;
        }

        if (nodes == 0 || !plantHeld || !subHeld) return 1.0;
        return 1.0 + 0.15 * held / nodes;
    }

    /// <summary>
    /// 逐格唯一占据方（一切存活单位）：两方同格 = 争夺中，不入表 ⇒ 不算占。
    /// 要点归属与电网系数共用这一份口径，一次算好复用，别每格重扫单位表。
    /// </summary>
    public static Dictionary<string, string> HoldersByHex(GameState g)
    {
        var first = new Dictionary<string, string>();
        var contested = new HashSet<string>();

        foreach (var u in g.Units)
        {
            if (!u.Alive) continue;
            var key = u.Pos.Key;
            if (contested.Contains(key)) continue;
            if (first.TryGetValue(key, out var side))
            {
                if (side != u.Side)
                {
                    contested.Add(key);
                    first.Remove(key);
                }
                continue;
            }
            first[key] = u.Side;
        }

        return first;
    }

    /// <summary>逐格唯一地面占据方（不含航空兵）：过境飞机既不占地也不断敌补给。</summary>
    public static Dictionary<string, string> GroundHolders(GameState g)
    {
        var first = new Dictionary<string, string>();
        var contested = new HashSet<string>();

        foreach (var u in g.Units)
        {
            if (!u.Alive || g.TypeOf(u).IsAir) continue;
            var key = u.Pos.Key;
            if (contested.Contains(key)) continue;
            if (first.TryGetValue(key, out var side))
            {
                if (side != u.Side)
                {
                    contested.Add(key);
                    first.Remove(key);
                }
                continue;
            }
            first[key] = u.Side;
        }

        return first;
    }

    /// <summary>
    /// 该水系格对本方是否可渡：有桥，或己方部队已在格上（架浮桥/夺渡口）。
    /// 不可渡不阻断，只按 3 倍代价绕行 —— 后勤可以绕，但不能无视河。
    /// </summary>
    private static bool Crossable(GameState g, Hex h, string side)
    {
        var c = g.GetCell(h);
        if (c != null && c.Bridge) return true;
        return g.Units.Any(u => u.Alive && u.Side == side && u.Pos == h);
    }

    // ─────────── Dart fog.dart/mechanics.dart view-shape helpers ───────────

    private static readonly (string Kind, string Label)[] NodeKindOrder =
    {
        ("power_plant", "发电厂"),
        ("substation", "变电站"),
        ("airport_major", "干线机场"),
        ("airport", "野战机场"),
        ("rail_station", "火车站"),
        ("city_major", "大城市"),
        ("city", "城镇"),
    };

    /// <summary>该格是要点则返回类别（电厂 &gt; 变电站 &gt; 机场 &gt; 车站 &gt; 城镇）。</summary>
    public static string? NodeKindOf(Cell c)
    {
        if (c.PowerPlant) return "power_plant";
        if (c.Substation) return "substation";
        if (c.Airport >= 2) return "airport_major";
        if (c.Airport == 1) return "airport";
        if (c.RailStation) return "rail_station";
        if (c.City >= 2) return "city_major";
        if (c.City == 1) return "city";
        return null;
    }

    public static string? NodeLabel(string? kind) =>
        kind == null ? null : NodeKindOrder.FirstOrDefault(n => n.Kind == kind).Label;

    /// <summary>战场要点清单（纯地理，不含归属）：{key,q,r,kind,label}，按类别→坐标排序。</summary>
    public static List<Dictionary<string, object?>> ObjectiveNodesGeo(GameState g)
    {
        var order = NodeKindOrder.Select(n => n.Kind).ToList();
        var outList = new List<(string Key, int Q, int R, string Kind, string Label)>();
        foreach (var (key, cell) in g.Cells)
        {
            var kind = NodeKindOf(cell);
            if (kind == null) continue;
            var h = Hex.Parse(key);
            outList.Add((key, h.Q, h.R, kind, NodeLabel(kind)!));
        }
        return outList
            .OrderBy(x => order.IndexOf(x.Kind))
            .ThenBy(x => x.Key)
            .Select(x => new Dictionary<string, object?>
            {
                ["key"] = x.Key, ["q"] = x.Q, ["r"] = x.R, ["kind"] = x.Kind, ["label"] = x.Label,
            })
            .ToList();
    }

    /// <summary>要点归属：hexKey → 阵营（争夺中不入表）。onlySide 用于双盲视图，只披露己方占领。</summary>
    public static Dictionary<string, string> NodeHoldersByHex(GameState g, string? onlySide = null)
    {
        var holders = HoldersByHex(g);
        var outMap = new Dictionary<string, string>();
        foreach (var (key, cell) in g.Cells)
        {
            if (NodeKindOf(cell) == null) continue;
            var holder = holders.GetValueOrDefault(key);
            if (holder == null || (onlySide != null && holder != onlySide)) continue;
            outMap[key] = holder;
        }
        return outMap;
    }
}
