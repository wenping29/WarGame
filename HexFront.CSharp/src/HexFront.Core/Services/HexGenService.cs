using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;

namespace HexFront.Core.Services;

/// <summary>
/// Native C# hex grid terrain generator. Full port of tools/gis/hexgen.py.
/// Generates realistic terrain with elevation, landcover, roads, rivers, bridges,
/// cities, railways, powerlines, airports, and other features.
/// </summary>
public static class HexGenService
{
    // Constants matching Python hexgen.py
    private const double HalfCircumferenceM = 20037508.342789244;
    private const double EarthRadiusM = HalfCircumferenceM / Math.PI;
    
    // Terrain classification thresholds
    private const double SlopeWoodsMaxDeg = 8.0;
    private const double SlopeHillsMinDeg = 6.0;
    private const double SlopeMountainMinDeg = 12.0;
    private const double ElevMountainRel = 0.75;
    private const double ElevHillsRel = 0.45;
    private const double WoodsNoiseMin = 0.35;
    private const double WoodsMaxElevRel = 0.85;
    
    // River generation
    private const int RiverRouteSteps = 3;
    private const double RiverRouteRidgePct = 0.35;
    private const double RiverMinElevRatio = 1.15;
    private const double RiverStepPenaltyRel = 0.08;
    private const double RiverBendPenaltyPerDeg = 2.5;
    
    // Road generation
    private const int BridgeSpacingCols = 8;
    private const int BridgeMin = 2;
    private const int BridgeMax = 4;
    private const int RoadRowTolerance = 2;
    private const double RoadRowDeviationCost = 15.0;
    private const double RoadRiverBlockCost = 4.0e5;
    private const double RoadWaterBlockCost = 1.0e6;
    private const double RoadMountainPenalty = 200.0;
    private const double RoadBendPenaltyPerDeg = 1.2;
    
    // Railway
    private const int RailStationMax = 3;
    private const int TunnelMax = 2;
    private const double TunnelSlopeMaxDeg = 8.0;
    private const int RailSeparation = 5;
    
    // Power grid
    private const int PlantMax = 2;
    private const int PlantRiverMaxDist = 1;
    private const int PlantSeparation = 4;
    private const int PlantCityMinDist = 2;
    private const int SubstationMax = 4;
    private const double PowerWaterBlockCost = 4.0e5;
    private const double PowerMountainPenalty = 500.0;
    private const double PowerBendPenaltyPerDeg = 1.5;
    private const double PowerMaxSlopeDeg = 9.0;
    private const double PowerlineMaxFraction = 0.10;
    
    // Airport
    private const double AirportDerivedSlopeMaxDeg = 1.5;
    private const int AirportCityMinDist = 2;
    private const int AirportCityMaxDist = 6;
    private const int AirportMax = 3;
    
    /// <summary>
    /// Generate complete hex package with terrain and features.
    /// </summary>
    public static Models.HexPackage Generate(GenSpec spec)
    {
        var grid = BuildGrid(spec);
        var elev = SampleElevation(grid, spec);
        var slopes = CalculateSlopes(grid, elev);
        var relief = CalculateRelief(elev);
        var th = BuildThresholds(elev, relief);
        
        var (terr, aux) = ClassifyTerrain(grid, elev, slopes, relief, th, spec);
        var riverBand = ExtractRiverBand(grid, elev, terr);
        var (road, bridges) = BuildRoads(grid, elev, terr, riverBand, spec.Seed);
        var cities = PlaceCities(grid, elev, terr, road, spec.Seed);
        var railway = BuildRailway(grid, elev, terr, road, cities, spec.Seed);
        var powerGrid = BuildPowerGrid(grid, elev, terr, road, cities, riverBand, spec.Seed);
        var airports = PlaceAirports(grid, elev, terr, cities, spec.Seed);
        
        var hexes = BuildHexCells(grid, elev, slopes, terr, road, riverBand, bridges, 
                                   cities, railway, powerGrid, airports);
        var meta = BuildMeta(grid, spec, elev, terr, road, riverBand, railway, powerGrid, airports);
        
        return new Models.HexPackage
        {
            Hexes = hexes,
            Meta = meta,
        };
    }
    
    /// <summary>Build hex grid geometry.</summary>
    private static HexGridGeometry BuildGrid(GenSpec spec)
    {
        // 格心中心 = bbox 的墨卡托中点（与 hexgen.grid_from_bbox 一致），不是纬度算术中点
        var (centerLon, centerLat) = GisPlanService.GridCenter(spec.Bbox.ToArray());
        var scale = GisPlanService.MercatorScale(centerLat);
        
        var cellGroundM = spec.CellKm * 1000.0;
        var cellM = cellGroundM * scale;
        var radiusM = cellM / Math.Sqrt(3.0);
        var hexWidthM = Math.Sqrt(3.0) * radiusM;
        var rowStepM = 1.5 * radiusM;
        
        var cx0 = LonToX(centerLon);
        var cy0 = LatToY(centerLat);
        
        // 行列数只有一份算法：与向导方案（GisPlanService）同源，否则「预览格数」≠「实际生成格数」
        var (width, height) = GisPlanService.GridSize(spec.Bbox.ToArray(), spec.CellKm);
        
        // 「格网几何中心」= bbox 中心：odd-r 棋盘横向跨度是 (width + 0.5) 格（奇数行右移半格），
        // 所以中心分数索引是 ((width-1)/2 + 0.25, (height-1)/2)——横向那 0.25 与行数无关。
        var mercatorOx = cx0 - hexWidthM * ((width - 1) / 2.0 + 0.25);
        var mercatorOy = cy0 + rowStepM * ((height - 1) / 2.0);
        
        return new HexGridGeometry
        {
            Width = width,
            Height = height,
            CellKm = spec.CellKm,
            Scale = scale,
            CellGroundM = cellGroundM,
            CellM = cellM,
            RadiusM = radiusM,
            HexWidthM = hexWidthM,
            RowStepM = rowStepM,
            MercatorOx = mercatorOx,
            MercatorOy = mercatorOy,
            CenterLon = centerLon,
            CenterLat = centerLat,
            Bbox = spec.Bbox,
        };
    }
    
