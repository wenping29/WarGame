using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Geometry;
using Xunit;

namespace HexFront.Tests.Geometry;

public class HexTests
{
    private static HexGrid MakeGrid() => new(
        ox: 1_000.0, oy: 2_000.0, radius: 500.0,
        width: 9, height: 6, cellGroundKm: 2.5, mercatorScale: 1.0);

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
    }

    [Fact]
    public void Axial_Conversion_Roundtrips()
    {
        foreach (var r in new[] { -3, -2, -1, 0, 1, 2, 3, 4 })
        for (var q = -3; q <= 3; q++)
        {
            var h = new Hex(q, r);
            Hex.FromAxial(h.AxialQ, h.AxialR).Should().Be(h, $"offset({q},{r}) must survive the axial round-trip");
        }
    }

    [Fact]
    public void Cube_Rounding_Produces_Valid_Axial_Hex()
    {
        // fractional axial (2.3, -1.7, -0.6) rounds to axial (2, -2, 0)
        var h = Hex.Round(2.3, -1.7);
        h.AxialQ.Should().Be(2);
        h.AxialR.Should().Be(-2);
        (h.AxialQ + h.AxialR + h.AxialS).Should().Be(0, "cube coordinates must sum to zero");
    }

    [Fact]
    public void Neighbor_Deltas_Follow_Row_Parity()
    {
        static (int, int)[] Deltas(Hex from) =>
            from.Neighbors().Select(n => (n.Q - from.Q, n.R - from.R)).ToArray();

        // even row: this row is not shifted
        Deltas(new Hex(4, 0)).Should().Equal(
            (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1));

        // odd row: this row is shifted right by half a cell
        Deltas(new Hex(4, 1)).Should().Equal(
            (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1));
    }

    [Fact]
    public void Neighbors_Returns_Six_Adjacent_Hexes()
    {
        foreach (var center in new[] { new Hex(0, 0), new Hex(5, 1), new Hex(5, 2) })
        {
            var neighbors = center.Neighbors().ToList();
            neighbors.Should().HaveCount(6);
            foreach (var n in neighbors)
                n.DistanceTo(center).Should().Be(1);
            // adjacency must be symmetric
            foreach (var n in neighbors)
                n.Neighbors().Should().Contain(center);
        }
    }

    [Fact]
    public void Distance_Counts_Board_Rings()
    {
        new Hex(0, 0).DistanceTo(new Hex(3, 0)).Should().Be(3, "straight east along one row");
        new Hex(0, 0).DistanceTo(new Hex(0, 3)).Should().Be(3, "straight south alternates column by parity");
        new Hex(0, 0).DistanceTo(new Hex(0, 0)).Should().Be(0);
        // 3 rows north-east from an even row gains half a cell per step, so 3 more columns
        // still costs 2 extra east steps → 5 hexes
        new Hex(2, 4).DistanceTo(new Hex(5, 1)).Should().Be(5);
        new Hex(0, 0).DistanceTo(new Hex(3, -2)).Should().Be(4);
    }

    [Fact]
    public void Range_Includes_Center_And_All_Within_N()
    {
        var center = new Hex(0, 0);
        var list = new List<Hex>(center.Range(2));
        list.Should().Contain(center);
        list.Should().HaveCount(19); // 1 + 6 + 12
        list.Should().OnlyHaveUniqueItems();
        foreach (var h in list)
            h.DistanceTo(center).Should().BeLessThanOrEqualTo(2);
        center.Range(0).Should().Equal(center);
    }

    [Fact]
    public void Lerp_Produces_Contiguous_Path()
    {
        foreach (var b in new[] { new Hex(8, 0), new Hex(0, 5), new Hex(7, 3), new Hex(2, 5) })
        {
            var a = new Hex(0, 0);
            var path = Hex.Lerp(a, b).ToList();
            path.First().Should().Be(a);
            path.Last().Should().Be(b);
            path.Should().OnlyHaveUniqueItems();
            path.Count.Should().Be(a.DistanceTo(b) + 1);
            for (var i = 1; i < path.Count; i++)
                path[i].DistanceTo(path[i - 1]).Should().Be(1, "each step must be adjacent");
        }
    }

    [Fact]
    public void Center_Uses_Rectangular_Brick_Layout()
    {
        var grid = MakeGrid();
        // A sheared (axial) board would push row r right by r/2 cells; a brick board
        // only ever shifts odd rows by half a cell. This is what makes the board a rectangle.
        for (var r = 0; r < grid.Height; r++)
        {
            var shift = grid.Center(0, r).X - grid.Center(0, 0).X;
            shift.Should().BeApproximately((r & 1) * grid.FlatToFlat / 2.0, 1e-9, $"row {r} shift");
            (grid.Oy - grid.Center(0, r).Y).Should().BeApproximately(r * grid.RowStep, 1e-9);
        }
    }

    [Fact]
    public void Cells_Tile_A_Rectangle()
    {
        var grid = MakeGrid();
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var h in grid.AllCells())
        {
            var (x, y) = grid.Center(h.Q, h.R);
            xs.Add(x);
            ys.Add(y);
        }
        var left = xs.Min() - grid.FlatToFlat / 2.0;
        var right = xs.Max() + grid.FlatToFlat / 2.0;
        var top = ys.Max() + grid.Radius;
        var bottom = ys.Min() - grid.Radius;
        (right - left).Should().BeApproximately(grid.BoardWidthM, 1e-9);
        (top - bottom).Should().BeApproximately(grid.BoardHeightM, 1e-9);
        // every row starts and ends on the same two vertical edges
        for (var r = 0; r < grid.Height; r++)
            grid.Center(0, r).X.Should().Be(left + grid.FlatToFlat / 2.0 + (r & 1) * grid.FlatToFlat / 2.0);
    }

    [Fact]
    public void Nearest_Inverts_Center()
    {
        var grid = MakeGrid();
        foreach (var h in grid.AllCells())
        {
            var (x, y) = grid.Center(h.Q, h.R);
            grid.Nearest(x, y).Should().Be(h);
            // and slightly off-centre still resolves to the same cell
            grid.Nearest(x + grid.Radius * 0.2, y - grid.Radius * 0.2).Should().Be(h);
        }
    }

    [Fact]
    public void Neighbors_Match_Pixel_Adjacency()
    {
        // The engine's notion of "adjacent" must be exactly "shares an edge on screen",
        // otherwise a click on a visually adjacent hex produces an illegal move order.
        var grid = MakeGrid();
        var cells = grid.AllCells().ToList();

        foreach (var h in cells)
        {
            var (hx, hy) = grid.Center(h.Q, h.R);
            var byPixel = cells
                .Where(o => o != h)
                .Where(o =>
                {
                    var (ox, oy) = grid.Center(o.Q, o.R);
                    var d = Math.Sqrt((ox - hx) * (ox - hx) + (oy - hy) * (oy - hy));
                    return Math.Abs(d - grid.FlatToFlat) < 1e-6;
                })
                .ToList();

            byPixel.Should().BeEquivalentTo(h.Neighbors().Where(grid.Contains),
                $"pixel neighbours of {h} must equal logical neighbours");
        }
    }

    [Fact]
    public void DistanceField_And_Pathfind_Agree_With_Board_Metrics()
    {
        var grid = MakeGrid();
        var dist = grid.DistanceField(new[] { new Hex(0, 0) });
        dist[new Hex(0, 0)].Should().Be(0);
        dist[new Hex(4, 4)].Should().Be(new Hex(0, 0).DistanceTo(new Hex(4, 4)));

        var path = grid.Pathfind(new Hex(0, 0), new Hex(4, 4));
        path.First().Should().Be(new Hex(0, 0));
        path.Last().Should().Be(new Hex(4, 4));
        for (var i = 1; i < path.Count; i++)
            path[i].DistanceTo(path[i - 1]).Should().Be(1);
    }
}
