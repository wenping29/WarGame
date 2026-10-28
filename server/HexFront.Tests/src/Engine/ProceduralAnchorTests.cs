using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// 程序化棋盘的地理锚点：未绑网格包的剧本必须落在自己的战场上。
/// 曾经 origin 硬编码在一个固定墨卡托点，24 个剧本共用同一块底图，
/// 在线底图与 /api/gis/vector 真实矢量对所有房间长得一样。
/// </summary>
public class ProceduralAnchorTests
{
    private const double EarthR = 6378137.0;

    private static (GameState Game, ScenarioService.ScenarioConfig Cfg) Procedural(string scenarioId)
    {
        var path = Path.Combine(RealScenario.RepoRoot(), "data", "scenarios", scenarioId + ".json");
        var cfg = ScenarioService.LoadFromFile(path);
        // 显式不传 hexGridDir：绑了网络包的剧本（如南京/长沙）也要走 environment 这条出图分支
        return (ScenarioService.CreateGameState(cfg, RealScenario.GisDirectory, 20261028), cfg);
    }

    /// <summary>棋盘中心（格心外接框的中点）反算成经纬度。</summary>
    private static (double Lng, double Lat) BoardCenter(HexGrid grid)
    {
        var mx = grid.Ox + (grid.Width - 1) * grid.FlatToFlat / 2.0 + grid.FlatToFlat / 4.0;
        var my = grid.Oy - (grid.Height - 1) * grid.RowStep / 2.0;
        return (mx / EarthR * 180.0 / Math.PI,
                (2.0 * Math.Atan(Math.Exp(my / EarthR)) - Math.PI / 2.0) * 180.0 / Math.PI);
    }

    [Theory]
    [InlineData("nanjing_1937")]
    [InlineData("korea_c4_1951")]
    [InlineData("lugouqiao_1937")]
    [InlineData("songhu_1937")]
    public void Board_Center_Lands_On_The_Scenarios_Own_Coordinates(string scenarioId)
    {
        var (g, cfg) = Procedural(scenarioId);
        var want = cfg.Environment!.CenterLonLat!;
        var (lng, lat) = BoardCenter(g.Grid);

        lng.Should().BeInRange(want[0] - 0.2, want[0] + 0.2, $"{scenarioId} 的棋盘没落在剧本 center_lonlat 上");
        lat.Should().BeInRange(want[1] - 0.2, want[1] + 0.2, $"{scenarioId} 的棋盘没落在剧本 center_lonlat 上");
    }

    [Fact]
    public void Boards_Do_Not_All_Land_On_The_Same_Point()
    {
        var ids = new[] { "nanjing_1937", "korea_c4_1951", "wuhan_1938", "liaoshen_1948" };
        var centers = ids.ToDictionary(id => id, id => BoardCenter(Procedural(id).Game.Grid));

        centers.Values.Select(v => v.Lng).Distinct().Count().Should().Be(ids.Length, "底图/真实矢量按棋盘中心取样，中心一样就等于所有房间同一张图");

        foreach (var a in ids)
            foreach (var b in ids)
            {
                if (string.CompareOrdinal(a, b) >= 0) continue;
                var separation = Math.Abs(centers[a].Lat - centers[b].Lat) + Math.Abs(centers[a].Lng - centers[b].Lng);
                separation.Should().BeGreaterThan(1.0, $"{a} 与 {b} 的棋盘必须分处不同战场");
            }
    }

    [Fact]
    public void Center_Latitude_Scales_The_Mercator_Radius()
    {
        // center_lonlat 键名是一个词，SnakeCaseLower 会拆成 center_lon_lat；漏了 JsonPropertyName 时
        // 缩放退化成 1、锚点退化成硬编码点，这两条都会在这里亮红灯。
        var (g, cfg) = Procedural("nanjing_1937");
        var env = cfg.Environment!;
        var scale = 1.0 / Math.Cos(env.CenterLonLat![1] * Math.PI / 180.0);

        g.Grid.MercatorScale.Should().BeInRange(scale - 1e-6, scale + 1e-6);
        var wantRadius = env.CellKm * 1000.0 * scale / Math.Sqrt(3.0);
        g.Grid.Radius.Should().BeInRange(wantRadius - 1, wantRadius + 1, "格宽要按战场纬度做墨卡托缩放，不是地面公里数直接当墨卡托米");
    }
}
