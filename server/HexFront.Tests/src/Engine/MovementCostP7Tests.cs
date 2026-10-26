using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P7-a 回归：单格机动代价（对齐 hexfront/bin/verify.dart §9 的 42–45 项，doc §6.4 地物玩法表）。
/// 这组数直接决定"一个机动脉冲能走几格"，倍率一旦走偏，前线推进速度会整体变形，
/// 而回合结算类断言只看得到"动了/没动"，看不出动了 3 格还是 4 格。
/// </summary>
public class MovementCostP7Tests
{
    private static readonly TerrainDef Clear = LibraryService.STANDARD_TERRAIN["clear"];
    private static readonly TerrainDef River = LibraryService.STANDARD_TERRAIN["river"];

    private static double Cost(TerrainDef td, Cell cell, string cls) => TurnResolver.StepCost(td, cell, cls);

    [Fact]
    public void Bare_Ground_Is_The_Reference_Cost()
    {
        var bare = new Cell { Terrain = "clear" };

        Cost(Clear, bare, "foot").Should().Be(1.0);
        Cost(Clear, bare, "motor").Should().Be(1.4);
        Cost(Clear, bare, "mech").Should().Be(1.2);
    }

    [Fact]
    public void Railway_Speeds_Up_Heavy_Traffic_But_Not_Foot()
    {
        var rail = new Cell { Terrain = "clear", Railway = true };

        Cost(Clear, rail, "motor").Should().Be(Cost(Clear, new Cell { Terrain = "clear" }, "motor") * 0.8);
        Cost(Clear, rail, "mech").Should().Be(Cost(Clear, new Cell { Terrain = "clear" }, "mech") * 0.8);
        Cost(Clear, rail, "foot").Should().Be(Cost(Clear, new Cell { Terrain = "clear" }, "foot"),
            "徒步不吃铁路红利，否则步兵沿铁路的推进速度会凭空翻倍");
    }

    [Fact]
    public void Road_And_Rail_Stack_Into_Half_The_Cost()
    {
        var roadRail = new Cell { Terrain = "clear", Road = true, Railway = true };

        Cost(Clear, roadRail, "motor").Should().BeApproximately(1.4 * 0.52, 1e-9,
            "路 0.65 × 铁 0.8 = 0.52，两个乘数缺一不可");
        Cost(Clear, roadRail, "foot").Should().BeApproximately(1.0 * 0.65, 1e-9, "徒步只吃路的折扣");
    }

    [Fact]
    public void Tunnel_Punishes_Heavy_Vehicles_Harder_Than_Foot()
    {
        var tunnel = new Cell { Terrain = "clear", Tunnel = true };
        var bare = new Cell { Terrain = "clear" };

        var mechRatio = Cost(Clear, tunnel, "mech") / Cost(Clear, bare, "mech");
        var footRatio = Cost(Clear, tunnel, "foot") / Cost(Clear, bare, "foot");

        mechRatio.Should().BeApproximately(1.5, 1e-9);
        footRatio.Should().BeApproximately(1.2, 1e-9);
        mechRatio.Should().BeGreaterThan(footRatio);
    }

    [Fact]
    public void Fording_Costs_Two_Extra_Steps_Over_A_Bridge()
    {
        var ford = new Cell { Terrain = "river", River = true };
        var bridged = new Cell { Terrain = "river", River = true, Bridge = true };

        (Cost(River, ford, "motor") - Cost(River, bridged, "motor")).Should().BeGreaterThan(1.99,
            "无桥渡河是 +2 的固定罚项而不是倍率，桥的价值就体现在这一格");
        Cost(River, bridged, "motor").Should().Be(5.0, "有桥即按普通格算，不再叠加渡河罚");
    }
}
