using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P3-b 回归：服务端指令校验（doc §3.4 + §8）。
/// 每条拒令原因都要能被触发且只触发它自己 —— 玩家看到"目标未被侦察到"却其实是超射程，
/// 就是引擎在撒谎；而该拒的没拒（尤其是用 air unit 占格断路、用 morale 代理溃退这类）会直接破坏双盲公平。
/// </summary>
public class CommandValidatorP3Tests
{
    /// <summary>两行开阔地走廊：山脉钉在 (12,0)、水钉在 (3,1)，够写出越界/跳格/不可通行各一条路径。</summary>
    private static GameState Board()
    {
        var g = new GameState
        {
            Grid = new HexGrid(0, 0, 1250, 24, 2, 2.5, 1.0),
            Cells = new Dictionary<string, Cell>(),
            Units = new List<Unit>(),
            Rules = LibraryService.DEFAULT_RULES,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            Weather = "clear",
            HourOfDay = 12,
            Tracks = new Dictionary<string, Dictionary<string, Track>>(),
        };
        for (var r = 0; r < 2; r++)
            for (var q = 0; q < 24; q++)
                g.Cells[$"{q},{r}"] = new Cell { Terrain = "clear" };
        g.Cells["12,0"] = new Cell { Terrain = "mountain" };
        g.Cells["3,1"] = new Cell { Terrain = "water" };
        return g;
    }

    private static Unit Add(GameState g, string id, string side, string type, int q, int r = 0)
    {
        var t = LibraryService.UNIT_TYPES[type];
        var u = new Unit
        {
            Id = id,
            Side = side,
            UnitType = type,
            Pos = new Hex(q, r),
            Personnel = t.Personnel,
            MaxPersonnel = t.Personnel,
            Morale = 80,
            Ammo = 1.0,
            Fuel = 1.0,
            Food = 1.0,
            Training = t.TrainingDefault,
            Experience = t.ExpDefault,
            CommanderQuality = 0.5,
            IsHQ = t.IsHQ,
        };
        g.Units.Add(u);
        return u;
    }