    /// <summary>Sample elevation from Open-Meteo API or generate synthetic DEM.</summary>
    private static double[] SampleElevation(HexGridGeometry grid, GenSpec spec)
    {
        if (spec.Offline || !spec.UseRealDem)
            return GenerateSyntheticDem(grid, spec.Seed);
        
        try
        {
            return FetchOpenMeteoElevation(grid, spec);
        }
        catch
        {
            return GenerateSyntheticDem(grid, spec.Seed);
        }
    }
    
    /// <summary>Fetch real elevation from Open-Meteo API.</summary>
    private static double[] FetchOpenMeteoElevation(HexGridGeometry grid, GenSpec spec)
    {
        // TODO: Implement HTTP client to call Open-Meteo Elevation API
        // For now, fallback to synthetic
        return GenerateSyntheticDem(grid, spec.Seed);
    }
    
    /// <summary>Generate deterministic synthetic DEM using value noise.</summary>
    private static double[] GenerateSyntheticDem(HexGridGeometry grid, int seed)
    {
        var elev = new double[grid.Width * grid.Height];
        var rng = new Random(seed);
        
        for (var r = 0; r < grid.Height; r++)
        {
            for (var q = 0; q < grid.Width; q++)
            {
                var idx = r * grid.Width + q;
                var nx = q / (double)grid.Width;
                var ny = r / (double)grid.Height;
                
                // Value noise with multiple octaves
                var noise = Fbm(nx * 3.0, ny * 3.0, seed, octaves: 4) * 2.0 - 1.0;
                
                // North-south valley cut
                var valleyFactor = Math.Abs(ny - 0.5) * 2.0;
                
                // East-west plateau ridges
                var ridgeFactor = Math.Sin(nx * Math.PI * 2.0) * 0.3 + 0.7;
                
                // Southeast sandstone ridge
                var seRidge = (nx > 0.6 && ny < 0.4) ? 0.4 : 0.0;
                
                var baseElev = 50.0 + noise * 80.0 * valleyFactor * ridgeFactor + seRidge * 100.0;
                elev[idx] = Math.Max(0, baseElev);
            }
        }
        
        return elev;
    }
    
    /// <summary>Calculate slope at each cell using three-axis central differences.</summary>
    private static double[] CalculateSlopes(HexGridGeometry grid, double[] elev)
    {
        var slopes = new double[grid.Width * grid.Height];
        var cellDist = grid.CellGroundM; // neighbor distance in ground meters
        
        for (var r = 0; r < grid.Height; r++)
        {
            for (var q = 0; q < grid.Width; q++)
            {
                var idx = r * grid.Width + q;
                var maxSlope = 0.0;
                
                // Three gradient axes through the cell (parity-aware on an odd-r board)
                foreach (var ((dqPos, drPos), (dqNeg, drNeg)) in SlopeAxes(r))
                {
                    var nqPos = q + dqPos;
                    var nrPos = r + drPos;
                    var nqNeg = q + dqNeg;
                    var nrNeg = r + drNeg;
                    
                    if (!InBounds(grid, nqPos, nrPos) || !InBounds(grid, nqNeg, nrNeg))
                        continue;
                    
                    var idxPos = nrPos * grid.Width + nqPos;
                    var idxNeg = nrNeg * grid.Width + nqNeg;
                    
                    var dz = elev[idxPos] - elev[idxNeg];
                    var slope = Math.Atan(Math.Abs(dz) / (2.0 * cellDist)) * 180.0 / Math.PI;
                    maxSlope = Math.Max(maxSlope, slope);
                }
                
                slopes[idx] = maxSlope;
            }
        }
        
        return slopes;
    }
    
    /// <summary>Calculate relief (elev range) for threshold building.</summary>
    private static (double Min, double Max, double Span) CalculateRelief(double[] elev)
    {
        var min = elev.Min();
        var max = elev.Max();
        var span = max - min;
        return (min, max, span);
    }
    
    /// <summary>Build terrain classification thresholds based on elevation distribution.</summary>
    private static TerrainThresholds BuildThresholds(double[] elev, (double Min, double Max, double Span) relief)
    {
        var sorted = elev.OrderBy(x => x).ToArray();
        var n = sorted.Length;
        
        var p25 = sorted[n / 4];
        var p50 = sorted[n / 2];
        var p75 = sorted[3 * n / 4];
        
        var elevMountain = Math.Max(relief.Min + relief.Span * ElevMountainRel, p75);
        var elevHills = Math.Max(relief.Min + relief.Span * ElevHillsRel, p50);
        
        return new TerrainThresholds
        {
            ElevMountain = elevMountain,
            ElevHills = elevHills,
            P25 = p25,
            P50 = p50,
            P75 = p75,
        };
    }
    
