using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// 战争迷雾：观测强度累计 → 通视判定 → 暴露分级 → 阵营视图裁剪。
/// 铁律：全量真实态永不出服务端，本文件产出的 side_view 就是网络能看见的上限。
/// 1:1 port of hexfront/lib/engine/fog.dart.
/// </summary>
public static class FogService
{
    /// <summary>侦察半径（格）：兵种 recon + 天气能见度 + 昼夜 + 观察方地形。</summary>
    public static double ReconRadius(GameState g, Unit u)
    {
        var t = g.TypeOf(u);
        var wx = g.WeatherOf();
        var day = DayPhase.FromHours(g.HourOfDay);
        var obsTerrain = g.TerrainOf(u.Pos);
        var r = (1.0 + t.Recon * 0.55) * wx.Recon * day.Recon;
        if (u.Fatigue > 60) r *= 0.85;
        if (u.Status == UnitStatus.Routed) r *= 0.5;
        r *= 1.0 - 0.15 * Math.Clamp(1.0 - obsTerrain.ObsPenalty, 0.0, 1.0);
        return r;
    }

    /// <summary>通视：真实高程 + 地表遮挡。山脊挡住谷地是迷雾可信度的来源。</summary>
    public static bool LineOfSight(GameState g, Hex from, Hex to)
    {
        if (from.Equals(to)) return true;

        var path = Hex.Lerp(from, to).ToList();
        var fromCell = g.GetCell(from);
        var toCell = g.GetCell(to);
        if (fromCell == null || toCell == null) return false;

        var eyeA = fromCell.Elev + 20.0;
        var eyeB = toCell.Elev + 20.0;
        var n = path.Count - 1;
        var step = g.Rules.LosElevStep;
        var blocking = 0.0;

        for (var i = 1; i < n; i++)
        {
            var t = (double)i / n;
            var sight = eyeA + (eyeB - eyeA) * t;
            var cell = g.GetCell(path[i]);
            if (cell != null && cell.Elev > sight + step) blocking += 1.0;
            blocking += g.TerrainOf(path[i]).LosBlock * 0.6;
        }

        if (blocking >= 1.6) return false;

        var targetTd = g.TerrainOf(to);
        if (targetTd.ObsPenalty < 0.6 && from.DistanceTo(to) > 4 && blocking > 0.4) return false;
        return true;
    }

    /// <summary>暴露等级：观测点 → 0..3。</summary>
    public static int ExposureLevel(GameState g, int obsPoints)
    {
        var r = g.Rules;
        if (obsPoints >= r.ExposureE3) return 3;
        if (obsPoints >= r.ExposureE2) return 2;
        if (obsPoints >= r.ExposureE1) return 1;
        return 0;
    }

    /// <summary>每回合重算各方观测点，并刷新/衰减“已暴露目标”记忆。</summary>
    public static void UpdateObservation(GameState g)
    {
        var sides = new[] { "red", "blue" };
        var fogOn = FlagBool(g, "fog", true);

        if (!fogOn)
        {
            // 迷雾关闭只放开观测场，不写 tracks：明牌期间的"看见"不该变成重开雾后 still 生效的永久档案（§11.9）。
            // 明牌展示由 SideView 实时合成。
            foreach (var side in sides)
            {
                var all = new Dictionary<string, int>();
                foreach (var key in g.Cells.Keys) all[key] = 99;
                g.Obs[side] = all;
            }
            g.Obs["spectator"] = new Dictionary<string, int>();
            return;
        }

        foreach (var side in sides)
        {
            var pts = new Dictionary<string, int>();
            foreach (var u in g.Units.Where(u => u.Alive && u.Side == side))
            {
                var r = ReconRadius(g, u);
                if (r <= 0) continue;
                var radius = Math.Max(1, (int)Math.Round(r));
                var strengthBase = u.Airborne ? 3 : 1 + (int)(g.TypeOf(u).Recon / 4.0);
                foreach (var h in u.Pos.Range(radius))
                {
                    if (!g.Cells.ContainsKey(h.Key)) continue;
                    var d = u.Pos.DistanceTo(h);
                    if (d > 0 && !LineOfSight(g, u.Pos, h)) continue;
                    var w = d == 0
                        ? strengthBase + 1
                        : Math.Max(1, strengthBase - (int)(d / radius * 1.5));
                    pts[h.Key] = pts.GetValueOrDefault(h.Key, 0) + w;
                }
            }

            // 雷达站：环形远距离 +1
            foreach (var u in g.Units.Where(u => u.Alive && u.Side == side && g.TypeOf(u).Id == "radar"))
            {
                foreach (var h in u.Pos.Range(9))
                {
                    if (g.Cells.ContainsKey(h.Key))
                        pts[h.Key] = pts.GetValueOrDefault(h.Key, 0) + 1;
                }
            }

            // 制空权带来额外监视
            foreach (var (k, v) in g.AirControl)
            {
                var mine = (v > 0 && side == "red") || (v < 0 && side == "blue");
                if (mine) pts[k] = pts.GetValueOrDefault(k, 0) + 2;
            }

            g.Obs[side] = pts;
            RefreshTracks(g, side);
        }
        g.Obs["spectator"] = new Dictionary<string, int>();
    }

