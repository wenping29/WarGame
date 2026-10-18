using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using HexFront.Core.Validation;
using Xunit;

namespace HexFront.Tests.Gis;

/// <summary>
/// Comprehensive tests for native C# hex generation (HexGenService.Generate).
/// Uses offline synthetic DEM so results are deterministic and network-free.
/// </summary>
public class HexGenServiceTests
{
    private static readonly HashSet<string> ValidTerrains = new()
    {
        "clear", "rough", "woods", "hills", "mountain", "city", "swamp", "river", "water"
    };

    private static GenSpec SmallSpec(int seed = 7, bool features = true) => new()
    {
        Name = "unit_test_grid",
        Bbox = new double[] { 6.80, 50.90, 7.10, 51.10 }, // ~3.3km x 2.2km area
        CellKm = 0.5,
        Seed = seed,
        Offline = true,
        UseRealDem = false,
        Features = features,
    };

    [Fact]
    public void Generate_Produces_NonEmpty_Package()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        pkg.Should().NotBeNull();
        pkg.Hexes.Should().NotBeEmpty();
        pkg.Width.Should().BePositive();
        pkg.Height.Should().BePositive();
    }

    [Fact]
    public void Generate_Hex_Count_Matches_Grid_Dimensions()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        pkg.Hexes.Should().HaveCount(pkg.Width * pkg.Height, "every (q,r) cell must be emitted");
        pkg.Hexes.Should().HaveCount(pkg.Meta.Width * pkg.Meta.Height);
    }

    [Fact]
    public void Generate_Coverage_Every_Cell_Once_No_Gaps()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        var keys = pkg.Hexes.Select(h => (h.Q, h.R)).ToList();
        keys.Should().OnlyHaveUniqueItems("each hex coordinate appears exactly once");

        foreach (var h in pkg.Hexes)
        {
            h.Q.Should().BeInRange(0, pkg.Width - 1);
            h.R.Should().BeInRange(0, pkg.Height - 1);
        }

        // Full rectangle coverage.
        keys.Select(k => k.Q).Distinct().Should().HaveCount(pkg.Width);
        keys.Select(k => k.R).Distinct().Should().HaveCount(pkg.Height);
    }

    [Fact]
    public void Generate_Terrain_Values_Are_All_Valid()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        foreach (var h in pkg.Hexes)
        {
            ValidTerrains.Should().Contain(h.Terrain, $"terrain '{h.Terrain}' at ({h.Q},{h.R}) is unknown");
        }
    }

    [Fact]
    public void Generate_Meta_Mirrors_Spec()
    {
        var spec = SmallSpec();
        var pkg = HexGenService.Generate(spec);

        pkg.Meta.Name.Should().Be(spec.Name);
        pkg.Meta.Source.Should().Be("synthetic-fallback");
        pkg.Meta.Bbox.Should().Equal(spec.Bbox);
        pkg.Meta.CellGroundM.Should().Be(spec.CellKm * 1000.0);
        pkg.Meta.CenterLonLat.Should().HaveCount(2);
        // 经度线性 ⇒ 仍是算术中点；纬度走墨卡托中点（与生成侧同一份算法）
        pkg.Meta.CenterLonLat[0].Should().BeApproximately((spec.Bbox[0] + spec.Bbox[2]) / 2.0, 1e-12);
        var (_, centerLat) = GisPlanService.GridCenter(spec.Bbox);
        pkg.Meta.CenterLonLat[1].Should().Be(centerLat);
        pkg.Meta.CenterLonLat[1].Should().BeGreaterThan((spec.Bbox[1] + spec.Bbox[3]) / 2.0);
    }

    [Fact]
    public void Generate_Mercator_Geometry_Is_Finite_And_Sane()
    {
        var pkg = HexGenService.Generate(SmallSpec());
        var m = pkg.Meta;

        double.IsFinite(m.RadiusM).Should().BeTrue();
        m.RadiusM.Should().BePositive();
        m.MercatorScale.Should().BeGreaterThanOrEqualTo(1.0);
        double.IsFinite(m.MercatorOx).Should().BeTrue();
        double.IsFinite(m.MercatorOy).Should().BeTrue();

        // radius = cellGroundM * scale / sqrt(3)
        var expected = m.CellGroundM * m.MercatorScale / Math.Sqrt(3.0);
        m.RadiusM.Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public void Generate_Is_Deterministic_For_Same_Seed()
    {
        var a = HexGenService.Generate(SmallSpec(seed: 42));
        var b = HexGenService.Generate(SmallSpec(seed: 42));

        var ta = a.Hexes.Select(h => $"{h.Q},{h.R}:{h.Terrain}").ToList();
        var tb = b.Hexes.Select(h => $"{h.Q},{h.R}:{h.Terrain}").ToList();
        ta.Should().Equal(tb, "identical seed must reproduce identical terrain");
    }

    [Fact]
    public void Generate_Different_Seeds_Change_Layout()
    {
        var a = HexGenService.Generate(SmallSpec(seed: 1));
        var b = HexGenService.Generate(SmallSpec(seed: 999));

        var sigA = string.Join("|", a.Hexes.Select(h => h.Terrain));
        var sigB = string.Join("|", b.Hexes.Select(h => h.Terrain));
        sigA.Should().NotBe(sigB, "changing the seed must alter the generated map");
    }

    [Fact]
    public void Generate_Stats_Total_Cells_Matches_Hexes()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        pkg.Meta.Stats["total_cells"].Should().Be(pkg.Hexes.Count);
    }

    [Fact]
    public void Generate_Terrain_Counts_Sum_To_Total()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        var counts = pkg.Meta.Stats["terrain_counts"] as Dictionary<string, int>;
        counts.Should().NotBeNull("terrain_counts stat must be a name->count map");
        long sum = counts!.Values.Sum(v => (long)v);
        sum.Should().Be(pkg.Hexes.Count);
    }

    [Fact]
    public void Generate_Road_And_River_Flag_Consistency()
    {
        var pkg = HexGenService.Generate(SmallSpec(features: true));

        var roadCells = pkg.Meta.Stats["road_cells"];
        roadCells.Should().Be(pkg.Hexes.Count(h => h.Road));

        // Bridges only make sense on river crossings; every bridge cell should be road.
        foreach (var h in pkg.Hexes.Where(h => h.Bridge))
        {
            h.Road.Should().BeTrue("a bridge cell is part of a road route");
        }
    }

    [Fact]
    public void Generate_Features_Disabled_Still_Produces_Valid_Terrain()
    {
        var pkg = HexGenService.Generate(SmallSpec(features: false));

        pkg.Hexes.Should().NotBeEmpty();
        foreach (var h in pkg.Hexes)
        {
            ValidTerrains.Should().Contain(h.Terrain);
        }
    }

    [Fact]
    public void Generate_Passes_Validator_With_Zero_Fails()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        var report = HexPackageValidator.Validate(pkg);
        report.Fails.Should().Be(0,
            string.Join("; ", report.Results.Where(r => r.Level == "FAIL").Select(r => r.Check + ":" + r.Detail)));
    }

    [Fact]
    public void Package_Roundtrips_Through_The_Snake_Case_Wire_Contract()
    {
        // 落盘/读取都走 SnakeCaseLower：键名对不上时属性会静默留空，校验也就跟着失明
        var pkg = HexGenService.Generate(SmallSpec());
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var json = JsonSerializer.Serialize(pkg, options);
        json.Should().Contain("\"center_lonlat\":[");

        var back = JsonSerializer.Deserialize<HexPackage>(json,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
            })!;
        back.Meta.CenterLonLat.Should().HaveCount(2);
        back.Meta.CenterLonLat.Should().Equal(pkg.Meta.CenterLonLat);
        back.Meta.RadiusM.Should().Be(pkg.Meta.RadiusM);
        back.Meta.MercatorScale.Should().Be(pkg.Meta.MercatorScale);
        HexPackageValidator.Validate(back).Fails.Should().Be(0);
    }

    [Fact]
    public void Generate_Validator_Reports_All_37_Checks()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        var report = HexPackageValidator.Validate(pkg);
        report.Total.Should().Be(37);
        report.Passes.Should().Be(report.Total - report.Fails - report.Warns);
    }

    [Fact]
    public void Generate_Elevation_Range_Is_Recorded()
    {
        var pkg = HexGenService.Generate(SmallSpec());

        pkg.Meta.Stats.ContainsKey("elev_min").Should().BeTrue();
        pkg.Meta.Stats.ContainsKey("elev_max").Should().BeTrue();
        Convert.ToDouble(pkg.Meta.Stats["elev_min"]).Should().BeLessThanOrEqualTo(Convert.ToDouble(pkg.Meta.Stats["elev_max"]));
    }
}
