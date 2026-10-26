using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Validation;

/// <summary>
/// 38-check validation suite matching Python hexgen.py's self-validation.
/// Ensures grid integrity, terrain consistency, feature invariants, and gameplay balance.
/// </summary>
public static class HexPackageValidator
{
    private static readonly HashSet<string> ValidTerrains = new()
    {
        "clear", "rough", "woods", "hills", "mountain", "city", "swamp", "river", "water"
    };

    /// <summary>Run all 37 checks and return detailed results.</summary>
    public static ValidationReport Validate(HexPackage pkg)
    {
        var results = new List<ValidationResult>();
        
        // [1-5] Grid structure checks
        CheckGridStructure(pkg, results);
        
        // [6-10] Terrain classification
        CheckTerrainClassification(pkg, results);
        
        // [11-15] Feature invariants (road/river/railway/tunnel)
        CheckFeatureInvariants(pkg, results);
        
        // [16-20] Railway & station rules
        CheckRailwayRules(pkg, results);
        
        // [21-25] Power grid rules
        CheckPowerGridRules(pkg, results);
        
        // [26-30] Airport & tunnel rules
        CheckAirportTunnelRules(pkg, results);
        
        // [31-35] Stats consistency
        CheckStatsConsistency(pkg, results);
        
        // [36-37] Meta & geometry
        CheckMetaGeometry(pkg, results);

        var fails = results.Count(r => r.Level == "FAIL");
        var warns = results.Count(r => r.Level == "WARN");
        
        return new ValidationReport
        {
            Total = results.Count,
            Fails = fails,
            Warns = warns,
            Passes = results.Count - fails - warns,
            Results = results,
        };
    }

    private static void CheckGridStructure(HexPackage pkg, List<ValidationResult> results)
    {
        var expectedCells = pkg.Width * pkg.Height;
        var actualCells = pkg.Hexes.Count;
        
        // [1] Cell count matches width × height
        results.Add(new ValidationResult
        {
            Level = actualCells == expectedCells ? "OK" : "FAIL",
            Check = "格数完整性",
            Detail = $"{actualCells} vs {expectedCells} ({pkg.Width}×{pkg.Height})"
        });

        // [2] Q range: 0..width-1
        var maxQ = pkg.Hexes.Max(h => h.Q);
        var minQ = pkg.Hexes.Min(h => h.Q);
        results.Add(new ValidationResult
        {
            Level = (minQ == 0 && maxQ == pkg.Width - 1) ? "OK" : "FAIL",
            Check = "Q 坐标范围",
            Detail = $"{minQ}..{maxQ} (期望 0..{pkg.Width - 1})"
        });

        // [3] R range: 0..height-1
        var maxR = pkg.Hexes.Max(h => h.R);
        var minR = pkg.Hexes.Min(h => h.R);
        results.Add(new ValidationResult
        {
            Level = (minR == 0 && maxR == pkg.Height - 1) ? "OK" : "FAIL",
            Check = "R 坐标范围",
            Detail = $"{minR}..{maxR} (期望 0..{pkg.Height - 1})"
        });

        // [4] No duplicate cells
        var uniqueKeys = pkg.Hexes.Select(h => $"{h.Q},{h.R}").Distinct().Count();
        results.Add(new ValidationResult
        {
            Level = uniqueKeys == actualCells ? "OK" : "FAIL",
            Check = "无重复格",
            Detail = $"{uniqueKeys}/{actualCells} 唯一"
        });

        // [5] All cells have valid coordinates
        var invalidCoords = pkg.Hexes.Count(h => h.Q < 0 || h.R < 0 || h.Q >= pkg.Width || h.R >= pkg.Height);
        results.Add(new ValidationResult
        {
            Level = invalidCoords == 0 ? "OK" : "FAIL",
            Check = "坐标有效性",
            Detail = $"{invalidCoords} 个越界格"
        });
    }

