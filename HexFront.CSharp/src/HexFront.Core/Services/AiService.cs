using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// AI decision system for automated unit control.
/// Implements decision trees for movement, combat, and mission assignment.
/// </summary>
public static class AiService
{
    /// <summary>AI difficulty level affecting decision quality.</summary>
    public enum Difficulty
    {
        Easy,    // Random choices, poor pathfinding
        Medium,  // Basic tactics, decent pathfinding
        Hard,    // Advanced tactics, optimal pathfinding
    }

    /// <summary>Generate orders for all AI-controlled units of a side.</summary>
    public static List<Command> GenerateOrders(GameState g, string side, Difficulty difficulty = Difficulty.Medium)
    {
        var orders = new List<Command>();
        var aiUnits = g.Units.Where(u => u.Side == side && u.Alive).ToList();

        foreach (var u in aiUnits)
        {
            var unitType = g.TypeOf(u);
            
            // Skip immobile units
            if (unitType.Mobility == 0) continue;

            // Decide action based on unit type and situation
            var action = DecideAction(g, u, unitType, difficulty);
            
            switch (action)
            {
                case "move":
                    var moveCmd = GenerateMoveOrder(g, u, difficulty);
                    if (moveCmd != null) orders.Add(moveCmd);
                    break;
                    
                case "attack":
                    var attackCmd = GenerateAttackOrder(g, u, difficulty);
                    if (attackCmd != null) orders.Add(attackCmd);
                    break;
                    
                case "fire":
                    orders.Add(new Command
                    {
                        Op = CommandOp.Fire,
                        UnitId = u.Id,
                        Zone = new Hex(u.Pos.Q, u.Pos.R),
                    });
                    break;
                    
                case "rest":
                    if (u.Fatigue > 60)
                    {
                        orders.Add(new Command
                        {
                            Op = CommandOp.Rest,
                            UnitId = u.Id,
                        });
                    }
                    break;
                    
                case "dig":
                    if (u.FortLevel < 2 && IsInDefensivePosition(g, u))
                    {
                        orders.Add(new Command
                        {
                            Op = CommandOp.Dig,
                            UnitId = u.Id,
                        });
                    }
                    break;
            }
        }

        return orders;
    }

    /// <summary>Decide best action for a unit based on situation.</summary>
    private static string DecideAction(GameState g, Unit u, UnitTypeDef type, Difficulty difficulty)
    {
        var enemies = FindNearbyEnemies(g, u, type.Recon * 3);
        var friends = FindNearbyFriends(g, u, type.Recon * 2);

        // Critical supply/fuel - rest or retreat
        if (u.Food < 0.3 || u.Fuel < 0.2)
            return "rest";

        // High fatigue - rest
        if (u.Fatigue > 70)
            return "rest";

        // Enemies nearby and can attack
        if (enemies.Count > 0 && type.Firepower > 0)
        {
            // Artillery prefers fire over direct attack
            if (type.Id == "artillery")
                return "fire";
            
            // Attack if we have advantage
            if (HasAdvantage(g, u, enemies.First()))
                return "attack";
        }

        // Defensive position with no immediate threats
        if (IsInDefensivePosition(g, u) && enemies.Count == 0)
            return "dig";

        // Move toward objectives or enemies
        return "move";
    }

    /// <summary>Generate movement order with pathfinding.</summary>
    private static Command? GenerateMoveOrder(GameState g, Unit u, Difficulty difficulty)
    {
        var target = FindBestTarget(g, u, difficulty);
        if (target == null) return null;

        // Find path using A* or BFS
        var path = FindPath(g, u.Pos, target.Value, difficulty);
        if (path == null || path.Count < 2) return null;

        return new Command
        {
            Op = CommandOp.Move,
            UnitId = u.Id,
            Path = path.Skip(1).ToList(), // Exclude starting position
        };
    }

    /// <summary>Generate attack order against best target.</summary>
    private static Command? GenerateAttackOrder(GameState g, Unit u, Difficulty difficulty)
    {
        var enemies = FindNearbyEnemies(g, u, 5);
        if (enemies.Count == 0) return null;

        // Select weakest enemy or highest priority target
        var target = SelectBestTarget(enemies, difficulty);
        if (target == null) return null;

        return new Command
        {
            Op = CommandOp.Attack,
            UnitId = u.Id,
            Target = target.Id,
        };
    }

