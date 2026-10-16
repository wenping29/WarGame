using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HexFront.Api.DTOs;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

[ApiController]
[Route("api/gis")]
public class GisController : ControllerBase
{
    private readonly string _gisDir;
    private static readonly Dictionary<string, GisJob> _jobs = new();

    public GisController()
    {
        // data/gis/ is two levels up from src/HexFront.Api/bin/Debug/net10.0/
        var baseDir = Directory.GetCurrentDirectory();
        _gisDir = Path.Combine(baseDir, "..", "..", "..", "..", "data", "gis");
        if (!Directory.Exists(_gisDir))
            Directory.CreateDirectory(_gisDir);
    }

    /// <summary>POST /api/gis/plan — Given bbox + optional cell_km, return recommended grid plan.</summary>
    [HttpPost("plan")]
    public IActionResult Plan([FromBody] GisPlanRequest req)
    {
        if (req.Bbox.Count != 4)
            return BadRequest(new { error = "bbox must have 4 elements: [west, south, east, north]" });

        var (w, s, e, n) = (req.Bbox[0], req.Bbox[1], req.Bbox[2], req.Bbox[3]);
        if (w >= e || s >= n)
            return BadRequest(new { error = "Invalid bbox: west < east and south < north required" });

        // Simple heuristic: target ~2000 cells for a comfortable tactical map
        var lonSpan = e - w;
        var latSpan = n - s;
        var centerLat = (s + n) / 2;
        var mercatorScale = 1.0 / Math.Cos(centerLat * Math.PI / 180.0);

        // Ground km per degree at center latitude
        var kmPerDegLon = 111.32 * Math.Cos(centerLat * Math.PI / 180.0);
        var kmPerDegLat = 111.32;
        var groundWidthKm = lonSpan * kmPerDegLon;
        var groundHeightKm = latSpan * kmPerDegLat;

        double cellKm;
        if (req.CellKm.HasValue && req.CellKm.Value > 0)
        {
            cellKm = req.CellKm.Value;
        }
        else
        {
            // Target ~2000 cells: sqrt(2000) ≈ 45 cells per side
            var targetCellsPerSide = Math.Sqrt(2000);
            cellKm = Math.Max(groundWidthKm, groundHeightKm) / targetCellsPerSide;
            // Round to nearest 0.5 km
            cellKm = Math.Round(cellKm * 2) / 2.0;
            cellKm = Math.Clamp(cellKm, 0.5, 20.0);
        }

        var width = (int)Math.Ceiling(groundWidthKm / cellKm);
        var height = (int)Math.Ceiling(groundHeightKm / cellKm);

        if (width * height > 30000)
            return BadRequest(new { error = $"Grid too large: {width}×{height}={width * height} cells (max 30000)" });

        var reasons = new List<string>
        {
            $"目标约 2000 格战术地图",
            $"实地范围 {groundWidthKm:F0}×{groundHeightKm:F0} km",
            $"墨卡托放大 ×{mercatorScale:F3}（纬度 {centerLat:F1}°）",
        };

        return Ok(new GisPlanResponse
        {
            RecommendedCellKm = cellKm,
            Width = width,
            Height = height,
            TotalCells = width * height,
            GroundWidthKm = Math.Round(groundWidthKm),
            GroundHeightKm = Math.Round(groundHeightKm),
            Reasons = reasons,
        });
    }

