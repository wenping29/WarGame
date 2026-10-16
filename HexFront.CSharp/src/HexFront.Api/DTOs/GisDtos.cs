using System.Collections.Generic;
using HexFront.Core.Models;

namespace HexFront.Api.DTOs;

// ────────────── Request DTOs ───────────────

public sealed class GisPlanRequest
{
    public List<double> Bbox { get; init; } = new(); // [west, south, east, north]
    public double? CellKm { get; init; }
}

public sealed class GisGenerateRequest
{
    public List<double> Bbox { get; init; } = new();
    public double CellKm { get; init; }
    public string Name { get; init; } = "";
    public string Dem { get; init; } = "auto";
    public string Landuse { get; init; } = "auto";
    public bool Roads { get; init; } = true;
    public bool Water { get; init; } = true;
    public bool Network { get; init; } = true;
    public bool Features { get; init; } = true;
    public int Seed { get; init; }
    public bool Offline { get; init; }
}

public sealed class GisValidateRequest
{
    public string? PackageName { get; init; }  // existing package in data/gis/
    // Alternatively, upload a file (handled via IFormFile in controller)
}

// ─────────────── Response DTOs ───────────────

public sealed class GisPlanResponse
{
    public double RecommendedCellKm { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int TotalCells { get; init; }
    public double GroundWidthKm { get; init; }
    public double GroundHeightKm { get; init; }
    public List<string> Reasons { get; init; } = new();
}

public sealed class GisJobResponse
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";     // queued/running/done/failed
    public string Name { get; init; } = "";
    public GisJobSpec Spec { get; init; } = new();
    public List<string> Log { get; init; } = new();
    public string? Error { get; init; }
    public PackageMeta? Meta { get; init; }
    public string CreatedAt { get; init; } = "";
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
}

public sealed class GisPackageInfo
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public double CellKm { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Cells { get; init; }
    public double? MercatorScale { get; init; }
    public bool Legacy { get; init; }
    public Dictionary<string, string> Layers { get; init; } = new();
    public string GeneratedAt { get; init; } = "";
}

public sealed class GisCapabilitiesResponse
{
    public bool Available { get; init; }
    public string? Python { get; init; }
    public bool Rasterio { get; init; }
    public string? GdalVersion { get; init; }
    public string? Reason { get; init; }
    public string? DemTif { get; init; }
    public bool DemPresent { get; init; }
    public bool LandcoverOnline { get; init; }
    public bool WaterPresent { get; init; }
    public bool WaterVectorOk { get; init; }
    public string? WaterReason { get; init; }
    public bool VectorOk { get; init; }
    public string? VectorReason { get; init; }
    public string? RoadsGdb { get; init; }
    public bool RoadsGdbPresent { get; init; }
    public string? AirportsShp { get; init; }
    public bool AirportsPresent { get; init; }
    public bool NetworkOk { get; init; }
}