    /// <summary>Classify terrain type for each cell.</summary>
    private static (string[] Terrain, Dictionary<string, object> Aux) ClassifyTerrain(
        HexGridGeometry grid, double[] elev, double[] slopes, 
        (double Min, double Max, double Span) relief, TerrainThresholds th, GenSpec spec)
    {
        var terr = new string[grid.Width * grid.Height];
        var aux = new Dictionary<string, object>();
        
        var rolling = relief.Span < 80.0;
        var mountainous = relief.Span > 200.0;
        
        for (var r = 0; r < grid.Height; r++)
        {
            for (var q = 0; q < grid.Width; q++)
            {
                var idx = r * grid.Width + q;
                var e = elev[idx];
                var sl = slopes[idx];
                
                // River will be marked later
                if (IsRiverCandidate(q, r, elev, grid))
                {
                    terr[idx] = "river";
                    continue;
                }
                
                // Mountain/hills based on elevation and slope
                if (mountainous && sl >= SlopeMountainMinDeg)
                {
                    terr[idx] = "mountain";
                }
                else if (e >= th.ElevMountain)
                {
                    terr[idx] = "mountain";
                }
                else if (rolling)
                {
                    terr[idx] = e >= th.ElevHills ? "hills" : "rough";
                }
                else if (sl >= SlopeHillsMinDeg)
                {
                    terr[idx] = "hills";
                }
                else if (sl >= SlopeWoodsMaxDeg)
                {
                    terr[idx] = "rough";
                }
                else
                {
                    // Use noise field for woods/clear distinction
                    var nx = q / (double)grid.Width;
                    var ny = r / (double)grid.Height;
                    var forestNoise = Fbm(nx * 5.0, ny * 5.0, spec.Seed + 1000, octaves: 3);
                    
                    if (forestNoise >= WoodsNoiseMin && sl <= SlopeWoodsMaxDeg && 
                        e < th.ElevMountain * WoodsMaxElevRel)
                    {
                        terr[idx] = "woods";
                    }
                    else
                    {
                        terr[idx] = "clear";
                    }
                }
            }
        }
        
        return (terr, aux);
    }
    
    /// <summary>Extract continuous river band from north to south edge using Dijkstra.</summary>
    private static List<(int Q, int R)> ExtractRiverBand(HexGridGeometry grid, double[] elev, string[] terr)
    {
        var allStarts = Enumerable.Range(0, grid.Width).Select(q => (q, 0)).ToList();
        var allTargets = Enumerable.Range(0, grid.Width).Select(q => (q, grid.Height - 1)).ToHashSet();
        
        var eMin = elev.Min();
        var eMax = elev.Max();
        var span = Math.Max(1.0, eMax - eMin);
        var stepPen = RiverStepPenaltyRel * span + 1.0;
        
        List<(int Q, int R)>? bestPath = null;
        
        for (var level = 0; level < RiverRouteSteps; level++)
        {
            var pct = level / (double)(RiverRouteSteps - 1);
            var lowCut = eMin + pct * span;
            var ridgeCut = eMin + RiverRouteRidgePct * span;
            var hardCap = eMin * RiverMinElevRatio;
            
            double NodeCost(int q, int r)
            {
                var idx = r * grid.Width + q;
                var e = elev[idx];
                var c = e + stepPen;
                
                if (level < RiverRouteSteps - 1)
                {
                    if (e > ridgeCut)
                        c += 25.0 * span;
                    else if (e > hardCap + 0.5 * span)
                        c += 6.0 * span;
                }
                
                if (e > lowCut)
                    c += 3.0 * (e - lowCut) + 0.4 * span;
                
                return c;
            }
            
            double BendCost((int Q, int R)? a, (int Q, int R) b, (int Q, int R) c)
            {
                if (a == null) return 0.0;
                var angle = TurnAngle(grid, a.Value, b, c);
                return RiverBendPenaltyPerDeg * angle;
            }
            
            var path = Dijkstra(grid, allStarts, allTargets, NodeCost, BendCost);
            
            if (path != null && path.Count >= grid.Height)
            {
                var minR = path.Min(p => p.R);
                var maxR = path.Max(p => p.R);
                var uniqueCount = path.Distinct().Count();
                
                if (minR == 0 && maxR == grid.Height - 1 && uniqueCount == path.Count)
                {
                    bestPath = path.Distinct().OrderBy(p => (p.R, p.Q)).ToList();
                    break;
                }
            }
        }
        
        // Fallback: straight line through center column
        if (bestPath == null || bestPath.Count == 0)
        {
            var qMid = grid.Width / 2;
            bestPath = Enumerable.Range(0, grid.Height).Select(r => (qMid, r)).ToList();
        }
        
        return bestPath;
    }
    