    /// <summary>Find best hex to move toward based on objectives.</summary>
    private static Hex? FindBestTarget(GameState g, Unit u, Difficulty difficulty)
    {
        var rng = new Random();

        if (difficulty == Difficulty.Easy)
        {
            // Random adjacent hex
            var directions = Hex.Directions;
            var dir = directions[rng.Next(directions.Length)];
            return new Hex(u.Pos.Q + dir.Q, u.Pos.R + dir.R);
        }

        // Medium/Hard: Move toward nearest enemy or objective
        var enemies = FindNearbyEnemies(g, u, 10);
        if (enemies.Count > 0)
        {
            var nearest = enemies.OrderBy(e => u.Pos.DistanceTo(e.Pos)).First();
            return GetNextStepToward(g, u.Pos, nearest.Pos);
        }

        // No enemies: move toward center of map or random
        return new Hex(u.Pos.Q + rng.Next(-2, 3), u.Pos.R + rng.Next(-2, 3));
    }

    /// <summary>Simple pathfinding (BFS for short distances).</summary>
    private static List<Hex>? FindPath(GameState g, Hex start, Hex end, Difficulty difficulty)
    {
        if (difficulty == Difficulty.Easy && start.DistanceTo(end) > 3)
            return null; // Easy AI has poor pathfinding

        var queue = new Queue<(Hex pos, List<Hex> path)>();
        var visited = new HashSet<string>();
        
        queue.Enqueue((start, new List<Hex> { start }));
        visited.Add(start.Key);

        var maxSteps = difficulty == Difficulty.Hard ? 20 : 10;

        while (queue.Count > 0 && maxSteps-- > 0)
        {
            var (current, path) = queue.Dequeue();

            if (current == end)
                return path;

            foreach (var neighbor in current.Neighbors())
            {
                var key = neighbor.Key;
                if (visited.Contains(key)) continue;

                var cell = g.GetCell(neighbor);
                if (cell == null) continue;

                var terrain = g.TerrainOf(neighbor);
                if (terrain.Impassable) continue;

                visited.Add(key);
                var newPath = new List<Hex>(path) { neighbor };
                queue.Enqueue((neighbor, newPath));
            }
        }

        return null;
    }

    /// <summary>Get next step toward target hex.</summary>
    private static Hex GetNextStepToward(GameState g, Hex from, Hex to)
    {
        var directions = Hex.Directions;

        // Pick direction that reduces distance most
        var bestDir = directions.OrderBy(d =>
        {
            var next = new Hex(from.Q + d.Q, from.R + d.R);
            return next.DistanceTo(to);
        }).First();

        return new Hex(from.Q + bestDir.Q, from.R + bestDir.R);
    }

    /// <summary>Find enemy units within range.</summary>
    private static List<Unit> FindNearbyEnemies(GameState g, Unit u, int range)
    {
        return g.Units.Where(other =>
            other.Side != u.Side &&
            other.Alive &&
            u.Pos.DistanceTo(other.Pos) <= range
        ).ToList();
    }

    /// <summary>Find friendly units within range.</summary>
    private static List<Unit> FindNearbyFriends(GameState g, Unit u, int range)
    {
        return g.Units.Where(other =>
            other.Side == u.Side &&
            other.Id != u.Id &&
            other.Alive &&
            u.Pos.DistanceTo(other.Pos) <= range
        ).ToList();
    }

    /// <summary>Select best enemy target based on weakness/priority.</summary>
    private static Unit? SelectBestTarget(List<Unit> enemies, Difficulty difficulty)
    {
        if (enemies.Count == 0) return null;

        if (difficulty == Difficulty.Easy)
        {
            var rng = new Random();
            return enemies[rng.Next(enemies.Count)];
        }

        // Medium/Hard: Pick weakest or most damaged
        return enemies.OrderBy(e => e.Personnel / (double)e.MaxPersonnel).First();
    }

    /// <summary>Check if attacker has advantage over defender.</summary>
    private static bool HasAdvantage(GameState g, Unit atk, Unit def)
    {
        var atkType = g.TypeOf(atk);
        var defType = g.TypeOf(def);
        var defTerrain = g.TerrainOf(def.Pos);

        var atkPower = atkType.Firepower * atk.Morale / 100.0 * atk.Personnel;
        var defPower = defType.Protection * def.Morale / 100.0 * def.Personnel * (1.0 + defTerrain.Defense * 0.2);

        return atkPower > defPower;
    }

    /// <summary>Check if unit is in a good defensive position.</summary>
    private static bool IsInDefensivePosition(GameState g, Unit u)
    {
        var terrain = g.TerrainOf(u.Pos);
        var cell = g.GetCell(u.Pos);

        // Good defense: hills, woods, city, or fortified
        return terrain.Defense >= 2.0 || 
               (cell?.City ?? 0) > 0 || 
               u.FortLevel > 0;
    }
}
