using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexFront.Core.Geometry;
using HexFront.Core.Services;

namespace HexFront.Core.Models;

/// <summary>Fog of war track for exposed enemy units.</summary>
public sealed class Track
{
    public string UnitId { get; init; } = "";
    public int Level { get; set; } // 0-3 exposure level
    public Hex LastSeen { get; init; }
    public int LastSeenTurn { get; init; }
}

/// <summary>War event log entry.</summary>
public sealed class WarEvent
{
    public int Turn { get; init; }
    public string Code { get; init; } = "";
    public string Text { get; init; } = "";
    public List<string> Sides { get; init; } = new();
}

/// <summary>
/// Single unit instance on the map. Ported from state.dart Unit class.
/// </summary>
public sealed class Unit
{
    public string Id { get; init; } = "";
    public string Side { get; init; } = "";      // "red" | "blue" | "neutral"
    public string UnitType { get; init; } = "";  // References UnitTypeDef.Id
    public Hex Pos { get; set; }                  // Current position (mutable for movement)
    public int Personnel { get; set; }            // Current personnel count
    public int MaxPersonnel { get; init; }        // Full strength
    public double Morale { get; set; }            // 0-100
    public double Fatigue { get; set; }           // 0-100 (higher = more tired)
    public double Ammo { get; set; }              // 0.0-1.0 ammunition level
    public double Fuel { get; set; }              // 0.0-1.0 fuel level
    public double Food { get; set; }              // 0.0-1.0 food supply
    public double SupplyRate { get; set; }        // Current supply rate (0-1)
    public double Training { get; set; } = 60;    // 0-100 (Dart Unit.training)
    public double Experience { get; set; } = 40;  // 0-100 (Dart Unit.experience)
    public bool IsHQ { get; set; }
    public string? CommanderName { get; set; }
    public double CommanderQuality { get; set; } = 0.5;
    public int Casualties { get; set; }           // Total casualties taken
    public bool Alive => Personnel > 0;
    public UnitStatus Status { get; set; } = UnitStatus.Ready;
    public bool Airborne { get; set; }
    public string Mission { get; set; } = "none";
    public Hex? MissionZone { get; set; }
    public int FortLevel { get; set; }
    public List<string> Orders { get; init; } = new(); // Pending orders for this turn
    
    public Unit Clone() => (Unit)MemberwiseClone();
}

/// <summary>Unit status enumeration.</summary>
public enum UnitStatus
{
    Ready,
    Moved,
    Fired,
    Resting,
    Digging,
    Routed,
    Destroyed,
}

/// <summary>
/// 胜利条件：与线上 <c>victory[]</c> 同形（doc §6.4「kind: "nodes", amount: N」）。
/// kind 用字符串而不是枚举：它既是剧本入参也是视图出参，多一层名字↔序号的映射只会让旧存档按数字错位。
/// </summary>
public sealed class VictoryCond
{
    /// <summary>annihilate 歼灭比例 / capture 独占要点格 / nodes 同时掌握 N 处要点 / survive 撑满回合。</summary>
    public string Kind { get; init; } = "annihilate";
    /// <summary>达成方；剧本漏写时按红方判定（与既有视图口径一致），不要让条件静默失效。</summary>
    public string Side { get; init; } = "red";
    public string? Target { get; init; }   // capture："q,r"
    public int Amount { get; init; }       // annihilate：削掉起始兵力的百分比；nodes：要点数量
    public int Turns { get; init; }        // survive：坚持到该回合
}

/// <summary>
/// Complete game state: grid, units, rules, scenario config.
/// Ported from state.dart GameState.
/// </summary>
public sealed class GameState
{
    public HexGrid Grid { get; init; } = null!;
    public Dictionary<string, Cell> Cells { get; init; } = new(); // hexKey → Cell
    public List<Unit> Units { get; init; } = new();
    public Rules Rules { get; init; } = new();
    public Dictionary<string, TerrainDef> Terrains { get; init; } = new();
    public Dictionary<string, UnitTypeDef> UnitTypes { get; init; } = new();
    public Dictionary<string, WeatherDef> Weathers { get; init; } = new();
    