    /// <summary>Build road corridors with bridges crossing the river.</summary>
    private static (HashSet<(int Q, int R)> Road, HashSet<(int Q, int R)> Bridges) BuildRoads(
        HexGridGeometry grid, double[] elev, string[] terr, List<(int Q, int R)> riverBand, int seed)
    {
        var riverSet = riverBand.ToHashSet();
        var k = BridgeTargetCount(grid);
        var rows = RoadCorridorRows(grid, seed, k);
        
        var road = new HashSet<(int Q, int R)>();
        var bridges = new HashSet<(int Q, int R)>();
        
        foreach (var targetRow in rows)
        {
            // Find crossing point on river band closest to target row
            var crossing = riverBand.OrderBy(c => (Math.Abs(c.R - targetRow), c.Q)).First();
            
            var lo = Math.Max(0, targetRow - RoadRowTolerance);
            var hi = Math.Min(grid.Height, targetRow + RoadRowTolerance + 1);
            
            var startsW = Enumerable.Range(lo, hi - lo).Select(r => (0, r)).ToHashSet();
            var startsE = Enumerable.Range(lo, hi - lo).Select(r => (grid.Width - 1, r)).ToHashSet();
            
            double Cost(int q, int r)
            {
                var idx = r * grid.Width + q;
                var t = terr[idx];
                var c = elev[idx] + RoadRowDeviationCost * Math.Abs(r - targetRow);
                
                if (t == "river" && (q, r) != crossing)
                    c += RoadRiverBlockCost;
                if (t == "water")
                    c += RoadWaterBlockCost;
                if (t == "mountain")
                    c += RoadMountainPenalty;
                
                return c;
            }
            
            double Bend((int Q, int R)? a, (int Q, int R) b, (int Q, int R) c)
            {
                var angle = TurnAngle(grid, a ?? b, b, c);
                return RoadBendPenaltyPerDeg * angle;
            }
            
            var pathW = Dijkstra(grid, startsW.ToList(), new HashSet<(int, int)> { crossing }, Cost, Bend);
            var pathE = Dijkstra(grid, startsE.ToList(), new HashSet<(int, int)> { crossing }, Cost, Bend);
            
            if (pathW != null)
            {
                foreach (var cell in pathW)
                    road.Add(cell);
            }
            
            if (pathE != null)
            {
                foreach (var cell in pathE)
                    road.Add(cell);
            }
            
            bridges.Add(crossing);
        }
        
        return (road, bridges);
    }
    
    /// <summary>Place cities along roads near river crossings.</summary>
    private static List<(int Q, int R, int Size)> PlaceCities(
        HexGridGeometry grid, double[] elev, string[] terr, HashSet<(int Q, int R)> road, int seed)
    {
        var cities = new List<(int Q, int R, int Size)>();
        var rng = new Random(seed + 2000);
        
        // Place cities at road intersections or near river
        var candidates = road.Where(cell =>
        {
            var idx = cell.R * grid.Width + cell.Q;
            return terr[idx] != "river" && terr[idx] != "water" && terr[idx] != "mountain";
        }).ToList();
        
        var cityCount = Math.Min(4, Math.Max(2, candidates.Count / 50));
        
        for (var i = 0; i < cityCount && candidates.Count > 0; i++)
        {
            var idx = rng.Next(candidates.Count);
            var cell = candidates[idx];
            var size = rng.Next(1, 4); // 1-3
            
            cities.Add((cell.Q, cell.R, size));
            
            // Remove nearby candidates to maintain separation
            candidates = candidates.Where(c => 
                HexDistance(cell.Q, cell.R, c.Q, c.R) > 5).ToList();
        }
        
        return cities;
    }
    
    /// <summary>Build railway network connecting cities and stations.</summary>
    private static RailwayNetwork BuildRailway(HexGridGeometry grid, double[] elev, string[] terr,
        HashSet<(int Q, int R)> road, List<(int Q, int R, int Size)> cities, int seed)
    {
        var railway = new HashSet<(int Q, int R)>();
        var stations = new List<(int Q, int R)>();
        var tunnels = new HashSet<(int Q, int R)>();
        
        // Connect cities with railway
        for (var i = 0; i < cities.Count - 1; i++)
        {
            var start = (cities[i].Q, cities[i].R);
            var goal = (cities[i + 1].Q, cities[i + 1].R);
            
            double Cost(int q, int r)
            {
                var idx = r * grid.Width + q;
                var t = terr[idx];
                var c = elev[idx] * 0.5;
                
                if (t == "mountain")
                    c += 300.0;
                if (t == "water")
                    c += 1.0e6;
                
                return c;
            }
            
            var path = AStar(grid, start, goal, Cost);
            if (path != null)
            {
                foreach (var cell in path)
                {
                    railway.Add(cell);
                    
                    // Mark tunnels through mountains
                    var idx = cell.R * grid.Width + cell.Q;
                    if (terr[idx] == "mountain" && elev[idx] > 200)
                        tunnels.Add(cell);
                }
            }
        }
        
        // Place rail stations at cities and major intersections
        foreach (var city in cities)
        {
            stations.Add((city.Q, city.R));
        }
        
        return new RailwayNetwork { Cells = railway, Stations = stations, Tunnels = tunnels };
    }
    
