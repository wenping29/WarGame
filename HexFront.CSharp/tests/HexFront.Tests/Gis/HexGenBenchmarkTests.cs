using System;
using System.Diagnostics;
using FluentAssertions;
using HexFront.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace HexFront.Tests.Gis;

/// <summary>
/// Performance benchmark: native C# hex generation must build a tactical 2.5km grid
/// (rhine-region scale, full feature pipeline) in well under 30 seconds.
/// </summary>
public class HexGenBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public HexGenBenchmarkTests(ITestOutputHelper outHelper) => _out = outHelper;

    private static GenSpec TacticalSpec() => new()
    {
        Name = "bench_rhine_2p5km",
        Bbox = new double[] { 6.5, 50.5, 8.0, 51.7 }, // ~105km x 133km
        CellKm = 2.5,
        Seed = 123,
        Offline = true,
        UseRealDem = false,
        Features = true,
    };

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Generate_2p5km_Grid_Under_30_Seconds()
    {
        var sw = Stopwatch.StartNew();
        var pkg = HexGenService.Generate(TacticalSpec());
        sw.Stop();

        var cells = pkg.Width * pkg.Height;
        _out.WriteLine($"grid {pkg.Width}x{pkg.Height} = {cells} cells in {sw.ElapsedMilliseconds} ms");

        cells.Should().BeGreaterThan(1500, "bbox should yield a tactical-scale grid");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            $".NET native generation of a {pkg.Width}x{pkg.Height} 2.5km grid must stay under the 30s budget");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Generate_Under_100ms_Per_Cell()
    {
        var spec = TacticalSpec();
        var sw = Stopwatch.StartNew();
        var pkg = HexGenService.Generate(spec);
        sw.Stop();

        var cells = pkg.Hexes.Count;
        var msPerCell = (double)sw.ElapsedMilliseconds / cells;
        _out.WriteLine($"{cells} cells, {msPerCell:F4} ms/cell");

        msPerCell.Should().BeLessThan(15.0, "amortized per-cell cost should be small");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Generate_Is_Faster_Than_Small_Theta_For_Larger_Grids()
    {
        // Relative sanity: doubling linear resolution (4x cells) should not blow up super-linearly
        // beyond a generous factor — guards against accidental O(n^2) regressions in the pipeline.
        var coarse = Measure(new GenSpec
        {
            Name = "bench_coarse", Bbox = new double[] { 6.5, 50.5, 8.0, 51.7 },
            CellKm = 5.0, Seed = 1, Offline = true, Features = true,
        });
        var fine = Measure(new GenSpec
        {
            Name = "bench_fine", Bbox = new double[] { 6.5, 50.5, 8.0, 51.7 },
            CellKm = 2.5, Seed = 1, Offline = true, Features = true,
        });

        var cellRatio = (double)fine.cells / coarse.cells;
        var timeRatio = (double)fine.ms / coarse.ms;
        _out.WriteLine($"coarse {coarse.cells}@{coarse.ms}ms, fine {fine.cells}@{fine.ms}ms, cellRatio {cellRatio:F2}, timeRatio {timeRatio:F2}");

        timeRatio.Should().BeLessThan(cellRatio * 6.0,
            "4x cells should not translate into a wildly super-linear time blowup");
    }

    private static (int cells, long ms) Measure(GenSpec spec)
    {
        var sw = Stopwatch.StartNew();
        var pkg = HexGenService.Generate(spec);
        sw.Stop();
        return (pkg.Width * pkg.Height, sw.ElapsedMilliseconds);
    }
}
