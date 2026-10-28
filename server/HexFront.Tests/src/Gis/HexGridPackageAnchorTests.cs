using System;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Gis;

/// <summary>
/// GIS 生成包 → 服务器网络包的锚点约定：west/south 必须是棋盘外接矩形的左缘/底缘。
/// 前端（网格管理页、剧本管理页）按「左下角 + 行号向北」渲染，锚点写成顶行会让整片网格北移一个棋盘高度。
/// </summary>
public class HexGridPackageAnchorTests
{
    private static readonly double[] TestBbox = { 6.80, 50.90, 7.10, 51.10 };

    private static HexPackage Generate(double cellKm) => HexGenService.Generate(new GenSpec
    {
        Name = "anchor_test",
        Bbox = TestBbox,
        CellKm = cellKm,
        Seed = 7,
        Offline = true,
        UseRealDem = false,
        Features = false,
    });

    [Fact]
    public void South_Is_Board_Bottom_Not_Top_Row_Edge()
    {
        var pkg = Generate(0.5);
        var doc = HexGridPackageService.FromHexPackage(pkg, "anchor_test");
        var radius = doc.FlatToFlat / Math.Sqrt(3.0);
        var rowStep = 1.5 * radius;

        var southM = HexGridPackageService.LatToMy(doc.South);
        var expectedBottomM = pkg.Meta.MercatorOy - rowStep * (pkg.Height - 1) - radius;
        southM.Should().BeApproximately(expectedBottomM, 1.0,
            "south 须是棋盘底缘；写成 oy-radius 只到顶行下缘，会北移 (rows-1) 个行距");

        // 旧写法（oy - radius）作为回归哨兵：与新语义必须差 (rows-1) 个行距
        southM.Should().BeLessThan(pkg.Meta.MercatorOy - radius - rowStep,
            "不得回退成顶行下缘");
    }

    [Fact]
    public void West_South_East_North_Covers_Generation_Bbox()
    {
        var doc = HexGridPackageService.FromHexPackage(Generate(0.5), "anchor_test");
        var gen = HexGridPackageService.BboxOf(doc);

        gen[0].Should().BeApproximately(TestBbox[0], 0.02, "西缘");
        gen[1].Should().BeApproximately(TestBbox[1], 0.02, "南缘");
        gen[2].Should().BeApproximately(TestBbox[2], 0.02, "东缘");
        gen[3].Should().BeApproximately(TestBbox[3], 0.02, "北缘");
    }

    [Fact]
    public void Build_Round_Trip_Keeps_Engine_Origin()
    {
        var pkg = Generate(0.5);
        var doc = HexGridPackageService.FromHexPackage(pkg, "anchor_test");
        var grid = HexGridPackageService.Build(doc).Grid;

        grid.Ox.Should().BeApproximately(pkg.Meta.MercatorOx, 1e-6, "还原的 ox 须回到格 (0,0) 中心");
        grid.Oy.Should().BeApproximately(pkg.Meta.MercatorOy, 1e-6, "还原的 oy 须回到格 (0,0) 中心");
        grid.Width.Should().Be(pkg.Width);
        grid.Height.Should().Be(pkg.Height);
    }
}