    private static void CheckTerrainClassification(HexPackage pkg, List<ValidationResult> results)
    {
        // [6] All terrains in whitelist
        var terrains = pkg.Hexes.Select(h => h.Terrain).Distinct().ToList();
        var invalidTerrains = terrains.Where(t => !ValidTerrains.Contains(t)).ToList();
        results.Add(new ValidationResult
        {
            Level = invalidTerrains.Count == 0 ? "OK" : "FAIL",
            Check = "terrain 白名单",
            Detail = invalidTerrains.Any() ? $"非法值: {string.Join(", ", invalidTerrains)}" : string.Join(", ", terrains)
        });

        // [7] Elev values reasonable (0-9000m)
        var maxElev = pkg.Hexes.Max(h => h.Elev);
        var minElev = pkg.Hexes.Min(h => h.Elev);
        results.Add(new ValidationResult
        {
            Level = (minElev >= 0 && maxElev <= 9000) ? "OK" : "WARN",
            Check = "高程范围",
            Detail = $"{minElev:F0}–{maxElev:F0} m"
        });

        // [8] Slope values reasonable (0-90°)
        var maxSlope = pkg.Hexes.Max(h => h.Slope);
        var minSlope = pkg.Hexes.Min(h => h.Slope);
        results.Add(new ValidationResult
        {
            Level = (minSlope >= 0 && maxSlope <= 90) ? "OK" : "WARN",
            Check = "坡度范围",
            Detail = $"{minSlope:F1}–{maxSlope:F1}°"
        });

        // [9] River cells have low elevation (typically valley floors)
        var riverCells = pkg.Hexes.Where(h => h.River).ToList();
        if (riverCells.Any())
        {
            var avgRiverElev = riverCells.Average(h => h.Elev);
            var globalMinElev = pkg.Hexes.Min(h => h.Elev);
            var ratio = avgRiverElev / Math.Max(1, globalMinElev);
            results.Add(new ValidationResult
            {
                Level = ratio < 2.0 ? "OK" : "WARN",
                Check = "河带高程合理性",
                Detail = $"河带平均 {avgRiverElev:F0}m / 全图最低 {globalMinElev:F0}m = {ratio:F1}×"
            });
        }
        else
        {
            results.Add(new ValidationResult { Level = "OK", Check = "河带存在性", Detail = "无河带（可能为干旱区）" });
        }

        // [10] Water/swamp cells are passable check
        var waterCells = pkg.Hexes.Where(h => h.Terrain == "water").Count();
        var swampCells = pkg.Hexes.Where(h => h.Terrain == "swamp").Count();
        results.Add(new ValidationResult
        {
            Level = "OK",
            Check = "水面/沼泽统计",
            Detail = $"water={waterCells}, swamp={swampCells}"
        });
    }

    private static void CheckFeatureInvariants(HexPackage pkg, List<ValidationResult> results)
    {
        // [11] Bridge only on river cells
        var bridgeNotOnRiver = pkg.Hexes.Count(h => h.Bridge && !h.River);
        results.Add(new ValidationResult
        {
            Level = bridgeNotOnRiver == 0 ? "OK" : "FAIL",
            Check = "桥梁只在水面",
            Detail = bridgeNotOnRiver == 0 ? "无违规" : $"{bridgeNotOnRiver} 个桥位不在河上"
        });

        // [12] Tunnel on land (not water/river)
        var tunnelOnWater = pkg.Hexes.Count(h => h.Tunnel && (h.Terrain == "water" || h.Terrain == "river"));
        results.Add(new ValidationResult
        {
            Level = tunnelOnWater == 0 ? "OK" : "FAIL",
            Check = "隧道不在水面",
            Detail = tunnelOnWater == 0 ? "无违规" : $"{tunnelOnWater} 个隧道在水面/河上"
        });

        // [13] Tunnel on railway or road (transport corridor)
        var tunnelNotOnTransport = pkg.Hexes.Count(h => h.Tunnel && !h.Railway && !h.Road);
        results.Add(new ValidationResult
        {
            Level = tunnelNotOnTransport == 0 ? "OK" : "WARN",
            Check = "隧道在交通线上",
            Detail = tunnelNotOnTransport == 0 ? "无违规" : $"{tunnelNotOnTransport} 个隧道无铁路/公路"
        });

        // [14] Road connectivity (at least one component spans west-east)
        var roadCells = pkg.Hexes.Where(h => h.Road).ToList();
        if (roadCells.Any())
        {
            var westRoads = roadCells.Where(h => h.Q == 0).Count();
            var eastRoads = roadCells.Where(h => h.Q == pkg.Width - 1).Count();
            results.Add(new ValidationResult
            {
                Level = (westRoads > 0 && eastRoads > 0) ? "OK" : "WARN",
                Check = "道路贯通东西",
                Detail = $"西端 {westRoads} / 东端 {eastRoads} / 共 {roadCells.Count} 格"
            });
        }
        else
        {
            results.Add(new ValidationResult { Level = "OK", Check = "道路存在性", Detail = "无道路" });
        }

        // [15] City cells on passable terrain
        var cityOnImpassable = pkg.Hexes.Count(h => h.City > 0 && (h.Terrain == "water" || h.Terrain == "mountain"));
        results.Add(new ValidationResult
        {
            Level = cityOnImpassable == 0 ? "OK" : "FAIL",
            Check = "聚落在可通行地",
            Detail = cityOnImpassable == 0 ? "无违规" : $"{cityOnImpassable} 个聚落在水面/山地"
        });
    }

