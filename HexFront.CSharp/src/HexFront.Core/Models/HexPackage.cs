using System;
using System.Collections.Generic;
using HexFront.Core.Geometry;

namespace HexFront.Core.Models;

/// <summary>
/// A complete hex map package: grid geometry + per-cell terrain/features + metadata.
/// Mirrors the JSON structure produced by tools/gis/hexgen.py.
/// </summary>
public sealed class HexPackage
{
    public List<HexCell> Hexes { get; init; } = new();
    public PackageMeta Meta { get; init; } = new();

    public int Width => Meta.Width;
    public int Height => Meta.Height;
}

public sealed class HexCell
{
    public int Q { get; init; }
    public int R { get; init; }
    public string Terrain { get; init; } = "clear";
    public double Elev { get; init; }
    public double Slope { get; init; }
    public bool Road { get; init; }
    public bool River { get; init; }
    public bool Bridge { get; init; }
    public int City { get; init; }
    public bool Railway { get; init; }
    public bool RailStation { get; init; }
    public bool Tunnel { get; init; }
    public bool Powerline { get; init; }
    public bool PowerPlant { get; init; }
    public bool Substation { get; init; }
    public int Airport { get; init; }
}

public sealed class PackageMeta
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double RadiusM { get; init; }
    public double CellGroundM { get; init; }
    public double MercatorScale { get; init; }
    public double MercatorOx { get; init; }
    public double MercatorOy { get; init; }
    public List<double> CenterLonLat { get; init; } = new();
    public List<double> Bbox { get; init; } = new(); // [west, south, east, north] in WGS84
    public string GeneratedAt { get; init; } = "";
    public Dictionary<string, object> Layers { get; init; } = new(); // Can be string or nested object
    public Dictionary<string, object> Stats { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public int Fails { get; init; }
    public List<ValidationResult> Validations { get; init; } = new();
}

public sealed class ValidationResult
{
    public string Level { get; init; } = "OK"; // OK / WARN / FAIL
    public string Check { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>GIS generation job status.</summary>
public enum GisJobStatus { Queued, Running, Done, Failed }

public sealed class GisJob
{
    public string Id { get; init; } = "";
    public GisJobSpec Spec { get; init; } = new();
    public string PkgName { get; init; } = "";
    public GisJobStatus Status { get; set; } = GisJobStatus.Queued;
    public List<string> Log { get; init; } = new();
    public string? Error { get; set; }
    public HexPackage? Package { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public sealed class GisJobSpec
{
    public List<double> Bbox { get; init; } = new(); // [west, south, east, north]
    public double CellKm { get; init; }
    public string Name { get; init; } = "";
    public string Dem { get; init; } = "auto";      // auto/local/synthetic
    public string Landuse { get; init; } = "auto";  // auto/proxy/off
    public bool Roads { get; init; } = true;
    public bool Water { get; init; } = true;
    public bool Network { get; init; } = true;       // real vector features (World_Roads + NE airports)
    public bool Features { get; init; } = true;      // derived gameplay layers (rail/power/tunnel/etc.)
    public int Seed { get; init; }
    public bool Offline { get; init; }
}
