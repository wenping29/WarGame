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

    /// <summary>
    /// doc §13.1 那条验收线写的是「2.5km 格距 1645 格 &lt; 30s」，1645 = 35x47 就是
    /// data/gis/rhine_ruhr_2p5km.json 的棋盘。上面那条用的是更大的 bbox，格数对不上文档，
    /// 所以这里按那个包自己的 bbox/cell_km 原样再生成一次，让文档里的数字有同名的一条回归。
    /// 实测拿 1645 格包的 meta.bbox 回喂 grid_from_bbox 会得到 36x48 = 1728 格：
    /// meta.bbox 记的是**生成结果**的格心范围（比输入宽半格），而 <c>GisPlanService.GridSize</c>
    /// 与 Python <c>grid_from_bbox</c> 都是 round(跨度/格宽)+1，回喂必然多一行一列——两端口径一致，不是回归。
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Generate_Doc_1645_Cell_Board_Under_30_Seconds()
    {
        var sw = Stopwatch.StartNew();
        var pkg = HexGenService.Generate(new GenSpec
        {
            Name = "bench_rhine_ruhr_2p5km",
            Bbox = new double[] { 5.967227, 50.489601, 7.232773, 51.410197 },
            CellKm = 2.5,
            Seed = 123,
            Offline = true,
            UseRealDem = false,
            Features = true,
        });
        sw.Stop();

        var cells = pkg.Width * pkg.Height;
        _out.WriteLine($"rhine_ruhr 2.5km grid {pkg.Width}x{pkg.Height} = {cells} cells in {sw.ElapsedMilliseconds} ms "
            + $"({(double)sw.ElapsedMilliseconds / cells:F4} ms/cell); shipped rhine_ruhr_2p5km.json is 35x47 = 1645 cells");

        cells.Should().BeInRange(1645, 1800,
            "同一 bbox 回喂比出厂包多一行一列（meta.bbox 是结果格心范围）；涨出这个区间说明采样口径漂了");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "doc §13.1：2.5km 格距 1645 格生成必须 < 30s");
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