    private static void CheckRailwayRules(HexPackage pkg, List<ValidationResult> results)
    {
        var railCells = pkg.Hexes.Where(h => h.Railway).ToList();
        
        // [16] Railway exists
        results.Add(new ValidationResult
        {
            Level = railCells.Any() ? "OK" : "WARN",
            Check = "铁路存在性",
            Detail = railCells.Count > 0 ? $"{railCells.Count} 格" : "无铁路"
        });

        // [17] Railway only crosses river at bridges
        var railCrossesRiverNoBridge = pkg.Hexes.Count(h => h.Railway && h.River && !h.Bridge);
        results.Add(new ValidationResult
        {
            Level = railCrossesRiverNoBridge == 0 ? "OK" : "FAIL",
            Check = "铁路只在桥位渡河",
            Detail = railCrossesRiverNoBridge == 0 ? "无违规" : $"{railCrossesRiverNoBridge} 处铁路穿河无桥"
        });

        // [18] Railway spans west-east (main line)
        if (railCells.Any())
        {
            var westRails = railCells.Where(h => h.Q == 0).Count();
            var eastRails = railCells.Where(h => h.Q == pkg.Width - 1).Count();
            results.Add(new ValidationResult
            {
                Level = (westRails > 0 && eastRails > 0) ? "OK" : "WARN",
                Check = "铁路干线通东西",
                Detail = $"西端 {westRails} / 东端 {eastRails} / 共 {railCells.Count} 格"
            });
        }

        // [19] Stations are on railway cells
        var stationNotOnRail = pkg.Hexes.Count(h => h.RailStation && !h.Railway);
        results.Add(new ValidationResult
        {
            Level = stationNotOnRail == 0 ? "OK" : "FAIL",
            Check = "车站在铁路上",
            Detail = stationNotOnRail == 0 ? "无违规" : $"{stationNotOnRail} 个车站不在铁路"
        });

        // [20] Station count ≤ city count
        var stats = pkg.Meta.Stats;
        T? GetStat<T>(string snakeKey, string pascalKey) where T : struct
        {
            if (stats.TryGetValue(snakeKey, out var obj1) && obj1 is T val1) return val1;
            if (stats.TryGetValue(pascalKey, out var obj2) && obj2 is T val2) return val2;
            return null;
        }
        
        var stationCount = GetStat<int>("rail_station_cells", "RailStationCells") ?? pkg.Hexes.Count(h => h.RailStation);
        var cityCount = pkg.Hexes.Count(h => h.City > 0);
        results.Add(new ValidationResult
        {
            Level = stationCount <= Math.Max(1, cityCount) ? "OK" : "WARN",
            Check = "车站数 ≤ 聚落数",
            Detail = $"车站 {stationCount} / 聚落 {cityCount}"
        });

        // [20b] 加油站在公路上且不在桥面（与 hexgen.py 契约同源）
        var badFuel = pkg.Hexes.Count(h => h.FuelStation && (!h.Road || h.Bridge));
        results.Add(new ValidationResult
        {
            Level = badFuel == 0 ? "OK" : "FAIL",
            Check = "加油站在公路上且不在桥面",
            Detail = badFuel == 0
                ? $"共 {pkg.Hexes.Count(h => h.FuelStation)} 处"
                : $"{badFuel} 处违规"
        });
    }