    /// <summary>红步兵(0,0) 红炮兵(1,0) 红雷达(2,0) 蓝战斗机(4,0) 蓝步兵(5,0) 红战斗机(11,0)。</summary>
    private static GameState Standard()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 0);
        Add(g, "A1", "red", "arty", 1);
        Add(g, "D1", "red", "radar", 2);
        Add(g, "J1", "blue", "fighter", 4);
        Add(g, "B1", "blue", "rifle", 5);
        Add(g, "F1", "red", "fighter", 11);
        return g;
    }

    private static void See(GameState g, string side, string unitId, int level)
    {
        if (!g.Tracks.TryGetValue(side, out var tracks))
            g.Tracks[side] = tracks = new Dictionary<string, Track>();
        tracks[unitId] = new Track
        {
            UnitId = unitId,
            Level = level,
            LastSeen = new Hex(5, 0),
            LastSeenTurn = g.Turn,
        };
    }

    private static Command Move(string unit, params (int q, int r)[] paths) => new()
    {
        Op = CommandOp.Move,
        UnitId = unit,
        Path = paths.Select(p => new Hex(p.q, p.r)).ToList(),
    };

    private static string Reason(GameState g, string side, params Command[] orders) =>
        CommandValidator.ValidateOrders(g, side, orders.ToList()).Select(e => e.Reason)
            .FirstOrDefault() ?? "";

    // ───────── 归属与状态 ─────────

    [Fact]
    public void Unknown_Or_Dead_Unit_Is_Rejected()
    {
        var g = Standard();
        Reason(g, "red", Move("ZZ", (9, 0))).Should().Be("no_unit");

        g.FindUnit("R1")!.Personnel = 0;
        Reason(g, "red", Move("R1", (1, 0))).Should().Be("no_unit");
    }

    [Fact]
    public void Cannot_Command_Another_Side()
    {
        var g = Standard();
        Reason(g, "red", Move("B1", (6, 0), (7, 0))).Should().Be("not_yours");
    }

    [Fact]
    public void One_Order_Per_Unit_Per_Turn()
    {
        var g = Standard();
        var errs = CommandValidator.ValidateOrders(g, "red", new List<Command>
        {
            Move("R1", (0, 0), (1, 0)),
            new() { Op = CommandOp.Rest, UnitId = "R1" },
        });
        errs.Select(e => e.Reason).Should().Contain("duplicate", "同回合重复受令必须拒，否则两条指令都会进结算");
    }

    [Fact]
    public void Routed_Status_Blocks_Everything_But_Rest()
    {
        var g = Standard();
        var u = g.FindUnit("R1")!;
        u.Status = UnitStatus.Routed;
        u.Morale = 20; // 已溃退但士气尚未见底：morale 不是溃退的代理指标

        Reason(g, "red", Move("R1", (1, 0))).Should().Be("routed");
        Reason(g, "red", new Command { Op = CommandOp.Rest, UnitId = "R1" }).Should().Be("");
    }

    [Fact]
    public void Low_Morale_Alone_Still_Allows_Orders()
    {
        var g = Standard();
        g.FindUnit("R1")!.Morale = 0;
        Reason(g, "red", Move("R1", (0, 0), (1, 0))).Should().Be("", "未落到 Routed 状态就还能受令，否则残兵连动都动不了");
    }

    // ───────── 机动 ─────────

    [Fact]
    public void Legal_Path_Is_Accepted()
    {
        var g = Standard();
        Reason(g, "red", Move("R1", (0, 0), (1, 0), (2, 0))).Should().Be("");
    }

    [Fact]
    public void Path_Rules_Have_Their_Own_Rejection_Reasons()
    {
        var g = Standard();
        Reason(g, "red", Move("R1", (0, 0))).Should().Be("bad_path");
        Reason(g, "red", Move("R1", (0, 0), (2, 0))).Should().Be("not_adjacent");
        Reason(g, "red", Move("R1", (1, 0), (2, 0))).Should().Be("path_start");
        Reason(g, "red", Move("R1", (0, 0), (-1, 0))).Should().Be("out_of_map");
        Reason(g, "red", Move("F1", (11, 0), (12, 0))).Should().Be("", "山地只是贵，不是不可通行：能拦的是江湖与境外");
        Reason(g, "red", Move("A1", (1, 0), (2, 0), (3, 0), (3, 1))).Should().Be("impassable");
    }

    [Fact]
    public void Structures_Cannot_Move()
    {
        var g = Standard();
        Reason(g, "red", Move("D1", (2, 0), (3, 0))).Should().Be("static");
    }

    [Fact]
    public void Enemy_Ground_Hex_Blocks_But_Enemy_Air_Overflight_Does_Not()
    {
        var g = Standard();
        See(g, "red", "B1", 1); // 阻挡只看下令方看得见谁：未知的敌人留给结算去遭遇
        Reason(g, "red", Move("R1", (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0))).Should().Be("blocked");
        Reason(g, "red", Move("R1", (0, 0), (1, 0), (2, 0), (3, 0), (4, 0))).Should().Be(
            "", "蓝战斗机在 (4,0) 过境：不占地也不该拦住红步兵行军");
    }

    [Fact]
    public void Unobserved_Enemy_Hex_Is_Traversable()
    {
        var g = Standard();
        Reason(g, "red", Move("R1", (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0))).Should().Be(
            "", "doc §2「移动路径只校验己方可通行性；进入未知格可能遭遇」：拿服务端真值一律拒会让遭遇战分支永远跑不到");
    }

    // ───────── 攻击令：目标合法性、暴露等级、射程 ─────────

    private static Command Attack(string unit, string target) =>
        new() { Op = CommandOp.Attack, UnitId = unit, Target = target };

    [Fact]
    public void Attack_Target_Checks()
    {
        var g = Standard();
        g.Flags["fog"] = false;
        Reason(g, "red", Attack("R1", "ZZ")).Should().Be("no_target");
        Reason(g, "red", Attack("R1", "A1")).Should().Be("friendly");
    }

    [Fact]
    public void Attack_Needs_E1_Exposure_Under_Fog()
    {
        var g = Standard();
        Reason(g, "red", Attack("F1", "B1")).Should().Be("not_observed", "没航迹就打不了：迷雾里不能凭空下令");

        See(g, "red", "B1", 0);
        Reason(g, "red", Attack("F1", "B1")).Should().Be("not_observed", "Track（level 0）只是陈旧线索，不构成暴露");

        See(g, "red", "B1", 1);
        Reason(g, "red", Attack("F1", "B1")).Should().Be("", "E1 起就够下攻击令（doc §3.4）");
    }

    [Fact]
    public void Attack_Without_Fog_Targets_Anything_In_Range()
    {
        var g = Standard();
        g.Flags["fog"] = false; // 导演部明牌：无需航迹
        Reason(g, "red", Attack("F1", "B1")).Should().Be("");
    }

    [Fact]
    public void Fog_Flag_Boxed_As_JsonElement_Still_Reads()
    {
        var g = Standard();
        g.Flags["fog"] = JsonDocument.Parse("false").RootElement; // 存档回读时的装箱形状
        Reason(g, "red", Attack("F1", "B1")).Should().Be("", "flags 回读是 JsonElement，直取 bool 会把明牌当迷雾");
    }

    [Fact]
    public void Attack_Reach_Comes_From_The_Type_Table()
    {
        var g = Standard();
        g.Flags["fog"] = false;
        Add(g, "B2", "blue", "rifle", 4); // 炮兵射程用对地目标验，别让"打不到飞机"这条混进来

        Reason(g, "red", Attack("R1", "B1")).Should().Be("out_of_range", "步枪直射 1 格，(0,0)→(5,0) 打不到");
        Reason(g, "red", Attack("A1", "B2")).Should().Be("", "炮兵 range=3，(1,0)→(4,0) 正好够");
        Reason(g, "red", Attack("A1", "B1")).Should().Be("out_of_range");
        Reason(g, "red", Attack("F1", "B1")).Should().Be("", "航空兵按作战半径（fighter air_range=10）");

        var g2 = Standard();
        g2.Flags["fog"] = false;
        g2.UnitTypes["arty"] = new UnitTypeDef
        {
            Id = "arty", Label = "炮兵", Cls = "motor", Mobility = 4,
            Firepower = 6, Personnel = 240, Consumption = 2.4, Range = 8,
        };
        Reason(g2, "red", Attack("A1", "B1")).Should().Be("", "剧本覆盖射程要立刻生效，射程不能写死在校验器里");
    }

    [Fact]
    public void Ground_Units_Need_Anti_Air_To_Engage_An_Aircraft()
    {
        var g = Standard();
        g.Flags["fog"] = false; // 明牌，排除 not_observed 干扰
        Add(g, "S1", "red", "aa", 3);

        Reason(g, "red", Attack("R1", "J1")).Should().Be("cannot_hit_air",
            "兵种表里只有 AntiAir 这一项地面反机能力；放行步枪打飞机等于让步兵班删光敌方航空兵");
        Reason(g, "red", Attack("S1", "J1")).Should().Be("", "防空单位接战空中目标是它的本职（aa→战斗机 相邻）");
    }

    // ───────── 间接火力 ─────────

    private static Command Fire(string unit, int q, int r = 0) =>
        new() { Op = CommandOp.Fire, UnitId = unit, Zone = new Hex(q, r) };

    [Fact]
    public void Only_Indirect_Units_Can_Cover_A_Zone()
    {
        var g = Standard();
        Reason(g, "red", Fire("R1", 3)).Should().Be("not_artillery");
    }

    [Fact]
    public void Blind_Fire_Is_Allowed_Up_To_One_Hex_Beyond_Range()
    {
        var g = Standard();
        Reason(g, "red", Fire("A1", 5)).Should().Be("", "盲打允许：不要求目视，精度降级留给结算");
        Reason(g, "red", Fire("A1", 7)).Should().Be("out_of_range");
        Reason(g, "red", Fire("A1", 30)).Should().Be("bad_zone");
    }

    // ───────── 空中任务 ─────────

    private static Command Air(string unit, string mission, int? q = null, int? r = null) => new()
    {
        Op = CommandOp.AirMission,
        UnitId = unit,
        Mission = mission,
        Zone = q == null ? null : new Hex(q.Value, r ?? 0),
    };

    [Fact]
    public void Air_Mission_Rules()
    {
        var g = Standard();
        Reason(g, "red", Air("R1", "patrol", 3)).Should().Be("not_air");
        Reason(g, "red", Air("F1", "patrol")).Should().Be("bad_zone", "派任务不给任务区 = 无效");
        Reason(g, "red", Air("F1", "patrol", 30)).Should().Be("bad_zone");
        Reason(g, "red", Air("F1", "strike", 21)).Should().Be("", "作战半径内（air_range=10）");
        Reason(g, "red", Air("F1", "strike", 23)).Should().Be("out_of_range");
        Reason(g, "red", Air("F1", "none")).Should().Be("", "收队（mission=none）不需要任务区");
    }

    // ───────── 线上形状 ─────────

    [Fact]
    public void Unrecognized_Op_Echoes_Back_As_Unknown_Op()
    {
        var g = Standard();
        using var doc = JsonDocument.Parse("""{"op":"fly","unit":"R1"}""");
        var cmd = CommandWire.FromJson(doc.RootElement);

        cmd.Should().NotBeNull("认不出的动作也要进指令流，静默丢弃等于玩家按了没反应");
        var errs = CommandValidator.ValidateOrders(g, "red", new List<Command> { cmd! });
        errs.Should().ContainSingle();
        errs[0].Reason.Should().Be("unknown_op");
        errs[0].Message.Should().Contain("fly", "拒因要回显玩家实际写的那个词");
    }

    [Fact]
    public void Known_Ops_Round_Trip_Through_The_Wire()
    {
        foreach (var name in new[] { "move", "attack", "fire", "dig", "rest", "air_mission", "halt" })
        {
            using var doc = JsonDocument.Parse($"{{\"op\":\"{name}\",\"unit\":\"R1\"}}");
            var cmd = CommandWire.FromJson(doc.RootElement);
            CommandWire.OpName(cmd!.Op).Should().Be(name);
            CommandWire.OpEcho(cmd).Should().Be(name);
        }
    }
}