    private static void RefreshTracks(GameState g, string side)
    {
        var enemies = g.Units
            .Where(u => u.Alive && u.Side != side && u.Side != "neutral")
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .ToList();
        var tracks = g.Tracks.TryGetValue(side, out var existing)
            ? existing
            : g.Tracks[side] = new Dictionary<string, Track>();
        var pts = g.Obs[side];
        var seenIds = new HashSet<string>();

        foreach (var e in enemies)
        {
            var p = pts.GetValueOrDefault(e.Pos.Key, 0);
            var lvl = ExposureLevel(g, p);
            if (lvl > 0)
            {
                seenIds.Add(e.Id);
                tracks.TryGetValue(e.Id, out var old);
                var level = old == null ? lvl : Math.Max(lvl, old.Level - (g.Turn - old.LastSeenTurn));
                tracks[e.Id] = new Track
                {
                    UnitId = e.Id,
                    Level = level,
                    LastSeen = e.Pos,
                    LastSeenTurn = g.Turn,
                };
            }
        }

        // 衰减：脱离观测 staleTurns 回合后彻底丢失
        var stale = g.Rules.StaleTurns;
        foreach (var id in tracks.Keys.ToList())
        {
            var t = tracks[id];
            if (seenIds.Contains(id)) continue;
            var age = g.Turn - t.LastSeenTurn;
            if (age > stale) tracks.Remove(id);
            else if (age >= 2) t.Level = Math.Max(1, t.Level - 1);
        }
    }

    /// <summary>兵力规模档位：E1 只给档位，E3 才给精确人数。</summary>
    public static string StrengthBand(GameState g, Unit u)
    {
        var full = g.TypeOf(u).Personnel;
        var ratio = u.Personnel / Math.Max(1, full);
        if (ratio < 0.34) return "弱";
        if (ratio < 0.7) return "中";
        return "强";
    }

    private static string Band(double v) => v < 30 ? "低" : (v < 60 ? "中" : "高");

    private static Dictionary<string, object?> ContactOf(GameState g, Track t)
    {
        var u = g.FindUnit(t.UnitId);
        var known = g.Turn - t.LastSeenTurn;
        var baseInfo = new Dictionary<string, object?>
        {
            ["id"] = t.UnitId,
            ["side"] = u?.Side ?? "unknown",
            ["q"] = t.LastSeen.Q,
            ["r"] = t.LastSeen.R,
            ["level"] = t.Level,
            ["known_turns_ago"] = known,
            ["stale"] = known > 0,
        };
        if (u == null || !u.Alive)
        {
            baseInfo["status"] = "gone";
            return baseInfo;
        }
        if (t.Level >= 1) baseInfo["strength_band"] = StrengthBand(g, u);
        if (t.Level >= 2)
        {
            var td = g.TypeOf(u);
            baseInfo["type"] = td.Id;
            baseInfo["type_label"] = td.Label;
            baseInfo["cls"] = td.Cls;
            baseInfo["fort"] = u.FortLevel;
            baseInfo["status"] = StatusStr(u);
            baseInfo["morale_band"] = Band(u.Morale);
            baseInfo["fatigue_band"] = Band(u.Fatigue);
        }
        if (t.Level >= 3)
        {
            var td = g.TypeOf(u);
            baseInfo["personnel"] = u.Personnel;
            baseInfo["ammo"] = R2(u.Ammo);
            baseInfo["fuel"] = R2(u.Fuel);
            baseInfo["food"] = R2(u.Food);
            baseInfo["training"] = (int)Math.Round(u.Training);
            baseInfo["experience"] = (int)Math.Round(u.Experience);
            baseInfo["equipment"] = Equipment(td);
            if (u.CommanderName != null)
                baseInfo["commander"] = new Dictionary<string, object?> { ["name"] = u.CommanderName, ["quality"] = u.CommanderQuality };
        }
        return baseInfo;
    }