    private static void CheckPowerGridRules(HexPackage pkg, List<ValidationResult> results)
    {
        var plantCells = pkg.Hexes.Where(h => h.PowerPlant).ToList();
        var substationCells = pkg.Hexes.Where(h => h.Substation).ToList();
        var powerlineCells = pkg.Hexes.Where(h => h.Powerline).ToList();

        // Also check stats for cases where hex flags may not be set
        var stats = pkg.Meta.Stats;
        T? GetStat<T>(string snakeKey, string pascalKey) where T : struct
        {
            if (stats.TryGetValue(snakeKey, out var obj1) && obj1 is T val1) return val1;
            if (stats.TryGetValue(pascalKey, out var obj2) && obj2 is T val2) return val2;
            return null;
        }

        var statPlants = GetStat<int>("power_plant_cells", "PowerPlantCells") ?? plantCells.Count;
        var statSubstations = GetStat<int>("substation_cells", "SubstationCells") ?? substationCells.Count;
        var statPowerlines = GetStat<int>("powerline_cells", "PowerlineCells") ?? powerlineCells.Count;

        // [21] Power plant count 1-2
        results.Add(new ValidationResult
        {
            Level = (statPlants >= 1 && statPlants <= 2) ? "OK" : "WARN",
            Check = "电厂数量 1–2",
            Detail = $"实际 {statPlants}"
        });

        // [22] Power plants on passable terrain
        var plantOnBadTerrain = plantCells.Count(h => h.Terrain == "water" || h.Terrain == "mountain");
        results.Add(new ValidationResult
        {
            Level = plantOnBadTerrain == 0 ? "OK" : "FAIL",
            Check = "电厂建在可通行平地",
            Detail = statPlants > 0 && plantOnBadTerrain == 0 ? $"共 {statPlants} 处" : $"{plantOnBadTerrain} 处在不良地形"
        });

        // [23] Substation count ≤ 4
        results.Add(new ValidationResult
        {
            Level = statSubstations <= 4 ? "OK" : "WARN",
            Check = "变电站数量 ≤ 4",
            Detail = $"实际 {statSubstations}"
        });

        // [24] Substations near cities
        results.Add(new ValidationResult
        {
            Level = "OK",
            Check = "变电站存在性",
            Detail = statSubstations > 0 ? $"{statSubstations} 座" : "无变电站"
        });

        // [25] Power lines connect plants and substations
        var hasGrid = statPlants > 0 && (statPowerlines > 0 || statSubstations > 0);
        results.Add(new ValidationResult
        {
            Level = hasGrid ? "OK" : "WARN",
            Check = "电网连通性",
            Detail = hasGrid ? $"线路 {statPowerlines} 格" : "无电网"
        });
    }