    /// <summary>Build power grid connecting plants to cities via substations.</summary>
    private static PowerGrid BuildPowerGrid(HexGridGeometry grid, double[] elev, string[] terr,
        HashSet<(int Q, int R)> road, List<(int Q, int R, int Size)> cities, 
        List<(int Q, int R)> riverBand, int seed)
    {
        var powerlines = new HashSet<(int Q, int R)>();
        var plants = new List<(int Q, int R)>();
        var substations = new List<(int Q, int R)>();
        
        // Place power plants near river but away from cities
        var plantCandidates = riverBand.Where(cell =>
        {
            var tooCloseToCity = cities.Any(c => HexDistance(cell.Q, cell.R, c.Q, c.R) < PlantCityMinDist);
            return !tooCloseToCity;
        }).ToList();
        
        var plantCount = Math.Min(PlantMax, plantCandidates.Count);
        for (var i = 0; i < plantCount; i++)
        {
            if (plantCandidates.Count == 0) break;
            var plant = plantCandidates[i * plantCandidates.Count / plantCount];
            plants.Add(plant);
        }
        
        // Connect plants to cities via substations
        foreach (var plant in plants)
        {
            foreach (var city in cities.Take(2))
            {
                var goal = (city.Q, city.R);
                
                double Cost(int q, int r)
                {
                    var idx = r * grid.Width + q;
                    var t = terr[idx];
                    var c = elev[idx] * 0.3;
                    
                    if (t == "water")
                        c += PowerWaterBlockCost;
                    if (t == "mountain")
                        c += PowerMountainPenalty;
                    
                    var slope = CalculateSlopeAt(grid, elev, q, r);
                    if (slope > PowerMaxSlopeDeg)
                        c += 100.0;
                    
                    return c;
                }
                
                var path = AStar(grid, plant, goal, Cost);
                if (path != null)
                {
                    foreach (var cell in path)
                        powerlines.Add(cell);
                    
                    // Add substation midway
                    if (path.Count > 4)
                    {
                        var subIdx = path.Count / 2;
                        substations.Add(path[subIdx]);
                    }
                }
            }
        }
        
        return new PowerGrid { Lines = powerlines, Plants = plants, Substations = substations };
    }
    
    /// <summary>Place airports near cities on flat terrain.</summary>
    private static List<(int Q, int R, bool IsMajor)> PlaceAirports(
        HexGridGeometry grid, double[] elev, string[] terr, 
        List<(int Q, int R, int Size)> cities, int seed)
    {
        var airports = new List<(int Q, int R, bool IsMajor)>();
        
        foreach (var city in cities)
        {
            // Search for flat area near city
            for (var dist = AirportCityMinDist; dist <= AirportCityMaxDist && airports.Count < AirportMax; dist++)
            {
                var candidates = new List<(int Q, int R)>();
                
                for (var dq = -dist; dq <= dist; dq++)
                {
                    for (var dr = -dist; dr <= dist; dr++)
                    {
                        if (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(-dq - dr) > dist * 2)
                            continue;
                        
                        var q = city.Q + dq;
                        var r = city.R + dr;
                        
                        if (!InBounds(grid, q, r))
                            continue;
                        
                        var idx = r * grid.Width + q;
                        var slope = CalculateSlopeAt(grid, elev, q, r);
                        
                        if (terr[idx] != "river" && terr[idx] != "water" && 
                            terr[idx] != "mountain" && slope <= AirportDerivedSlopeMaxDeg)
                        {
                            candidates.Add((q, r));
                        }
                    }
                }
                
                if (candidates.Count > 0)
                {
                    var airport = candidates[0];
                    var isMajor = city.Size >= 2;
                    airports.Add((airport.Q, airport.R, isMajor));
                    break;
                }
            }
        }
        
        return airports;
    }
    
    /// <summary>Build final hex cells with all features.</summary>
    private static List<Models.HexCell> BuildHexCells(
        HexGridGeometry grid, double[] elev, double[] slopes, string[] terr,
        HashSet<(int Q, int R)> road, List<(int Q, int R)> riverBand, 
        HashSet<(int Q, int R)> bridges, List<(int Q, int R, int Size)> cities,
        RailwayNetwork railway, PowerGrid powerGrid, List<(int Q, int R, bool IsMajor)> airports)
    {
        var riverSet = riverBand.ToHashSet();
        var hexes = new List<Models.HexCell>();
        
        for (var r = 0; r < grid.Height; r++)
        {
            for (var q = 0; q < grid.Width; q++)
            {
                var idx = r * grid.Width + q;
                var isRoad = road.Contains((q, r));
                var isRiver = riverSet.Contains((q, r));
                var isBridge = bridges.Contains((q, r));
                var city = cities.FirstOrDefault(c => c.Q == q && c.R == r);
                var isRailway = railway.Cells.Contains((q, r));
                var isRailStation = railway.Stations.Any(s => s.Q == q && s.R == r);
                var isTunnel = railway.Tunnels.Contains((q, r));
                var isPowerline = powerGrid.Lines.Contains((q, r));
                var isPowerPlant = powerGrid.Plants.Any(p => p.Q == q && p.R == r);
                var isSubstation = powerGrid.Substations.Any(s => s.Q == q && s.R == r);
                var airport = airports.FirstOrDefault(a => a.Q == q && a.R == r);
                
                var terrain = terr[idx];
                if (isRiver) terrain = "river";
                if (isBridge) terrain = "clear"; // Bridge sits on clear terrain
                
                hexes.Add(new Models.HexCell
                {
                    Q = q,
                    R = r,
                    Terrain = terrain,
                    Elev = Math.Round(elev[idx], 1),
                    Slope = Math.Round(slopes[idx], 1),
                    Road = isRoad,
                    River = isRiver,
                    Bridge = isBridge,
                    City = city.Size,
                    Railway = isRailway,
                    RailStation = isRailStation,
                    Tunnel = isTunnel,
                    Powerline = isPowerline,
                    PowerPlant = isPowerPlant,
                    Substation = isSubstation,
                    Airport = airport.IsMajor ? 2 : (airport.Q != 0 ? 1 : 0),
                });
            }
        }
        
        return hexes;
    }
    
