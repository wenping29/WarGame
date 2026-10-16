using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;

namespace HexFront.Core.Models;

/// <summary>
/// Single unit instance on the map. Ported from state.dart Unit class.
/// </summary>
public sealed class Unit
{
    public string Id { get; init; } = "";
    public string Side { get; init; } = "";      // "red" | "blue" | "neutral"
    public string UnitType { get; init; } = "";  // References UnitTypeDef.Id
    public Hex Pos { get; set; }                  // Current position (mutable for movement)
    public int HP { get; set; }                   // Current hit points
    public int MaxHP { get; init; }
    public double Morale { get; set; }            // 0-100
    public double Fatigue { get; set; }           // 0-100 (higher = more tired)
    public double Ammo { get; set; }              // 0.0-1.0 ammunition level
    public double Fuel { get; set; }              // 0.0-1.0 fuel level
    public bool Alive => HP > 0;
    public bool Routed => Morale <= 0;
    public int MovesLeft { get; set; }            // Movement points remaining this turn
    public bool HasMoved { get; set; }
    public bool HasFired { get; set; }
    public List<string> Orders { get; init; } = new(); // Pending orders for this turn
    
    public Unit Clone() => (Unit)MemberwiseClone();
}

/// <summary>Victory condition types.</summary>
public enum VictoryType
{
    Attrition,      // Eliminate enemy units
    HoldObjective,  // Hold specific hexes
    ControlNodes,   // Control N objective nodes
    ExitMap,        // Move units off map edge
    TimedHold       // Hold until turn N
}

/// <summary>Victory condition definition.</summary>
public sealed class VictoryCond
{
    public VictoryType Type { get; init; }
    public string Side { get; init; } = "";
    public int Amount { get; init; }          // e.g., number of nodes to hold
    public List<string> HexKeys { get; init; } = new(); // Specific hexes to hold/exit
    public int? UntilTurn { get; init; }      // For timed conditions
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
    public int CurrentTurn { get; set; }
    public string Phase { get; set; } = "orders"; // orders/movement/combat/resolution
    public string? Weather { get; set; }
    
    // Game flags (optional modifiers)
    public Dictionary<string, bool> Flags { get; init; } = new();
    
    // Victory conditions
    public List<VictoryCond> VictoryConds { get; init; } = new();
    
    // Observation history (for fog of war)
    public Dictionary<string, HashSet<string>> Observations { get; init; } = new(); // side → observed hex keys
    
    // Saved turn events (for replay)
    public List<TurnEvent> EventLog { get; init; } = new();

    /// <summary>Get cell at hex position.</summary>
    public Cell? GetCell(Hex h)
    {
        return Cells.GetValueOrDefault(h.Key);
    }

    /// <summary>Get unit type definition for a unit.</summary>
    public UnitTypeDef TypeOf(Unit u)
    {
        return UnitTypes.GetValueOrDefault(u.UnitType) ?? 
               new UnitTypeDef { Id = u.UnitType, Label = u.UnitType };
    }

    /// <summary>Get terrain definition for a cell.</summary>
    public TerrainDef TerrainOf(Cell c)
    {
        return Terrains.GetValueOrDefault(c.Terrain) ??
               new TerrainDef { Id = c.Terrain, Label = c.Terrain };
    }

    /// <summary>Check if a hex is within line of sight from another hex.</summary>
    public bool InLOS(Hex from, Hex to, string observerSide)
    {
        // Simplified: check if observer has observed this hex
        if (Observations.TryGetValue(observerSide, out var observed))
        {
            return observed.Contains(to.Key);
        }
        return false;
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
            CurrentTurn = CurrentTurn,
            Phase = Phase,
            Weather = Weather,
            Flags = new Dictionary<string, bool>(Flags),
            VictoryConds = new List<VictoryCond>(VictoryConds),
            Observations = Observations.ToDictionary(
                kvp => kvp.Key,
                kvp => new HashSet<string>(kvp.Value)),
            EventLog = new List<TurnEvent>(EventLog),
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
