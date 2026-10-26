using System;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Gis;

/// <summary>
/// 格网方案必须与 hexgen.py 的 --plan 逐字段一致（前端只显示这份方案，不自己算几何）。
/// 期望值取自 `python tools/gis/hexgen.py --plan --bbox ...` 的实测输出。
/// </summary>
public class GisPlanServiceTests
{
    private static readonly double[] RhineRuhr = { 6.8, 51.2, 7.3, 51.6 };

    [Fact]
    public void Plan_Recommends_1km_Over_36x52_For_Rhine_Ruhr_Box()
    {
        var (cellKm, reasons) = GisPlanService.Recommend(RhineRuhr);
        cellKm.Should().Be(1.0);
        GisPlanService.GridSize(RhineRuhr, cellKm).Should().Be((36, 52));
        reasons[0].Should().Be("目标 2400 格左右（实地 35×44 km）⇒ 理论格距 0.86 km");
        reasons.Should().Contain(r => r.Contains("高程栅格分辨率", StringComparison.Ordinal));
        reasons[^1].Should().Be("对边距按实地计，中心纬度墨卡托放大 1.60 倍");
    }

    [Fact]
    public void Grid_Uses_0866_Row_Step_Not_The_Full_Vertex_Distance()
    {
        // 44.23 km 南北 ÷ (0.866×1 km) ≈ 51 行；若按对边距直行距只有 44 行 ⇒ 少铺 20% 的格
        var (w, h) = GisPlanService.GridSize(RhineRuhr, 1.0);
        h.Should().BeGreaterThan(w);
        var rows = (RhineRuhr[3] - RhineRuhr[1]) * 110.574 / (0.866 * 1.0);
        rows.Should().BeApproximately(h - 1, 0.6);
    }

    [Fact]
    public void Cell_Count_Matches_Area_Estimate_Plus_Edge_Cells()
    {
        // 每轴向上取整后再 +1（两端格心都算），所以实际格数略多于「面积/(0.866·c²)」
        var (w, h) = GisPlanService.GridSize(RhineRuhr, 2.0);
        var (gwm, ghm) = GisPlanService.BboxGroundKm(RhineRuhr);
        (w, h).Should().Be((18, 27));
        ((double)w * h).Should().BeApproximately(gwm * ghm / (0.866 * 4.0), 60);
    }

    [Fact]
    public void Recommend_Drops_A_Ladder_Rung_To_Fill_12_Cells_On_Short_Side()
    {
        // 6×60 km 的细长条：按面积算出的理论格距放不下短边 12 格，必须降档并说明理由
        var narrow = new double[] { 7.0, 51.13, 7.0858, 51.6727 };
        var (cellKm, reasons) = GisPlanService.Recommend(narrow);
        cellKm.Should().Be(0.5);
        reasons.Should().Contain(r => r.Contains("为在短边铺够 12 格", StringComparison.Ordinal));
    }

    [Fact]
    public void Recommend_Takes_Minimum_Rung_When_Box_Is_Too_Small()
    {
        var tiny = new double[] { 7.0, 51.4, 7.009, 51.409 };   // ≈0.6×1 km
        var (cellKm, reasons) = GisPlanService.Recommend(tiny);
        cellKm.Should().Be(GisPlanService.CellLadderKm[0]);
        reasons.Should().Contain(r => r.Contains("范围过小", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_Payload_Exposes_Ladder_And_Current_With_Wire_Field_Names()
    {
        var plan = GisPlanService.PlanPayload(RhineRuhr, 1.5);
        plan["ok"]!.GetValue<bool>().Should().BeTrue();
        plan["target_cells"]!.GetValue<int>().Should().Be(GisPlanService.TargetCells);
        plan["min_side"]!.GetValue<int>().Should().Be(GisPlanService.MinSide);
        plan["dem_floor_km"]!.GetValue<double>().Should().Be(GisPlanService.DemFloorKm);
        plan["max_cells"]!.GetValue<int>().Should().Be(GisPlanService.MaxCells);
        plan["bbox"]!.AsArray().Select(v => v!.GetValue<double>()).Should().Equal(RhineRuhr);
        plan["ground_km"]!.AsArray()[0]!.GetValue<double>().Should().Be(34.73);
        plan["mercator_scale"]!.GetValue<double>().Should().BeGreaterThan(1.6);
        plan["ladder"]!.AsArray().Should().HaveCount(GisPlanService.CellLadderKm.Length);
        var cur = plan["current"]!.AsObject();
        cur["cell_km"]!.GetValue<double>().Should().Be(1.5);
        cur["cells"]!.GetValue<int>().Should().Be(cur["width"]!.GetValue<int>() * cur["height"]!.GetValue<int>());
    }

    [Fact]
    public void Ladder_Labels_Split_Recommended_Heavy_And_Coarse()
    {
        var labels = GisPlanService.PlanPayload(RhineRuhr, null)["ladder"]!.AsArray()
            .ToDictionary(r => r!["cell_km"]!.GetValue<double>(), r => r!["label"]!.GetValue<string>());
        labels[1.0].Should().Be("recommended");
        labels[0.5].Should().Be("heavy");        // 7000+ 格 > 2.5×2400
        labels[5.0].Should().Be("coarse");       // 短边不足 12 格
        labels[2.5].Should().Be("ok");
        GisPlanService.PlanPayload(RhineRuhr, null)["current"]!.Should().BeNull();
    }

    [Fact]
    public void Grid_Center_Is_Mercator_Midpoint_Not_Latitude_Mean()
    {
        var (_, lat) = GisPlanService.GridCenter(RhineRuhr);
        lat.Should().BeGreaterThan((RhineRuhr[1] + RhineRuhr[3]) / 2.0);
        GisPlanService.MercatorScale(0).Should().Be(1.0);
    }

    [Fact]
    public void Invalid_Bbox_Fails_Instead_Of_Quietly_Planning()
    {
        var act = () => GisPlanService.GridSize(new double[] { 7.3, 51.6, 6.8, 51.2 }, 1.0);
        act.Should().Throw<GisPlanException>().WithMessage("*西<东*");
    }

    [Fact]
    public void Generation_And_Plan_Share_One_Row_Column_Algorithm()
    {
        var spec = new GenSpec { Name = "parity", Bbox = RhineRuhr, CellKm = 2.5, Seed = 7 };
        var pkg = HexGenService.Generate(spec);
        pkg.Meta.Width.Should().Be(GisPlanService.GridSize(RhineRuhr, 2.5).Width);
        pkg.Meta.Height.Should().Be(GisPlanService.GridSize(RhineRuhr, 2.5).Height);
        // 行距 = 0.866×对边距 ⇒ meta 半径必须满足 sqrt(3)·radius = cell_ground·scale
        var scale = GisPlanService.MercatorScale(GisPlanService.GridCenter(RhineRuhr).CenterLat);
        (Math.Sqrt(3.0) * pkg.Meta.RadiusM).Should().BeApproximately(2.5 * 1000.0 * scale, 1e-6);
    }
}