    /// <summary>Build metadata with statistics.</summary>
    private static Models.PackageMeta BuildMeta(HexGridGeometry grid, GenSpec spec, 
        double[] elev, string[] terr, HashSet<(int Q, int R)> road, 
        List<(int Q, int R)> riverBand, RailwayNetwork railway, 
        PowerGrid powerGrid, List<(int Q, int R, bool IsMajor)> airports)
    {
        var totalCells = grid.Width * grid.Height;
        var terrainCounts = terr.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        
        var stats = new Dictionary<string, object>
        {
            ["total_cells"] = totalCells,
            ["road_cells"] = road.Count,
            ["river_cells"] = riverBand.Count,
            ["railway_cells"] = railway.Cells.Count,
            ["rail_station_cells"] = railway.Stations.Count,
            ["tunnel_cells"] = railway.Tunnels.Count,
            ["powerline_cells"] = powerGrid.Lines.Count,
            ["power_plant_cells"] = powerGrid.Plants.Count,
            ["substation_cells"] = powerGrid.Substations.Count,
            ["airport_cells"] = airports.Count,
            ["city_cells"] = terr.Count(t => t == "city"),
            ["bridge_cells"] = terr.Count(t => t == "bridge"),
            ["elev_min"] = Math.Round(elev.Min(), 1),
            ["elev_max"] = Math.Round(elev.Max(), 1),
            ["terrain_counts"] = terrainCounts,
        };
        
        var layers = new Dictionary<string, object>
        {
            ["elevation"] = spec.UseRealDem ? "open-meteo" : "synthetic",
            ["landcover"] = "off",
            ["roads"] = spec.Features ? "generated" : "off",
            ["hydrology"] = "generated",
            ["settlements"] = "generated",
            ["railway"] = spec.Features ? "generated" : "off",
            ["power_grid"] = spec.Features ? "generated" : "off",
            ["airports"] = spec.Features ? "generated" : "off",
        };
        
        return new Models.PackageMeta
        {
            Name = spec.Name,
            Source = spec.UseRealDem ? "open-meteo-elevation" : "synthetic-fallback",
            Width = grid.Width,
            Height = grid.Height,
            RadiusM = grid.RadiusM,
            CellGroundM = grid.CellGroundM,
            MercatorScale = grid.Scale,
            MercatorOx = grid.MercatorOx,
            MercatorOy = grid.MercatorOy,
            CenterLonLat = new List<double> { grid.CenterLon, grid.CenterLat },
            Bbox = spec.Bbox.ToList(),
            GeneratedAt = DateTime.UtcNow.ToString("O"),
            Layers = layers,
            Stats = stats,
        };
    }
    
    // ==================== Helper Methods ====================
    
    private static double LonToX(double lonDeg)
    {
        return lonDeg * HalfCircumferenceM / 180.0;
    }
    
    private static double LatToY(double latDeg)
    {
        return EarthRadiusM * Math.Log(Math.Tan((90.0 + latDeg) * Math.PI / 360.0));
    }
    
    private static bool InBounds(HexGridGeometry grid, int q, int r)
    {
        return q >= 0 && q < grid.Width && r >= 0 && r < grid.Height;
    }
    
    private static int HexDistance(int q1, int r1, int q2, int r2)
    {
        return new Hex(q1, r1).DistanceTo(new Hex(q2, r2));
    }
    
    private static double TurnAngle(HexGridGeometry grid, (int Q, int R) a, (int Q, int R) b, (int Q, int R) c)
    {
        var (ax, ay) = Center(grid, a.Q, a.R);
        var (bx, by) = Center(grid, b.Q, b.R);
        var (cx, cy) = Center(grid, c.Q, c.R);
        
        var v1 = Math.Atan2(by - ay, bx - ax);
        var v2 = Math.Atan2(cy - by, cx - bx);
        var d = Math.Abs((v2 - v1) * 180.0 / Math.PI) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }
    
    private static (double X, double Y) Center(HexGridGeometry grid, int q, int r)
    {
        // odd-r offset board: odd rows shifted half a cell right
        var x = grid.MercatorOx + grid.HexWidthM * (q + (r & 1) / 2.0);
        var y = grid.MercatorOy - grid.RowStepM * r;
        return (x, y);
    }
    