    // Scenario metadata
    public string ScenarioId { get; init; } = "";
    public string ScenarioName { get; init; } = "";
    /// <summary>副本随机种子：同种子 + 同指令序列 → 同结果（§4 确定性要求）。</summary>
    public uint Seed { get; set; }
    public int Turn { get; set; }  // Current turn number
    public int MaxTurns { get; init; } = 14;
    public int HourOfDay { get; set; } = 6;
    public string Phase { get; set; } = "orders"; // orders/movement/combat/resolution
    public string? Weather { get; set; } = "clear";
    public string WeatherMode { get; set; } = "director"; // director/realistic/random
    /// <summary>夜幕起始时刻（0–23，null = 出厂默认 20）。拂晓/黄昏窗口随夜幕自动推算。</summary>
    public int? NightFrom { get; set; }
    /// <summary>夜幕结束时刻（0–23，可小于 NightFrom 表示跨零点；null = 出厂默认 6）。</summary>
    public int? NightTo { get; set; }
    /// <summary>
    /// 天气计划（导演部专属）：回合区间 → 天气的预排列表。
    /// 只有导演部能整组设置 / 启动 / 暂停 / 删除；红蓝双方既看不到操作入口也改不动。
    /// 计划整体替换，条目本身不可变，浅拷贝列表即可。
    /// </summary>
    public List<WeatherPhase>? WeatherSchedule { get; set; }
    /// <summary>计划执行状态：null/off 未启动、running 执行中、paused 暂停（冻结当前天气）。</summary>
    public string? WeatherScheduleState { get; set; }
    public bool Over { get; set; }
    public string? Winner { get; set; }
    /// <summary>终局发生的回合号（未终局为 null）：回放时间轴要知道"打到第几回合就结了"。</summary>
    public int? EndedTurn { get; set; }
    
    // Game flags (optional modifiers)
    public Dictionary<string, object> Flags { get; init; } = new();
    
    // Victory conditions
    public List<VictoryCond> VictoryConds { get; init; } = new();
    
    // Observation (for fog of war) - side → hexKey → observation points
    public Dictionary<string, Dictionary<string, int>> Obs { get; init; } = new();
    
    // Track history (for fog exposure levels)
    public Dictionary<string, Dictionary<string, Track>> Tracks { get; init; } = new();
    
    // Air control: hexKey → value (>0 red, <0 blue)
    public Dictionary<string, double> AirControl { get; init; } = new();
    
    // Events log
    public List<WarEvent> Events { get; init; } = new();
    