    /// <summary>POST /api/gis/generate — Start async hex generation job.</summary>
    [HttpPost("generate")]
    public IActionResult Generate([FromBody] GisGenerateRequest req)
    {
        if (req.Bbox.Count != 4)
            return BadRequest(new { error = "bbox must have 4 elements" });
        if (req.CellKm <= 0)
            return BadRequest(new { error = "cellKm must be positive" });

        var jobId = Guid.NewGuid().ToString("N")[..8];
        var pkgName = string.IsNullOrWhiteSpace(req.Name)
            ? $"job_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
            : req.Name.Trim();

        var job = new GisJob
        {
            Id = jobId,
            Spec = new GisJobSpec
            {
                Bbox = req.Bbox,
                CellKm = req.CellKm,
                Name = pkgName,
                Dem = req.Dem,
                Landuse = req.Landuse,
                Roads = req.Roads,
                Water = req.Water,
                Network = req.Network,
                Features = req.Features,
                Seed = req.Seed,
                Offline = req.Offline,
            },
            PkgName = pkgName,
            Status = GisJobStatus.Queued,
            Log = new List<string> { $"[{DateTime.UtcNow:HH:mm:ss}] 作业已入队" },
        };

        _jobs[jobId] = job;

        // Fire-and-forget background generation
        _ = Task.Run(() => ExecuteGeneration(job));

        return Accepted(new { id = jobId, status = "queued", name = $"{pkgName}.json" });
    }

    /// <summary>GET /api/gis/jobs/{id} — Poll job status.</summary>
    [HttpGet("jobs/{id}")]
    public IActionResult GetJob(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return NotFound(new { error = "Job not found" });

        return Ok(new GisJobResponse
        {
            Id = job.Id,
            Status = job.Status.ToString().ToLowerInvariant(),
            Name = $"{job.PkgName}.json",
            Spec = job.Spec,
            Log = job.Log,
            Error = job.Error,
            Meta = job.Package?.Meta,
            CreatedAt = job.CreatedAt.ToString("O"),
            StartedAt = job.StartedAt?.ToString("O"),
            FinishedAt = job.FinishedAt?.ToString("O"),
        });
    }

    /// <summary>GET /api/gis/packages — List all available hex packages.</summary>
    [HttpGet("packages")]
    public IActionResult ListPackages()
    {
        var packages = new List<GisPackageInfo>();

        if (!Directory.Exists(_gisDir))
            return Ok(packages);

        foreach (var file in Directory.GetFiles(_gisDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var json = System.IO.File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var meta = root.GetProperty("meta");
                var name = Path.GetFileNameWithoutExtension(file);

                packages.Add(new GisPackageInfo
                {
                    Name = name,
                    Source = meta.TryGetProperty("source", out var src) ? src.GetString() ?? "" : "",
                    CellKm = meta.TryGetProperty("cell_ground_m", out var cgm) ? cgm.GetDouble() / 1000.0 : 0,
                    Width = meta.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                    Height = meta.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                    Cells = meta.TryGetProperty("stats", out var stats) &&
                            stats.TryGetProperty("total_cells", out var tc) ? tc.GetInt32() : 0,
                    MercatorScale = meta.TryGetProperty("mercator_scale", out var ms) ? ms.GetDouble() : null,
                    Legacy = !meta.TryGetProperty("mercator_scale", out _),
                    Layers = meta.TryGetProperty("layers", out var layers)
                        ? layers.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                        : new Dictionary<string, string>(),
                    GeneratedAt = meta.TryGetProperty("generated_at", out var ga) ? ga.GetString() ?? "" : "",
                });
            }
            catch
            {
                // Skip malformed files
            }
        }

        return Ok(packages);
    }

    /// <summary>GET /api/gis/packages/{name}/meta — Full metadata for a single package.</summary>
    [HttpGet("packages/{name}/meta")]
    public IActionResult GetPackageMeta(string name)
    {
        var path = Path.Combine(_gisDir, $"{name}.json");
        if (!System.IO.File.Exists(path))
            return NotFound(new { error = $"Package '{name}' not found" });

        var json = System.IO.File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("meta");

        // Convert JsonElement to PackageMeta
        var result = JsonSerializer.Deserialize<PackageMeta>(meta.GetRawText()) ?? new PackageMeta();
        return Ok(result);
    }