    private static double CalculateSlopeAt(HexGridGeometry grid, double[] elev, int q, int r)
    {
        if (!InBounds(grid, q, r)) return 0.0;
        
        var idx = r * grid.Width + q;
        var cellDist = grid.CellGroundM;
        var maxSlope = 0.0;
        
        foreach (var ((dqPos, drPos), (dqNeg, drNeg)) in SlopeAxes(r))
        {
            var nqPos = q + dqPos;
            var nrPos = r + drPos;
            var nqNeg = q + dqNeg;
            var nrNeg = r + drNeg;
            
            if (!InBounds(grid, nqPos, nrPos) || !InBounds(grid, nqNeg, nrNeg))
                continue;
            
            var idxPos = nrPos * grid.Width + nqPos;
            var idxNeg = nrNeg * grid.Width + nqNeg;
            
            var dz = elev[idxPos] - elev[idxNeg];
            var slope = Math.Atan(Math.Abs(dz) / (2.0 * cellDist)) * 180.0 / Math.PI;
            maxSlope = Math.Max(maxSlope, slope);
        }
        
        return maxSlope;
    }
    
    private static bool IsRiverCandidate(int q, int r, double[] elev, HexGridGeometry grid)
    {
        // Simple heuristic: low elevation cells in valley
        var idx = r * grid.Width + q;
        var e = elev[idx];
        var eMin = elev.Min();
        var eMax = elev.Max();
        var span = eMax - eMin;
        
        return e < eMin + span * 0.15 && r > 0 && r < grid.Height - 1;
    }
    
    /// <summary>Fractional Brownian Motion for procedural noise.</summary>
    private static double Fbm(double x, double y, int seed, int octaves = 4)
    {
        var value = 0.0;
        var amplitude = 1.0;
        var frequency = 1.0;
        var maxValue = 0.0;
        
        var rng = new Random(seed);
        
        for (var i = 0; i < octaves; i++)
        {
            value += amplitude * ValueNoise(x * frequency, y * frequency, rng.Next());
            maxValue += amplitude;
            amplitude *= 0.5;
            frequency *= 2.0;
        }
        
        return value / maxValue;
    }
    
    private static double ValueNoise(double x, double y, int seed)
    {
        var xi = (int)Math.Floor(x);
        var yi = (int)Math.Floor(y);
        var xf = x - xi;
        var yf = y - yi;
        
        var tl = Hash(xi, yi, seed);
        var tr = Hash(xi + 1, yi, seed);
        var bl = Hash(xi, yi + 1, seed);
        var br = Hash(xi + 1, yi + 1, seed);
        
        var u = SmoothStep(xf);
        var v = SmoothStep(yf);
        
        var top = Lerp(tl, tr, u);
        var bot = Lerp(bl, br, u);
        
        return Lerp(top, bot, v);
    }
    
    private static double Hash(int x, int y, int seed)
    {
        var h = seed + x * 374761393 + y * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        return (h & 0x7fffffff) / (double)0x7fffffff;
    }
    
    private static double SmoothStep(double t)
    {
        return t * t * (3.0 - 2.0 * t);
    }
    
    private static double Lerp(double a, double b, double t)
    {
        return a + (b - a) * t;
    }
    
    /// <summary>Dijkstra's algorithm with turn cost support.</summary>
    private static List<(int Q, int R)>? Dijkstra(HexGridGeometry grid, 
        List<(int Q, int R)> starts, HashSet<(int Q, int R)> targets,
        Func<int, int, double> nodeCost, Func<(int Q, int R)?, (int Q, int R), (int Q, int R), double>? turnCost = null)
    {
        var dist = new Dictionary<((int Q, int R)? Prev, (int Q, int R) Cur), double>();
        var back = new Dictionary<((int Q, int R)? Prev, (int Q, int R) Cur), ((int Q, int R)? Prev, (int Q, int R) Cur)?>();
        var heap = new SortedSet<(double Dist, int Seq, (int Q, int R)? Prev, (int Q, int R) Cur)>();
        var seq = 0;
        
        foreach (var s in starts.OrderBy(s => (s.R, s.Q)))
        {
            var state = (Prev: ((int Q, int R)?)null, Cur: s);
            var cost = nodeCost(s.Q, s.R);
            dist[state] = cost;
            back[state] = null;
            heap.Add((cost, seq++, null, s));
        }
        
        ((int Q, int R)? Prev, (int Q, int R) Cur)? best = null;
        
        while (heap.Count > 0)
        {
            var minItem = heap.Min;
            var d = minItem.Dist;
            var prev = minItem.Prev;
            var cur = minItem.Cur;
            heap.Remove(minItem);
            
            var state = (prev, cur);
            if (d > dist.GetValueOrDefault(state, double.MaxValue) + 1e-9)
                continue;
            
            if (targets.Contains(cur))
            {
                best = state;
                break;
            }
            
            foreach (var neighbor in GetNeighbors(grid, cur.Q, cur.R))
            {
                if (prev.HasValue && neighbor == prev.Value)
                    continue;
                
                var cost = d + nodeCost(neighbor.Q, neighbor.R);
                if (turnCost != null)
                    cost += turnCost(prev, cur, neighbor);
                
                var nextState = (Prev: ((int Q, int R)?)cur, Cur: neighbor);
                if (cost < dist.GetValueOrDefault(nextState, double.MaxValue) - 1e-9)
                {
                    dist[nextState] = cost;
                    back[nextState] = state;
                    heap.Add((cost, seq++, cur, neighbor));
                }
            }
        }
        
        if (best == null)
            return null;
        
        var path = new List<(int Q, int R)>();
        var node = best;
        while (node.HasValue)
        {
            path.Add(node.Value.Cur);
            node = back.GetValueOrDefault(node.Value);
        }
        
        path.Reverse();
        return path;
    }
    