    // Instance metadata
    public string InstanceId { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = "";
    public int Width => Grid.Width;
    public int Height => Grid.Height;
    public string Season { get; init; } = "summer";

    /// <summary>
    /// 全引擎唯一随机源。Rng 本身不进 JSON（[JsonIgnore]），
    /// 由 <see cref="RngState"/> 以扁平 rng_state 落盘，与 Dart 快照字段同名，
    /// 载入时必须恢复状态而不是新建，否则续跑的分歧从第一个抖动就开始。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SeededRng Rng { get; } = new SeededRng(0);

    /// <summary>随机源状态（序列化为 rng_state）。</summary>
    public uint RngState
    {
        get => Rng.State;
        set => Rng.State = value;
    }

    /// <summary>Get cell at hex position.</summary>
    public Cell? GetCell(Hex h)
    {
        return Cells.GetValueOrDefault(h.Key);
    }

    /// <summary>Get terrain definition for a hex position.</summary>
    public TerrainDef TerrainOf(Hex h)
    {
        var cell = GetCell(h);
        if (cell == null) return LibraryService.GetTerrain("clear");
        return Terrains.GetValueOrDefault(cell.Terrain) ?? LibraryService.GetTerrain("clear");
    }

    /// <summary>Get weather definition.</summary>
    public WeatherDef WeatherOf()
    {
        return Weathers.GetValueOrDefault(Weather ?? "clear") ?? LibraryService.GetWeather("clear");
    }

    /// <summary>
    /// 当前昼夜相位：按剧本夜幕窗口（NightFrom→NightTo，支持跨零点）划分，
    /// 夜幕前后各 3 小时为拂晓/黄昏，其余为昼间；窗口非法或昼夜等长时回落出厂默认。
    /// </summary>
    public DayPhase DayPhaseOf()
    {
        var nf = NightFrom ?? 20;
        var nt = NightTo ?? 6;
        var h = ((HourOfDay % 24) + 24) % 24;

        var nightLen = ((nf - nt) % 24 + 24) % 24;   // 夜长（小时）
        if (nightLen == 0) return DayPhase.FromHours(h); // 起止相同视为无效，回落默认

        var sinceDawn = ((h - nt) % 24 + 24) % 24;   // 距拂晓开始的小时数
        if (sinceDawn >= nightLen) return new DayPhase("night", "夜间", 0.45, 0.75);

        var edge = Math.Min(3, nightLen / 3);        // 夜太短时拂晓/黄昏按比例压缩
        if (sinceDawn < edge) return new DayPhase("dawn", "拂晓", 0.8, 0.95);
        if (sinceDawn >= nightLen - edge) return new DayPhase("dusk", "黄昏", 0.8, 0.95);
        return new DayPhase("day", "昼间", 1.0, 1.0);
    }

    /// <summary>Get unit type definition for a unit.</summary>
    public UnitTypeDef TypeOf(Unit u)
    {
        return UnitTypes.GetValueOrDefault(u.UnitType) ?? LibraryService.GetUnitType(u.UnitType);
    }

    /// <summary>Find unit by ID.</summary>
    public Unit? FindUnit(string id)
    {
        return Units.FirstOrDefault(u => u.Id == id);
    }

    /// <summary>Get all alive units at a hex.</summary>
    public List<Unit> UnitsAt(Hex h)
    {
        return Units.Where(u => u.Alive && u.Pos == h).ToList();
    }

    /// <summary>Get all alive units for a side.</summary>
    public List<Unit> LiveOf(string side)
    {
        return Units.Where(u => u.Alive && u.Side == side).ToList();
    }

    /// <summary>
    /// 紧凑指纹（FNV-1a 8 位十六进制）：确定性对拍 + 存档完整性校验，字段构成与 Dart 版 state.dart 对齐。
    /// 必须含 rng_state —— 随机源走偏要在指纹上立刻暴露，不能等到肉眼比对兵力。
    /// </summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(Turn).Append('#').Append(HourOfDay).Append('#').Append(Weather ?? "clear")
          .Append('#').Append(Rng.State).Append('#');
        sb.Append(string.Join("|", Units
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .Select(u => $"{u.Id}:{u.Pos.Q},{u.Pos.R}:{u.Personnel}:{u.Status}:{u.FortLevel}")));
        sb.Append('#');
        sb.Append(string.Join("|", AirControl
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value.ToString("F3", CultureInfo.InvariantCulture)}")));
        return Fnv1a(sb.ToString());
    }

    private static string Fnv1a(string s)
    {
        var h = 2166136261u;
        // 按 UTF-16 code unit 累加，与 Dart 的 codeUnits 取法一致
        foreach (var c in s) h = unchecked((h ^ c) * 16777619u);
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>Create a deep copy of the game state (for turn resolution rollback).</summary>
    public GameState Clone()
    {
        return new GameState
        {
            Grid = Grid,
            Cells = new Dictionary<string, Cell>(Cells),
            Units = Units.Select(u => u.Clone()).ToList(),
            Rules = Rules,
            Terrains = new Dictionary<string, TerrainDef>(Terrains),
            UnitTypes = new Dictionary<string, UnitTypeDef>(UnitTypes),
            Weathers = new Dictionary<string, WeatherDef>(Weathers),
            ScenarioId = ScenarioId,
            ScenarioName = ScenarioName,
            Turn = Turn,
            MaxTurns = MaxTurns,
            HourOfDay = HourOfDay,
            Phase = Phase,
            Weather = Weather,
            WeatherMode = WeatherMode,
            WeatherSchedule = WeatherSchedule?.ToList(),
            WeatherScheduleState = WeatherScheduleState,
            NightFrom = NightFrom,
            NightTo = NightTo,
            Over = Over,
            Winner = Winner,
            EndedTurn = EndedTurn,
            Flags = new Dictionary<string, object>(Flags),
            VictoryConds = new List<VictoryCond>(VictoryConds),
            Obs = Obs.ToDictionary(
                kvp => kvp.Key,
                kvp => new Dictionary<string, int>(kvp.Value)),
            Tracks = Tracks.ToDictionary(
                kvp => kvp.Key,
                kvp => new Dictionary<string, Track>(kvp.Value)),
            AirControl = new Dictionary<string, double>(AirControl),
            Events = new List<WarEvent>(Events),
            InstanceId = InstanceId,
            Name = Name,
            Season = Season,
            Seed = Seed,
            RngState = RngState, // 副本回滚不能顺带把随机源拨回原点
        };
    }
}

/// <summary>Turn event log entry (for replay and director view).</summary>
public sealed class TurnEvent
{
    public int Turn { get; init; }
    public string Phase { get; init; } = "";
    public string Type { get; init; } = ""; // move/fire/supply/route/etc.
    public string UnitId { get; init; } = "";
    public string Detail { get; init; } = "";
    public Dictionary<string, object> Data { get; init; } = new();
}

/// <summary>
/// 天气计划条目：[FromTurn, ToTurn] 回合闭区间内强制为 Weather。
/// 存档序列化为 snake_case（from_turn/to_turn/weather），旧档缺键回落默认。
/// </summary>
public sealed class WeatherPhase
{
    public int FromTurn { get; init; } = 1;
    public int ToTurn { get; init; } = 1;
    public string Weather { get; init; } = "clear";
}