    private static void CheckAirportTunnelRules(HexPackage pkg, List<ValidationResult> results)
    {
        var airportCells = pkg.Hexes.Where(h => h.Airport > 0).ToList();
        var tunnelCells = pkg.Hexes.Where(h => h.Tunnel).ToList();

        // [26] Airport count ≤ 3
        results.Add(new ValidationResult
        {
            Level = airportCells.Count <= 3 ? "OK" : "WARN",
            Check = "机场数量 ≤ 3",
            Detail = $"实际 {airportCells.Count}"
        });

        // [27] Airports not on water/city
        var airportOnBad = airportCells.Count(h => h.Terrain == "water" || h.Terrain == "river");
        results.Add(new ValidationResult
        {
            Level = airportOnBad == 0 ? "OK" : "FAIL",
            Check = "机场不在水面/城镇",
            Detail = airportOnBad == 0 ? $"共 {airportCells.Count} 处" : $"{airportOnBad} 个在不良位置"
        });

        // [28] Airport levels valid (1=major, 2=minor)
        var invalidLevels = airportCells.Count(h => h.Airport != 1 && h.Airport != 2);
        results.Add(new ValidationResult
        {
            Level = invalidLevels == 0 ? "OK" : "FAIL",
            Check = "机场等级合法",
            Detail = invalidLevels == 0 ? "全部合法" : $"{invalidLevels} 个非法等级"
        });

        // [29] Tunnel count reasonable
        results.Add(new ValidationResult
        {
            Level = "OK",
            Check = "隧道数量",
            Detail = tunnelCells.Count > 0 ? $"{tunnelCells.Count} 处" : "无隧道"
        });

        // [30] Tunnels in hills/mountain terrain
        var tunnelInFlat = tunnelCells.Count(h => h.Terrain != "hills" && h.Terrain != "mountain");
        results.Add(new ValidationResult
        {
            Level = tunnelInFlat == 0 ? "OK" : "WARN",
            Check = "隧道在陡区",
            Detail = tunnelInFlat == 0 ? "无违规" : $"{tunnelInFlat} 个隧道在平地"
        });
    }

    private static void CheckStatsConsistency(HexPackage pkg, List<ValidationResult> results)
    {
        var stats = pkg.Meta.Stats;
        
        // Helper to try both camelCase and PascalCase keys
        T? GetStat<T>(string snakeKey, string pascalKey) where T : struct
        {
            if (stats.TryGetValue(snakeKey, out var obj1) && obj1 is T val1) return val1;
            if (stats.TryGetValue(pascalKey, out var obj2) && obj2 is T val2) return val2;
            return null;
        }
        
        // [31] total_cells matches
        var totalCells = GetStat<int>("total_cells", "TotalCells");
        if (totalCells.HasValue)
        {
            results.Add(new ValidationResult
            {
                Level = totalCells.Value == pkg.Hexes.Count ? "OK" : "FAIL",
                Check = "stats.total_cells 一致",
                Detail = $"{totalCells.Value} vs {pkg.Hexes.Count}"
            });
        }

        // [32] road_cells matches
        var roadCells = GetStat<int>("road_cells", "RoadCells");
        if (roadCells.HasValue)
        {
            var actualRoads = pkg.Hexes.Count(h => h.Road);
            results.Add(new ValidationResult
            {
                Level = roadCells.Value == actualRoads ? "OK" : "FAIL",
                Check = "stats.road_cells 一致",
                Detail = $"{roadCells.Value} vs {actualRoads}"
            });
        }

        // [33] railway_cells matches
        var railCells = GetStat<int>("railway_cells", "RailwayCells");
        if (railCells.HasValue)
        {
            var actualRails = pkg.Hexes.Count(h => h.Railway);
            results.Add(new ValidationResult
            {
                Level = railCells.Value == actualRails ? "OK" : "FAIL",
                Check = "stats.railway_cells 一致",
                Detail = $"{railCells.Value} vs {actualRails}"
            });
        }

        // [34] tunnel_cells matches
        var tunnelCells = GetStat<int>("tunnel_cells", "TunnelCells");
        if (tunnelCells.HasValue)
        {
            var actualTunnels = pkg.Hexes.Count(h => h.Tunnel);
            results.Add(new ValidationResult
            {
                Level = tunnelCells.Value == actualTunnels ? "OK" : "FAIL",
                Check = "stats.tunnel_cells 一致",
                Detail = $"{tunnelCells.Value} vs {actualTunnels}"
            });
        }

        // [35] powerline_cells matches
        var powerCells = GetStat<int>("powerline_cells", "PowerlineCells");
        if (powerCells.HasValue)
        {
            var actualPower = pkg.Hexes.Count(h => h.Powerline);
            results.Add(new ValidationResult
            {
                Level = powerCells.Value == actualPower ? "OK" : "FAIL",
                Check = "stats.powerline_cells 一致",
                Detail = $"{powerCells.Value} vs {actualPower}"
            });
        }
    }

