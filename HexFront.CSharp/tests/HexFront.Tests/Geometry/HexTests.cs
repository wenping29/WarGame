using FluentAssertions;
using HexFront.Core.Geometry;
using Xunit;

namespace HexFront.Tests.Geometry;

public class HexTests
{
    [Fact]
    public void Key_Format_Is_Q_Comma_R()
    {
        var h = new Hex(3, 7);
        h.Key.Should().Be("3,7");
    }

    [Fact]
    public void Parse_Roundtrips_Key()
    {
        var h = Hex.Parse("12,-5");
        h.Q.Should().Be(12);
        h.R.Should().Be(-5);
        h.S.Should().Be(-7);
    }

    [Fact]
    public void Cube_Rounding_Produces_Valid_Hex()
    {
        // Fractional hex near (2.3, -1.7, -0.6) should round to (2, -2, 0)
        var h = Hex.Round(2.3, -1.7, -0.6);
        (h.Q + h.R + h.S).Should().Be(0, "cube coordinates must sum to zero");
    }

    [Fact]
    public void Neighbors_Returns_Six_Adjacent_Hexes()
    {
        var center = new Hex(0, 0);
        var neighbors = center.Neighbors();
        neighbors.Should().HaveCount(6);
        foreach (var n in neighbors)
            n.DistanceTo(center).Should().Be(1);
    }

    [Fact]
    public void Distance_Is_Max_Of_Cube_Deltas()
    {
        var a = new Hex(0, 0);
        var b = new Hex(3, -2); // s: 0 vs -1 → delta 1
        // dq=3, dr=2, ds=1 → max=3
        a.DistanceTo(b).Should().Be(3);
    }

    [Fact]
    public void Range_Includes_Center_And_All_Within_N()
    {
        var center = new Hex(0, 0);
        var range = center.Range(2);
        var list = new List<Hex>(range);
        list.Should().Contain(center);
        list.Should().HaveCount(19); // 1 + 6 + 12
        foreach (var h in list)
            h.DistanceTo(center).Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Lerp_Produces_Contiguous_Path()
    {
        var a = new Hex(0, 0);
        var b = new Hex(4, -2);
        var path = Hex.Lerp(a, b).ToList();
        path.First().Should().Be(a);
        path.Last().Should().Be(b);
        for (var i = 1; i < path.Count; i++)
            path[i].DistanceTo(path[i - 1]).Should().Be(1, "each step must be adjacent");
    }

    [Fact]
    public void CompareTo_Is_Consistent_With_Q_Then_R()
    {
        var a = new Hex(1, 3);
        var b = new Hex(1, 5);
        var c = new Hex(2, 0);
        a.CompareTo(b).Should().BeNegative();
        b.CompareTo(c).Should().BeNegative();
        a.CompareTo(a).Should().Be(0);
    }
}
