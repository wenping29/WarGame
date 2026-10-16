using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Scenario configuration loader for loading game setups from JSON files.
/// Handles OOB (Order of Battle), terrain specs, victory conditions, and initial deployments.
/// </summary>
public static class ScenarioService
{
    /// <summary>Scenario configuration structure.</summary>
    public class ScenarioConfig
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public int MaxTurns { get; init; } = 14;
        public string WeatherMode { get; init; } = "director";
        public bool StrictSupply { get; init; } = false;
        
        public List<UnitDeployment> Units { get; init; } = new();
        public List<VictoryCondition> VictoryConds { get; init; } = new();
        public TerrainSpec? Terrain { get; init; }
        public Dictionary<string, object>? Flags { get; init; }
    }

    /// <summary>Unit deployment specification.</summary>
    public class UnitDeployment
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "infantry";
        public string Side { get; init; } = "red";
        public int Q { get; init; }
        public int R { get; init; }
        public double Morale { get; init; } = 80;
        public double Fatigue { get; init; } = 0;
        public double Ammo { get; init; } = 1.0;
        public double Fuel { get; init; } = 1.0;
        public int Personnel { get; init; } = -1; // -1 means use type default
    }

    /// <summary>Victory condition specification.</summary>
    public class VictoryCondition
    {
        public string Type { get; init; } = "attrition";
        public int RedThreshold { get; init; }
        public int BlueThreshold { get; init; }
        public int RedHoldCount { get; init; }
        public int BlueHoldCount { get; init; }
    }

    /// <summary>Terrain specification for generation.</summary>
    public class TerrainSpec
    {
        public double[] Bbox { get; init; } = new double[4];
        public double CellKm { get; init; } = 2.5;
        public int Seed { get; init; } = 42;
        public bool Offline { get; init; } = true;
        public bool Features { get; init; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>Load scenario from JSON file path.</summary>
    public static ScenarioConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Scenario file not found: {path}");

        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    /// <summary>Load scenario from JSON string.</summary>
    public static ScenarioConfig LoadFromJson(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<ScenarioConfig>(json, JsonOptions);
            return config ?? throw new InvalidOperationException("Failed to deserialize scenario");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid scenario JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Create a GameState from scenario config.</summary>
    public static GameState CreateGameState(ScenarioConfig config)
    {
        var g = new GameState
        {
            Turn = 1,
            MaxTurns = config.MaxTurns,
            HourOfDay = 6,
            Weather = "clear",
            WeatherMode = config.WeatherMode,
            Over = false,
            Winner = null,
            Rules = LibraryService.DEFAULT_RULES,
            Flags = config.Flags ?? new Dictionary<string, object>(),
            VictoryConds = new List<VictoryCond>(),
            Units = new List<Unit>(),
            Cells = new Dictionary<string, Cell>(),
            Obs = new Dictionary<string, Dictionary<string, double>>(),
            AirControl = new Dictionary<string, double>(),
            Events = new List<Track>(),
        };

        // Parse victory conditions
        foreach (var vc in config.VictoryConds)
        {
            g.VictoryConds.Add(new VictoryCond
            {
                Type = ParseVictoryType(vc.Type),
                RedThreshold = vc.RedThreshold,
                BlueThreshold = vc.BlueThreshold,
                RedHoldCount = vc.RedHoldCount,
                BlueHoldCount = vc.BlueHoldCount,
            });
        }

        // Deploy units
        foreach (var dep in config.Units)
        {
            var unitType = LibraryService.GetUnitType(dep.Type);
            var personnel = dep.Personnel > 0 ? dep.Personnel : unitType.Personnel;

            var u = new Unit
            {
                Id = dep.Id,
                Side = dep.Side,
                TypeId = dep.Type,
                Q = dep.Q,
                R = dep.R,
                Personnel = personnel,
                MaxPersonnel = personnel,
                Morale = dep.Morale,
                Fatigue = dep.Fatigue,
                Ammo = dep.Ammo,
                Fuel = dep.Fuel,
                Food = 1.0,
                SupplyRate = 1.0,
                Status = UnitStatus.Ready,
                Casualties = 0,
                Airborne = false,
                Mission = "none",
                FortLevel = 0,
            };

            g.Units.Add(u);
        }

        return g;
    }

    /// <summary>Get list of available scenarios from directory.</summary>
    public static List<ScenarioInfo> ListScenarios(string directory)
    {
        var scenarios = new List<ScenarioInfo>();

        if (!Directory.Exists(directory))
            return scenarios;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var config = LoadFromFile(file);
                scenarios.Add(new ScenarioInfo
                {
                    Id = config.Id,
                    Name = config.Name,
                    Description = config.Description,
                    Path = file,
                });
            }
            catch
            {
                // Skip invalid files
            }
        }

        return scenarios;
    }

    /// <summary>Scenario metadata for listing.</summary>
    public class ScenarioInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Path { get; init; } = "";
    }

    private static VictoryType ParseVictoryType(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "attrition" => VictoryType.Attrition,
            "hold_objective" => VictoryType.HoldObjective,
            "control_nodes" => VictoryType.ControlNodes,
            _ => VictoryType.Attrition,
        };
    }
}