    /// <summary>阵营视图：己方全知 + 敌方仅已暴露目标。这是下发给红/蓝终端的全部内容。</summary>
    public static Dictionary<string, object?> SideView(GameState g, string side)
    {
        var own = g.Units
            .Where(u => u.Side == side)
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .Select(u => OwnUnit(g, u))
            .ToList();
        // 明牌时按 E3 实时合成敌情，不读也不写 tracks —— 关雾是一次性照明，不是发档案
        List<Track> seenTracks;
        if (!FlagBool(g, "fog", true))
        {
            seenTracks = g.Units
                .Where(u => u.Alive && u.Side != side && u.Side != "neutral")
                .Select(u => new Track { UnitId = u.Id, Level = 3, LastSeen = u.Pos, LastSeenTurn = g.Turn })
                .ToList();
        }
        else
        {
            seenTracks = g.Tracks.TryGetValue(side, out var tracks) ? tracks.Values.ToList() : new List<Track>();
        }
        var contacts = seenTracks
            .OrderBy(t => t.UnitId, StringComparer.Ordinal)
            .Select(t => ContactOf(g, t))
            .ToList();

        var view = Common(g);
        view["kind"] = "side";
        view["side"] = side;
        view["own"] = own;
        view["contacts"] = contacts;
        view["node_holders"] = MechanicsService.NodeHoldersByHex(g, onlySide: side);
        return view;
    }

    /// <summary>导演部视图：全局真实态（双方指令/准备态由服务端附加）。</summary>
    public static Dictionary<string, object?> DirectorView(GameState g)
    {
        var all = g.Units
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .Select(u => OwnUnit(g, u))
            .ToList();
        var power = new Dictionary<string, object?>
        {
            ["red"] = Math.Round(MechanicsService.PowerFactor(g, "red"), 3),
            ["blue"] = Math.Round(MechanicsService.PowerFactor(g, "blue"), 3),
        };
        var airControl = g.AirControl
            .Where(kv => kv.Value != 0)
            .Select(kv =>
            {
                var h = Hex.Parse(kv.Key);
                return new Dictionary<string, object?> { ["q"] = h.Q, ["r"] = h.R, ["v"] = R2(kv.Value) };
            })
            .ToList();

        var view = Common(g);
        view["kind"] = "director";
        view["units"] = all;
        view["node_holders"] = MechanicsService.NodeHoldersByHex(g);
        view["power"] = power;
        view["air_control"] = airControl;
        return view;
    }

    /// <summary>观战视图：仅公开情报层（双方已暴露目标的并集），无未暴露单位。</summary>
    public static Dictionary<string, object?> SpectatorView(GameState g)
    {
        var merged = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var side in new[] { "red", "blue" })
        {
            if (!g.Tracks.TryGetValue(side, out var tracks)) continue;
            foreach (var (id, t) in tracks)
            {
                if (merged.TryGetValue(id, out var old) && Convert.ToInt32(old["level"]) >= t.Level) continue;
                merged[id] = ContactOf(g, t);
            }
        }
        var contacts = merged.Values
            .OrderBy(c => (string)c["id"]!, StringComparer.Ordinal)
            .ToList();