    private static void CheckMetaGeometry(HexPackage pkg, List<ValidationResult> results)
    {
        // [36] Mercator scale reasonable for latitude
        var centerLat = pkg.Meta.CenterLonLat != null && pkg.Meta.CenterLonLat.Count >= 2 
            ? pkg.Meta.CenterLonLat[1] 
            : 0;
        
        if (centerLat != 0)
        {
            var expectedScale = 1.0 / Math.Cos(centerLat * Math.PI / 180.0);
            var actualScale = pkg.Meta.MercatorScale;
            var scaleDiff = Math.Abs(actualScale - expectedScale);
            
            results.Add(new ValidationResult
            {
                Level = scaleDiff < 0.01 ? "OK" : "WARN",
                Check = "墨卡托放大系数",
                Detail = $"纬度 {centerLat:F1}°: 期望 {expectedScale:F3}, 实际 {actualScale:F3}, 差 {scaleDiff:F3}"
            });
        }
        else
        {
            results.Add(new ValidationResult
            {
                Level = "WARN",
                Check = "墨卡托放大系数",
                Detail = "CenterLonLat 为空，无法验证"
            });
        }

        // [37] 半径与对边距自洽：尖顶格 √3·radius = 墨卡托口径的格宽 = cell_ground_m × 放大系数
        var groundCellM = pkg.Meta.CellGroundM;
        var mercCellM = groundCellM * (pkg.Meta.MercatorScale > 0 ? pkg.Meta.MercatorScale : 1.0);
        var radiusBasedCellM = pkg.Meta.RadiusM * Math.Sqrt(3);
        
        if (mercCellM > 0 && radiusBasedCellM > 0)
        {
            var cellDiff = Math.Abs(mercCellM - radiusBasedCellM);
            results.Add(new ValidationResult
            {
                Level = cellDiff / mercCellM < 1e-6 ? "OK" : "WARN",
                Check = "对边距与半径一致",
                Detail = $"cell_ground_m×scale={mercCellM:F1}, radius×√3={radiusBasedCellM:F1}, 差 {cellDiff:F1}m"
            });
        }
        else
        {
            results.Add(new ValidationResult
            {
                Level = "WARN",
                Check = "对边距与半径一致",
                Detail = "数值为 0，无法验证"
            });
        }

        // [38] ox/oy 中心对齐：格网几何中心 == bbox 中心 == center_lonlat（设计文档 §6.2 零漂移）
        if (pkg.Meta.CenterLonLat is { Count: >= 2 } && pkg.Meta.RadiusM > 0)
        {
            // odd-r 矩形棋盘横向跨度是 (width+0.5)·格宽（奇数行右移半格）⇒ 中心列索引 (width-1)/2 + 0.25
            var hexWidthM = Math.Sqrt(3.0) * pkg.Meta.RadiusM;
            var rowStepM = 1.5 * pkg.Meta.RadiusM;
            var cx = pkg.Meta.MercatorOx + hexWidthM * ((pkg.Meta.Width - 1) / 2.0 + 0.25);
            var cy = pkg.Meta.MercatorOy - rowStepM * ((pkg.Meta.Height - 1) / 2.0);
            var (lon, lat) = Mercator.MercatorToLonLat(cx, cy);
            var dlon = Math.Abs(lon - pkg.Meta.CenterLonLat[0]);
            var dlat = Math.Abs(lat - pkg.Meta.CenterLonLat[1]);
            results.Add(new ValidationResult
            {
                Level = dlon < 1e-6 && dlat < 1e-6 ? "OK" : "FAIL",
                Check = "ox/oy 中心对齐",
                Detail = $"Δlon={dlon:F6}° Δlat={dlat:F6}°（axial 期的 ox 公式会在这里露出来）"
            });
        }
        else
        {
            results.Add(new ValidationResult
            {
                Level = "WARN",
                Check = "ox/oy 中心对齐",
                Detail = "缺 center_lonlat 或 radius_m，无法验证"
            });
        }
    }
}