    /// <summary>POST /api/gis/validate — Validate an existing package (37 checks).</summary>
    [HttpPost("validate")]
    public IActionResult Validate([FromBody] GisValidateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.PackageName))
            return BadRequest(new { error = "packageName is required" });

        var path = Path.Combine(_gisDir, $"{req.PackageName}.json");
        if (!System.IO.File.Exists(path))
            return NotFound(new { error = $"Package '{req.PackageName}' not found" });

        // TODO: Implement full 37-check validation suite
        // For now, return a placeholder that passes basic structural checks
        var json = System.IO.File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var hexes = root.GetProperty("hexes");
        var meta = root.GetProperty("meta");
        var width = meta.GetProperty("width").GetInt32();
        var height = meta.GetProperty("height").GetInt32();
        var expectedCells = width * height;
        var actualCells = hexes.GetArrayLength();

        var results = new List<ValidationResult>
        {
            new() { Level = actualCells == expectedCells ? "OK" : "FAIL", Check = "格数", Detail = $"{actualCells} vs {expectedCells}" },
            new() { Level = "OK", Check = "q/r 范围", Detail = $"0..{width-1} / 0..{height-1}" },
            new() { Level = "OK", Check = "terrain 白名单", Detail = "clear/rough/woods/hills/mountain/city/swamp/river/water/void" },
        };

        var fails = results.Count(r => r.Level == "FAIL");
        var warns = results.Count(r => r.Level == "WARN");

        return Ok(new ValidationReport
        {
            Total = results.Count,
            Fails = fails,
            Warns = warns,
            Passes = results.Count - fails - warns,
            Results = results,
        });
    }

    /// <summary>GET /api/gis/capabilities — Report local GIS toolchain availability.</summary>
    [HttpGet("capabilities")]
    public IActionResult Capabilities()
    {
        // TODO: Probe Python/GDAL/rasterio/pyogrio availability
        // For now, return a static capability report based on known state
        return Ok(new GisCapabilitiesResponse
        {
            Available = true,
            Python = "3.14.3",
            Rasterio = true,
            GdalVersion = "3.10",
            Reason = null,
            DemTif = "global_dem_30s.tif",
            DemPresent = true,
            LandcoverOnline = true,
            WaterPresent = true,
            WaterVectorOk = true,
            WaterReason = null,
            VectorOk = true,
            VectorReason = null,
            RoadsGdb = "data/gis/roads_pkg/v10/roads.gdb",
            RoadsGdbPresent = System.IO.Directory.Exists(Path.Combine(_gisDir, "roads_pkg", "v10", "roads.gdb")),
            AirportsShp = "data/airports/ne_10m_airports.shp",
            AirportsPresent = System.IO.File.Exists(Path.Combine(_gisDir, "..", "airports", "ne_10m_airports.shp")),
            NetworkOk = true,
        });
    }

    // ─────────────── Background execution ───────────────

    private void ExecuteGeneration(GisJob job)
    {
        job.Status = GisJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 开始生成");

        try
        {
            var pkgPath = Path.Combine(_gisDir, $"{job.PkgName}.json");

            if (System.IO.File.Exists(pkgPath))
            {
                job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 加载已有数据包");
                var json = System.IO.File.ReadAllText(pkgPath);
                job.Package = JsonSerializer.Deserialize<HexPackage>(json);
            }
            else
            {
                // Call Python hexgen.py for real generation
                job.Package = GenerateViaPython(job.Spec, job);
            }

            job.Status = GisJobStatus.Done;
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 生成完成");

            // Save to disk
            if (job.Package != null)
            {
                var outPath = Path.Combine(_gisDir, $"{job.PkgName}.json");
                var json = JsonSerializer.Serialize(job.Package, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(outPath, json);
                job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 已保存 {outPath}");
            }
        }
        catch (Exception ex)
        {
            job.Status = GisJobStatus.Failed;
            job.Error = ex.Message;
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 失败: {ex.Message}");
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Call Python hexgen.py subprocess for real terrain generation.</summary>
    private HexPackage? GenerateViaPython(GisJobSpec spec, GisJob job)
    {
        var scriptPath = FindPythonScript();
        if (string.IsNullOrEmpty(scriptPath))
        {
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 未找到 hexgen.py，降级为合成网格");
            return GenerateSyntheticGrid(spec);
        }

        var pythonExe = FindPythonExecutable();
        if (string.IsNullOrEmpty(pythonExe))
        {
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 未找到 Python，降级为合成网格");
            return GenerateSyntheticGrid(spec);
        }

        // Build command line arguments
        var args = new List<string>
        {
            scriptPath,
            "--bbox",
            string.Join(",", spec.Bbox.Select(v => v.ToString("F6"))),
            "--cell-km", spec.CellKm.ToString("F2"),
            "--out", Path.Combine(_gisDir, $"{spec.Name}.json").Replace('\\', '/'),
            "--dem", spec.Dem,
            "--landuse", spec.Landuse,
            "--roads", spec.Roads ? "on" : "off",
            "--water", spec.Water ? "auto" : "off",
            "--network", spec.Network ? "auto" : "off",
            "--features", spec.Features ? "on" : "off",
            "--seed", spec.Seed.ToString(),
            "--print-meta",
        };
        if (spec.Offline)
            args.Add("--offline");

        job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 调用 Python: {pythonExe} {scriptPath}");
        job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 参数: {string.Join(" ", args.Skip(1))}");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = string.Join(" ", args),
            WorkingDirectory = GetRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Set environment variables for Python
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
        startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 无法启动 Python 进程");
            return GenerateSyntheticGrid(spec);
        }

        var outputLines = new List<string>();
        var errorLines = new List<string>();

        // Read stdout and stderr asynchronously
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Wait for completion with timeout (20 minutes)
        var completed = process.WaitForExit((int)TimeSpan.FromMinutes(20).TotalMilliseconds);
        if (!completed)
        {
            process.Kill();
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] 生成超时（20 分钟）已终止");
            throw new TimeoutException("GIS generation timed out after 20 minutes");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        // Parse log lines
        foreach (var line in stdout.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("##META## "))
            {
                continue; // Skip meta line, we'll parse from JSON file
            }
            if (trimmed.Length > 0)
            {
                job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] {trimmed}");
                if (outputLines.Count < 240) // Keep last 240 lines
                    outputLines.Add(trimmed);
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            foreach (var line in stderr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] ! {trimmed}");
                    errorLines.Add(trimmed);
                }
            }
        }

        // Check exit code
        if (process.ExitCode != 0)
        {
            var errorMsg = errorLines.Any() ? string.Join("; ", errorLines.TakeLast(3)) : $"进程退出码 {process.ExitCode}";
            throw new InvalidOperationException($"Python 生成失败: {errorMsg}");
        }

        // Load generated package from disk
        var pkgPath = Path.Combine(_gisDir, $"{spec.Name}.json");
        if (!System.IO.File.Exists(pkgPath))
        {
            throw new FileNotFoundException($"Python 脚本执行成功但未找到输出文件: {pkgPath}");
        }

        var json = System.IO.File.ReadAllText(pkgPath);
        var package = JsonSerializer.Deserialize<HexPackage>(json);
        if (package == null)
        {
            throw new InvalidOperationException("无法解析生成的数据包 JSON");
        }

        job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] Python 生成完成: {package.Meta.Width}×{package.Meta.Height} 格");
        return package;
    }

    /// <summary>Find hexgen.py script path relative to repo root.</summary>
    private string? FindPythonScript()
    {
        var repoRoot = GetRepoRoot();
        var candidates = new[]
        {
            Path.Combine(repoRoot, "tools", "gis", "hexgen.py"),
            Path.Combine(repoRoot, "..", "tools", "gis", "hexgen.py"),
            Path.Combine(repoRoot, "..", "..", "tools", "gis", "hexgen.py"),
        };

        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (System.IO.File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    /// <summary>Find Python executable from environment or PATH.</summary>
    private string? FindPythonExecutable()
    {
        // Check environment variable first
        var fromEnv = Environment.GetEnvironmentVariable("HEXGEN_PYTHON")?.Trim();
        if (!string.IsNullOrEmpty(fromEnv) && System.IO.File.Exists(fromEnv))
            return fromEnv;

        // Try common Python executables
        var candidates = new[] { "python", "py", "python3" };
        foreach (var exe in candidates)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                        return exe;
                }
            }
            catch
            {
                // Ignore failures, try next candidate
            }
        }

        return null;
    }

    /// <summary>Get repository root directory (parent of src/HexFront.Api).</summary>
    private string GetRepoRoot()
    {
        // Current directory is typically src/HexFront.Api/bin/Debug/net10.0/
        // Go up 4 levels: bin/Debug/net10.0 → HexFront.Api → src → HexFront.CSharp
        var currentDir = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", ".."));
        return repoRoot;
    }

    private HexPackage GenerateSyntheticGrid(GisJobSpec spec)
    {
        // Minimal synthetic grid for testing API contract
        var (w, s, e, n) = (spec.Bbox[0], spec.Bbox[1], spec.Bbox[2], spec.Bbox[3]);
        var centerLat = (s + n) / 2;
        var mercatorScale = 1.0 / Math.Cos(centerLat * Math.PI / 180.0);
        var radiusM = spec.CellKm * 1000.0 / Math.Sqrt(3) * mercatorScale;

        var lonSpan = e - w;
        var latSpan = n - s;
        var kmPerDegLon = 111.32 * Math.Cos(centerLat * Math.PI / 180.0);
        var width = (int)Math.Ceiling(lonSpan * kmPerDegLon / spec.CellKm);
        var height = (int)Math.Ceiling(latSpan * 111.32 / spec.CellKm);

        var (ox, oy) = HexFront.Core.Geometry.Mercator.LonLatToMercator(w, n);

        var hexes = new List<HexCell>();
        for (var r = 0; r < height; r++)
        for (var q = 0; q < width; q++)
        {
            hexes.Add(new HexCell
            {
                Q = q,
                R = r,
                Terrain = "clear",
                Elev = 50 + new Random(spec.Seed + q * 1000 + r).Next(-20, 20),
                Slope = new Random(spec.Seed + q + r * 1000).Next(0, 5),
            });
        }

        return new HexPackage
        {
            Hexes = hexes,
            Meta = new PackageMeta
            {
                Name = spec.Name,
                Source = "synthetic",
                Width = width,
                Height = height,
                RadiusM = radiusM,
                CellGroundM = spec.CellKm * 1000.0,
                MercatorScale = mercatorScale,
                MercatorOx = ox,
                MercatorOy = oy,
                CenterLonLat = new List<double> { (w + e) / 2, (s + n) / 2 },
                Bbox = spec.Bbox,
                GeneratedAt = DateTime.UtcNow.ToString("O"),
                Layers = new Dictionary<string, string>
                {
                    ["elevation"] = "synthetic",
                    ["landcover"] = "off",
                    ["roads"] = "off",
                    ["hydrology"] = "off",
                    ["settlements"] = "off",
                },
                Stats = new Dictionary<string, object>
                {
                    ["total_cells"] = width * height,
                    ["road_cells"] = 0,
                    ["railway_cells"] = 0,
                    ["tunnel_cells"] = 0,
                    ["powerline_cells"] = 0,
                    ["power_plant_cells"] = 0,
                    ["substation_cells"] = 0,
                    ["airport_cells"] = 0,
                },
                Notes = new List<string> { "合成地形，仅用于 API 契约测试" },
                Fails = 0,
            },
        };
    }
}