        var view = Common(g);
        view["kind"] = "spectator";
        view["contacts"] = contacts;
        view["summary"] = new Dictionary<string, object?>
        {
            ["red_units"] = g.LiveOf("red").Count,
            ["blue_units"] = g.LiveOf("blue").Count,
            ["red_personnel"] = g.LiveOf("red").Sum(u => (long)u.Personnel),
            ["blue_personnel"] = g.LiveOf("blue").Sum(u => (long)u.Personnel),
        };
        return view;
    }

    // ==================== Serialization helpers ====================

    private static Dictionary<string, object?> OwnUnit(GameState g, Unit u)
    {
        var t = g.TypeOf(u);
        return new Dictionary<string, object?>
        {
            ["id"] = u.Id,
            ["side"] = u.Side,
            ["type"] = t.Id,
            ["type_label"] = t.Label,
            ["cls"] = t.Cls,
            ["q"] = u.Pos.Q,
            ["r"] = u.Pos.R,
            ["personnel"] = u.Personnel,
            ["morale"] = (int)Math.Round(u.Morale),
            ["fatigue"] = (int)Math.Round(u.Fatigue),
            ["training"] = (int)Math.Round(u.Training),
            ["experience"] = (int)Math.Round(u.Experience),
            ["ammo"] = R2(u.Ammo),
            ["fuel"] = R2(u.Fuel),
            ["food"] = R2(u.Food),
            ["status"] = StatusStr(u),
            ["fort"] = u.FortLevel,
            ["supply"] = R2(u.SupplyRate),
            ["hq"] = u.IsHQ || t.IsHQ,
            ["airborne"] = u.Airborne,
            ["mission"] = u.Mission,
            ["zone"] = u.MissionZone == null ? null : new[] { u.MissionZone.Value.Q, u.MissionZone.Value.R },
            ["commander"] = u.CommanderName,
            ["equipment"] = Equipment(t),
            ["mobility"] = t.Mobility,
            ["firepower"] = t.Firepower,
            ["recon"] = t.Recon,
        };
    }

    private static List<Dictionary<string, object?>> Equipment(UnitTypeDef t) =>
        t.Equipment.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id, ["label"] = e.Label, ["count"] = e.Count,
        }).ToList();

    private static Dictionary<string, object?> Common(GameState g)
    {
        var wx = g.WeatherOf();
        var day = DayPhase.FromHours(g.HourOfDay);

        var cells = g.Cells.Select(kv =>
        {
            var h = Hex.Parse(kv.Key);
            var c = kv.Value;
            var j = new Dictionary<string, object?>
            {
                ["q"] = h.Q,
                ["r"] = h.R,
                ["terrain"] = c.Terrain,
                ["elev"] = c.Elev,
                ["slope"] = c.Slope,
                ["road"] = c.Road,
                ["river"] = c.River,
                ["bridge"] = c.Bridge,
                ["city"] = c.City,
            };
            if (c.Railway) j["railway"] = true;
            if (c.RailStation) j["rail_station"] = true;
            if (c.Tunnel) j["tunnel"] = true;
            if (c.Powerline) j["powerline"] = true;
            if (c.PowerPlant) j["power_plant"] = true;
            if (c.Substation) j["substation"] = true;
            if (c.Airport > 0) j["airport"] = c.Airport;
            return j;
        }).ToList();

        var geo = new Dictionary<string, object?>
        {
            ["ox"] = g.Grid.Ox,
            ["oy"] = g.Grid.Oy,
            ["radius"] = g.Grid.Radius,
            ["layout"] = "odd_r",
        };
        if (g.Grid.CellGroundKm > 0) geo["cell_ground_km"] = g.Grid.CellGroundKm;
        if (g.Grid.MercatorScale > 0) geo["mercator_scale"] = g.Grid.MercatorScale;

        return new Dictionary<string, object?>
        {
            ["instance"] = g.InstanceId,
            ["scenario"] = g.ScenarioId,
            ["name"] = string.IsNullOrEmpty(g.Name) ? g.ScenarioName : g.Name,
            ["turn"] = g.Turn,
            ["max_turns"] = g.MaxTurns,
            ["hour"] = g.HourOfDay,
            ["day"] = new Dictionary<string, object?>
            {
                ["id"] = day.Id, ["label"] = day.Label, ["recon"] = day.Recon, ["combat"] = day.Combat,
            },
            ["season"] = g.Season,
            ["weather"] = new Dictionary<string, object?>
            {
                ["id"] = wx.Id, ["label"] = wx.Label, ["mobility"] = wx.Mobility, ["recon"] = wx.Recon,
                ["combat"] = wx.Combat, ["attrition"] = wx.Attrition, ["supply"] = wx.Supply,
                ["visibility"] = wx.Visibility,
            },
            ["flags"] = g.Flags,
            ["geo"] = geo,
            ["width"] = g.Width,
            ["height"] = g.Height,
            ["cells"] = cells,
            ["victory"] = g.VictoryConds.Select(v => new Dictionary<string, object?>
            {
                ["kind"] = v.Kind,
                ["side"] = v.Side,
                ["target"] = v.Target,
                ["amount"] = v.Amount,
                ["turns"] = v.Turns,
            }).ToList(),
            ["nodes"] = MechanicsService.ObjectiveNodesGeo(g),
            ["over"] = g.Over,
            ["winner"] = g.Winner,
            ["fog"] = FlagBool(g, "fog", true),
        };
    }

    /// <summary>Unit status → Dart wire string (ready/moved/combat/routed/destroyed).</summary>
    public static string StatusStr(Unit u) => u.Status switch
    {
        UnitStatus.Ready => "ready",
        UnitStatus.Moved => "moved",
        UnitStatus.Fired => "combat",
        UnitStatus.Resting => "rest",
        UnitStatus.Digging => "dig",
        UnitStatus.Routed => "routed",
        UnitStatus.Destroyed => "destroyed",
        _ => "ready",
    };

    private static double R2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 读 GameState.Flags 里的布尔开关，容忍存档回读时的 JsonElement 装箱。
    /// 快照里的 flags 是 JsonElement 而不是 bool，所以全引擎只能经这里取值。
    /// </summary>
    public static bool FlagBool(GameState g, string key, bool fallback)
    {
        var raw = g.Flags.GetValueOrDefault(key);
        if (raw == null) return fallback;
        return raw switch
        {
            bool b => b,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.False => false,
            _ => fallback,
        };
    }
}