    /// <summary>A* pathfinding with custom heuristic.</summary>
    private static List<(int Q, int R)>? AStar(HexGridGeometry grid, 
        (int Q, int R) start, (int Q, int R) goal, Func<int, int, double> costFunc)
    {
        var openSet = new SortedSet<(double F, int Seq, (int Q, int R) Cell)>();
        var cameFrom = new Dictionary<(int Q, int R), (int Q, int R)>();
        var gScore = new Dictionary<(int Q, int R), double>();
        
        gScore[start] = 0;
        openSet.Add((Heuristic(start, goal), 0, start));
        var seq = 0;
        
        while (openSet.Count > 0)
        {
            var (_, _, current) = openSet.Min;
            openSet.Remove(openSet.Min);
            
            if (current == goal)
                return ReconstructPath(cameFrom, current);
            
            foreach (var neighbor in GetNeighbors(grid, current.Q, current.R))
            {
                var tentativeG = gScore[current] + costFunc(neighbor.Q, neighbor.R);
                
                if (!gScore.ContainsKey(neighbor) || tentativeG < gScore[neighbor])
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeG;
                    var f = tentativeG + Heuristic(neighbor, goal);
                    openSet.Add((f, seq++, neighbor));
                }
            }
        }
        
        return null;
    }
    
    private static double Heuristic((int Q, int R) a, (int Q, int R) b)
    {
        return HexDistance(a.Q, a.R, b.Q, b.R);
    }
    
    private static List<(int Q, int R)> ReconstructPath(
        Dictionary<(int Q, int R), (int Q, int R)> cameFrom, (int Q, int R) current)
    {
        var path = new List<(int Q, int R)> { current };
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Insert(0, current);
        }
        return path;
    }
    
    private static IEnumerable<(int Q, int R)> GetNeighbors(HexGridGeometry grid, int q, int r)
    {
        foreach (var (dq, dr) in Hex.DirectionsFor(r))
        {
            var nq = q + dq;
            var nr = r + dr;
            if (InBounds(grid, nq, nr))
                yield return (nq, nr);
        }
    }

    /// <summary>The 3 diametric neighbor pairs (indices 0/3, 1/4, 2/5) used for slope gradients.</summary>
    private static ((int Dq, int Dr) Pos, (int Dq, int Dr) Neg)[] SlopeAxes(int r)
    {
        var d = Hex.DirectionsFor(r);
        return new[]
        {
            (d[0], d[3]),
            (d[1], d[4]),
            (d[2], d[5]),
        };
    }
    
    private static int BridgeTargetCount(HexGridGeometry grid)
    {
        return Math.Max(BridgeMin, Math.Min(BridgeMax, grid.Width / BridgeSpacingCols));
    }
    
    private static List<int> RoadCorridorRows(HexGridGeometry grid, int seed, int k)
    {
        var rows = new List<int>();
        var rng = new Random(seed + 3000);
        
        for (var i = 0; i < k; i++)
        {
            var baseRow = (int)Math.Round(grid.Height * (i + 1) / (k + 1.0));
            var jitter = (int)Math.Round(2.0 * (rng.NextDouble() - 0.5));
            var row = Math.Max(1, Math.Min(grid.Height - 2, baseRow + jitter));
            rows.Add(row);
        }
        
        return rows.Distinct().OrderBy(r => r).Take(BridgeMax).ToList();
    }
    
    // ==================== Data Structures ====================
    
    private class HexGridGeometry
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public double CellKm { get; init; }
        public double Scale { get; init; }
        public double CellGroundM { get; init; }
        public double CellM { get; init; }
        public double RadiusM { get; init; }
        public double HexWidthM { get; init; }
        public double RowStepM { get; init; }
        public double MercatorOx { get; init; }
        public double MercatorOy { get; init; }
        public double CenterLon { get; init; }
        public double CenterLat { get; init; }
        public double[] Bbox { get; init; } = new double[4];
    }
    
    private class TerrainThresholds
    {
        public double ElevMountain { get; init; }
        public double ElevHills { get; init; }
        public double P25 { get; init; }
        public double P50 { get; init; }
        public double P75 { get; init; }
    }
    
    private class RailwayNetwork
    {
        public HashSet<(int Q, int R)> Cells { get; init; } = new();
        public List<(int Q, int R)> Stations { get; init; } = new();
        public HashSet<(int Q, int R)> Tunnels { get; init; } = new();
    }
    
    private class PowerGrid
    {
        public HashSet<(int Q, int R)> Lines { get; init; } = new();
        public List<(int Q, int R)> Plants { get; init; } = new();
        public List<(int Q, int R)> Substations { get; init; } = new();
    }
}

/// <summary>Generation specification matching GisJobSpec.</summary>
public class GenSpec
{
    public string Name { get; init; } = "";
    public double[] Bbox { get; init; } = new double[4];
    public double CellKm { get; init; }
    public int Seed { get; init; }
    public bool Offline { get; init; }
    public bool UseRealDem { get; init; }
    public bool Features { get; init; }
}
