using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core;

namespace HexFront.Tools.Commands;

/// <summary>
/// update-scenarios 子命令：按 docs 各战役文档的『模板对应与修正建议』批量更新 data/scenarios/*.json。
/// 忠实移植 tools/update_scenarios_from_docs.py 的 UPDATES 表 + apply_ops（cmd/move/split/align/flags/schedule/victory/desc）。
/// </summary>
public static class UpdateScenariosCommand
{
    private enum OpKind { Cmd, Move, Split, Align, Schedule, Victory, Desc, Flags }

    private sealed record Op(OpKind Kind)
    {
        public string? Side;
        public string? Name;
        public double Q;
        public string? Typ;
        public int[]? At;
        public int Occ;
        public int[][]? Positions;
        public string? LightSide;
        public (int From, int To, string Weather)[]? Segs;
        public string? OldVictory;
        public string? NewVictory;
        public string? Text;
        public Dictionary<string, object?>? Kw;
    }

    // ---- op 构造器（镜像 Python 的 cmd/mv/sp/split/align/sched/vic/desc/flags）----
    private static Op Cmd(string side, string name, double q) => new(OpKind.Cmd) { Side = side, Name = name, Q = q };
    private static Op Mv(string side, string typ, int[] at, int occ = 0) => new(OpKind.Move) { Side = side, Typ = typ, At = at, Occ = occ };
    private static Op Spl(string side, string typ, int[][] positions) => new(OpKind.Split) { Side = side, Typ = typ, Positions = positions };
    private static Op Align(string lightSide = "red") => new(OpKind.Align) { LightSide = lightSide };
    private static Op Sched(params (int, int, string)[] segs) => new(OpKind.Schedule) { Segs = segs };
    private static Op Vic(string side, string oldV, string newV) => new(OpKind.Victory) { Side = side, OldVictory = oldV, NewVictory = newV };
    private static Op Desc(string text) => new(OpKind.Desc) { Text = text };
    private static Op Flg(params (string, object?)[] kv) => new(OpKind.Flags)
    {
        Kw = kv.ToDictionary(x => x.Item1, x => x.Item2),
    };

    private static readonly Dictionary<string, List<Op>> Updates = new()
    {
        ["lugouqiao_1937"] = new()
        {
            Cmd("red", "冯治安（第37师·卢沟桥当面）", 0.72),
            Cmd("blue", "河边正三（驻屯步兵旅团长）", 0.68),
            Mv("red", "hq", new[]{13,9}),
            Mv("red", "light_inf", new[]{13,9}, 0),
            Spl("red", "light_inf", new[]{ new[]{13,9}, new[]{12,10}, new[]{14,12} }),
            Mv("red", "rifle", new[]{12,9}, 0),
            Spl("red", "rifle", new[]{ new[]{12,9}, new[]{13,10} }),
            Mv("red", "gun", new[]{12,11}), Mv("red", "arty", new[]{11,9}),
            Mv("red", "engineer", new[]{11,10}), Mv("red", "signals", new[]{11,11}),
            Mv("red", "aa", new[]{12,8}),
            Mv("blue", "hq", new[]{18,9}),
            Spl("blue", "light_inf", new[]{ new[]{17,8}, new[]{17,10}, new[]{18,9} }),
            Spl("blue", "rifle", new[]{ new[]{18,8}, new[]{18,10} }),
            Mv("blue", "gun", new[]{17,9}), Mv("blue", "arty", new[]{19,9}),
            Mv("blue", "engineer", new[]{16,9}), Mv("blue", "signals", new[]{19,10}),
            Mv("blue", "recy", new[]{20,9}),
        },
        ["mukden_1931"] = new()
        {
            Cmd("red", "赵镇藩（第7旅参谋长·当夜主事）", 0.62),
            Cmd("blue", "本庄繁（关东军司令官）", 0.65),
            Mv("red", "hq", new[]{14,10}),
            Spl("red", "light_inf", new[]{ new[]{13,10}, new[]{14,11}, new[]{13,9} }),
            Mv("red", "rifle", new[]{14,11}, 0),
            Spl("red", "rifle", new[]{ new[]{14,11}, new[]{15,10} }),
            Mv("red", "gun", new[]{15,10}), Mv("red", "arty", new[]{13,11}),
            Mv("red", "engineer", new[]{14,10}), Mv("red", "signals", new[]{13,11}),
            Mv("red", "recy", new[]{11,10}), Mv("red", "aa", new[]{13,9}),
            Mv("blue", "hq", new[]{13,5}),
            Spl("blue", "light_inf", new[]{ new[]{13,7}, new[]{14,8}, new[]{12,7} }),
            Spl("blue", "rifle", new[]{ new[]{14,9}, new[]{13,8} }),
            Mv("blue", "gun", new[]{13,6}), Mv("blue", "arty", new[]{14,7}),
            Mv("blue", "engineer", new[]{12,6}), Mv("blue", "signals", new[]{14,6}),
            Mv("blue", "recy", new[]{15,6}),
        },
        ["songhu_1937"] = new()
        {
            Cmd("red", "张治中（第9集团军总司令）", 0.72),
            Cmd("blue", "松井石根（上海派遣军司令官）", 0.70),
            Mv("red", "hq", new[]{7,10}),
            Mv("red", "light_inf", new[]{9,9}, 0), Mv("red", "rifle", new[]{9,12}),
            Mv("red", "gun", new[]{8,11}), Mv("red", "arty", new[]{7,9}),
            Mv("red", "engineer", new[]{8,10}), Mv("red", "signals", new[]{8,11}),
            Mv("red", "aa", new[]{8,7}),
            Mv("blue", "hq", new[]{21,10}),
            Spl("blue", "light_inf", new[]{ new[]{19,9}, new[]{19,12} }),
            Spl("blue", "rifle", new[]{ new[]{20,10}, new[]{21,13} }),
            Mv("blue", "gun", new[]{20,11}), Mv("blue", "arty", new[]{21,9}),
            Mv("blue", "engineer", new[]{20,10}), Mv("blue", "signals", new[]{22,11}),
            Mv("blue", "recy", new[]{23,10}),
        },
        ["nanjing_1937"] = new()
        {
            Cmd("red", "唐生智（卫戍军总司令）", 0.55),
            Cmd("blue", "朝香宫鸠彦（上海派遣军司令官）", 0.68),
            Mv("red", "hq", new[]{10,10}),
            Mv("red", "light_inf", new[]{9,9}, 0), Mv("red", "rifle", new[]{9,12}),
            Mv("red", "gun", new[]{8,11}), Mv("red", "arty", new[]{7,9}),
            Mv("red", "engineer", new[]{9,10}), Mv("red", "signals", new[]{8,11}),
            Mv("red", "aa", new[]{9,7}),
            Mv("blue", "hq", new[]{20,10}),
            Spl("blue", "light_inf", new[]{ new[]{18,9}, new[]{18,12} }),
            Spl("blue", "rifle", new[]{ new[]{19,10}, new[]{17,13} }),
            Mv("blue", "gun", new[]{19,11}), Mv("blue", "arty", new[]{20,9}),
            Mv("blue", "engineer", new[]{19,10}), Mv("blue", "signals", new[]{21,11}),
            Mv("blue", "recy", new[]{22,10}),
        },
        ["taierzhuang_1938"] = new()
        {
            Cmd("red", "孙连仲（第2集团军总司令）", 0.78),
            Cmd("blue", "濑谷启（第33旅团长·濑谷支队）", 0.62),
            Mv("red", "hq", new[]{9,9}),
            Spl("red", "light_inf", new[]{ new[]{12,9}, new[]{11,10}, new[]{10,7} }),
            Mv("red", "rifle", new[]{12,10}, 0),
            Spl("red", "rifle", new[]{ new[]{12,10}, new[]{11,11} }),
            Mv("red", "gun", new[]{9,10}), Mv("red", "arty", new[]{9,8}),
            Mv("red", "engineer", new[]{10,9}), Mv("red", "signals", new[]{9,11}),
            Mv("red", "recy", new[]{7,10}), Mv("red", "aa", new[]{10,7}),
            Mv("blue", "hq", new[]{13,3}),
            Spl("blue", "light_inf", new[]{ new[]{12,5}, new[]{13,6} }),
            Spl("blue", "rifle", new[]{ new[]{13,4}, new[]{12,6} }),
            Mv("blue", "gun", new[]{12,3}), Mv("blue", "arty", new[]{13,3}),
            Mv("blue", "engineer", new[]{11,3}), Mv("blue", "signals", new[]{13,2}),
            Mv("blue", "recy", new[]{14,2}),
        },
        ["xuzhou_1938"] = new()
        {
            Cmd("red", "李宗仁（第五战区司令长官）", 0.85),
            Cmd("blue", "寺内寿一（华北方面军司令官）", 0.75),
            Mv("red", "hq", new[]{9,10}),
            Mv("red", "light_inf", new[]{10,9}, 0), Mv("red", "rifle", new[]{10,11}),
            Mv("red", "gun", new[]{9,11}), Mv("red", "arty", new[]{8,10}),
            Mv("red", "engineer", new[]{9,10}), Mv("red", "signals", new[]{8,11}),
            Mv("red", "aa", new[]{9,7}),
            Mv("blue", "hq", new[]{14,3}),
            Spl("blue", "light_inf", new[]{ new[]{13,4}, new[]{14,17} }),
            Spl("blue", "rifle", new[]{ new[]{15,5}, new[]{14,18} }),
            Mv("blue", "gun", new[]{13,3}), Mv("blue", "arty", new[]{12,3}),
            Mv("blue", "engineer", new[]{13,2}), Mv("blue", "signals", new[]{14,2}),
            Mv("blue", "recy", new[]{15,2}),
        },
        ["wuhan_1938"] = new()
        {
            Cmd("red", "陈诚（第九战区司令长官）", 0.72),
            Cmd("blue", "冈村宁次（第11军司令官）", 0.80),
            Mv("red", "hq", new[]{7,10}),
            Mv("red", "light_inf", new[]{10,8}, 0), Mv("red", "rifle", new[]{10,12}),
            Mv("red", "gun", new[]{8,11}), Mv("red", "arty", new[]{7,9}),
            Mv("red", "engineer", new[]{9,10}), Mv("red", "signals", new[]{8,11}),
            Mv("red", "aa", new[]{9,7}),
            Mv("blue", "hq", new[]{22,10}),
            Spl("blue", "light_inf", new[]{ new[]{20,9}, new[]{21,12} }),
            Spl("blue", "rifle", new[]{ new[]{19,10}, new[]{22,12} }),
            Mv("blue", "gun", new[]{21,10}), Mv("blue", "arty", new[]{22,9}),
            Mv("blue", "engineer", new[]{20,10}), Mv("blue", "signals", new[]{22,11}),
            Mv("blue", "recy", new[]{23,10}),
        },
        ["changsha_1941"] = new()
        {
            Cmd("red", "薛岳（第九战区司令长官）", 0.85),
            Cmd("blue", "阿南惟几（第11军司令官）", 0.65),
            Mv("red", "hq", new[]{9,9}),
            Spl("red", "light_inf", new[]{ new[]{11,8}, new[]{10,10}, new[]{10,7} }),
            Mv("red", "rifle", new[]{10,11}, 0),
            Spl("red", "rifle", new[]{ new[]{10,11}, new[]{9,11} }),
            Mv("red", "gun", new[]{9,10}), Mv("red", "arty", new[]{9,9}),
            Mv("red", "engineer", new[]{10,9}), Mv("red", "signals", new[]{9,11}),
            Mv("red", "recy", new[]{7,10}), Mv("red", "aa", new[]{10,7}),
            Mv("blue", "hq", new[]{12,2}),
            Spl("blue", "light_inf", new[]{ new[]{11,5}, new[]{13,5} }),
            Spl("blue", "rifle", new[]{ new[]{11,6}, new[]{13,6} }),
            Mv("blue", "gun", new[]{11,4}), Mv("blue", "arty", new[]{12,3}),
            Mv("blue", "engineer", new[]{11,3}), Mv("blue", "signals", new[]{12,4}),
            Mv("blue", "recy", new[]{12,2}),
            Sched((1,4,"overcast"), (5,7,"clear"), (8,13,"overcast")),
        },
        ["zhongtiaoshan_1941"] = new()
        {
            Cmd("red", "曾万钟（第5集团军总司令）", 0.55),
            Cmd("blue", "多田骏（华北方面军司令官）", 0.78),
            Flg(("strict_supply", true)),
            Mv("red", "hq", new[]{7,10}),
            Spl("red", "light_inf", new[]{ new[]{10,9}, new[]{11,11}, new[]{16,6} }),
            Spl("red", "rifle", new[]{ new[]{13,13}, new[]{14,5}, new[]{12,12} }),
            Mv("red", "gun", new[]{9,10}), Mv("red", "arty", new[]{8,10}),
            Mv("red", "engineer", new[]{10,10}), Mv("red", "signals", new[]{7,10}),
            Mv("red", "aa", new[]{10,8}),
            Mv("blue", "hq", new[]{14,3}),
            Spl("blue", "light_inf", new[]{ new[]{12,4}, new[]{20,4} }),
            Spl("blue", "rifle", new[]{ new[]{21,11}, new[]{4,13} }),
            Mv("blue", "gun", new[]{12,3}), Mv("blue", "arty", new[]{13,2}),
            Mv("blue", "engineer", new[]{12,2}), Mv("blue", "signals", new[]{13,2}),
            Mv("blue", "recy", new[]{14,2}),
        },
        ["shipai_1943"] = new()
        {
            Cmd("red", "胡琏（第11师师长）", 0.82),
            Cmd("blue", "澄田赉四郎（第39师团长）", 0.62),
            Mv("red", "hq", new[]{10,10}),
            Spl("red", "light_inf", new[]{ new[]{10,9}, new[]{9,11}, new[]{8,8} }),
            Mv("red", "rifle", new[]{9,11}, 0),
            Spl("red", "rifle", new[]{ new[]{9,11}, new[]{8,10} }),
            Mv("red", "gun", new[]{8,10}), Mv("red", "arty", new[]{8,9}),
            Mv("red", "engineer", new[]{9,10}), Mv("red", "signals", new[]{8,11}),
            Mv("red", "recy", new[]{6,10}), Mv("red", "aa", new[]{9,8}),
            Mv("blue", "hq", new[]{18,10}),
            Spl("blue", "light_inf", new[]{ new[]{16,9}, new[]{17,11}, new[]{16,12} }),
            Spl("blue", "rifle", new[]{ new[]{17,10}, new[]{15,10} }),
            Mv("blue", "gun", new[]{17,9}), Mv("blue", "arty", new[]{18,9}),
            Mv("blue", "engineer", new[]{16,10}), Mv("blue", "signals", new[]{18,11}),
            Mv("blue", "recy", new[]{19,10}),
        },
        ["menglianggu_1947"] = new()
        {
            Cmd("red", "陈毅·粟裕（华野司令员兼政委·负责战役指挥）", 0.88),
            Cmd("blue", "张灵甫（整编第74师师长）", 0.72),
        },
        ["huaihai_1948"] = new()
        {
            Cmd("red", "粟裕（华野代司令员兼代政委）", 0.90),
            Cmd("blue", "杜聿明（徐州剿总副总司令·前进指挥部主任）", 0.68),
            Sched((1,4,"overcast"), (9,12,"snow"), (13,15,"clear")),
        },
        ["liaoshen_1948"] = new()
        {
            Cmd("red", "林彪·罗荣桓（东北野战军司令员·政委）", 0.88),
            Cmd("blue", "卫立煌（东北剿总总司令）", 0.60),
            Sched((8,9,"overcast"), (11,13,"overcast"), (14,16,"clear")),
        },
        ["pingjin_1949"] = new()
        {
            Cmd("red", "林彪·刘亚楼（东北野战军司令员·参谋长）", 0.88),
            Cmd("blue", "陈长捷（天津警备司令）", 0.62),
            Sched((1,5,"clear"), (6,8,"snow"), (10,11,"clear"), (12,12,"overcast")),
        },
        ["yudong_1948"] = new()
        {
            Cmd("red", "粟裕（华野代司令员兼代政委·全权指挥）", 0.92),
            Cmd("blue", "李仲辛（整编第66师师长·开封城防）", 0.50),
            Mv("blue", "hq", new[]{12,10}),
            Mv("blue", "rifle", new[]{11,9}, 0), Mv("blue", "rifle", new[]{12,11}, 1),
            Mv("blue", "rifle_auto", new[]{14,10}), Mv("blue", "tank", new[]{17,9}),
            Mv("blue", "gun", new[]{13,10}), Mv("blue", "arty", new[]{14,11}),
            Mv("blue", "aa", new[]{12,9}), Mv("blue", "engineer", new[]{12,10}),
            Mv("blue", "recy", new[]{11,10}),
            Vic("red", "16,10", "12,10"),
        },
        ["dujiang_1949"] = new(), // 已含史实数据，跳过
        // ===== 抗美援朝系列 =====
        ["korea_c1_1950"] = new()
        {
            Cmd("red", "彭德怀（志愿军司令员兼政委）", 0.88),
            Cmd("blue", "沃克（美第8集团军司令官）", 0.68),
            Flg(("night_from", 19), ("night_to", 6)),
            Sched((1,2,"clear"), (3,6,"overcast"), (8,9,"rain"), (10,14,"clear")),
        },
        ["korea_c2_1950"] = new()
        {
            Cmd("red", "彭德怀（志愿军司令员兼政委）", 0.88),
            Cmd("blue", "沃克（美第8集团军司令官）", 0.65),
        },
        ["korea_c3_1950"] = new()
        {
            Cmd("red", "彭德怀（志愿军司令员兼政委）", 0.85),
            Cmd("blue", "李奇微（美第8集团军司令官）", 0.75),
            Flg(("night_from", 19), ("night_to", 6)),
        },
        ["korea_c4_1951"] = new()
        {
            Cmd("red", "彭德怀（志愿军司令员兼政委）", 0.82),
            Cmd("blue", "李奇微（美第8集团军司令官·霹雳/屠夫/撕裂者行动）", 0.78),
        },
        ["korea_c5_1951"] = new()
        {
            Cmd("red", "彭德怀（志愿军司令员兼政委）", 0.85),
            Cmd("blue", "范弗里特（美第8集团军司令官）", 0.78),
        },
        ["hanjiang_1951"] = new()
        {
            Align(),
            Cmd("red", "曾泽生（第50军军长）", 0.80),
            Cmd("blue", "李奇微（美第8集团军司令官）", 0.75),
            Flg(("night", true), ("night_from", 18), ("night_to", 6)),
            Sched((1,6,"snow"), (7,9,"overcast"), (10,14,"clear")),
            Mv("red", "hq", new[]{12,14}),
            Spl("red", "light_inf", new[]{ new[]{10,14}, new[]{14,15} }),
            Mv("red", "rifle", new[]{12,13}), Mv("red", "arty", new[]{10,13}),
            Mv("red", "engineer", new[]{12,12}), Mv("red", "signals", new[]{10,12}),
            Mv("red", "recy", new[]{9,13}),
            Mv("blue", "mec_inf", new[]{20,10}), Mv("blue", "tank", new[]{21,11}),
            Mv("blue", "gun", new[]{21,12}), Mv("blue", "arty", new[]{20,13}),
            Mv("blue", "aa", new[]{20,9}), Mv("blue", "radar", new[]{21,10}),
        },
        ["tieyuan_1951"] = new()
        {
            Align(),
            Cmd("red", "傅崇碧（第63军军长）", 0.82),
            Cmd("blue", "范弗里特（美第8集团军司令官）", 0.75),
            Mv("red", "hq", new[]{8,10}),
            Spl("red", "light_inf", new[]{ new[]{9,10}, new[]{10,12} }),
            Mv("red", "rifle", new[]{10,13}), Mv("red", "arty", new[]{8,9}),
            Mv("red", "engineer", new[]{9,11}), Mv("red", "signals", new[]{8,11}),
            Mv("red", "recy", new[]{7,10}),
            Mv("blue", "mec_inf", new[]{19,10}), Mv("blue", "tank", new[]{21,9}),
            Mv("blue", "gun", new[]{21,10}), Mv("blue", "arty", new[]{20,8}),
            Mv("blue", "aa", new[]{20,10}), Mv("blue", "radar", new[]{21,11}),
        },
        ["shangganling_1952"] = new()
        {
            Align(),
            Cmd("red", "秦基伟（第15军军长）", 0.85),
            Cmd("blue", "范弗里特（美第8集团军司令官）", 0.72),
            Mv("red", "hq", new[]{10,9}),
            Spl("red", "light_inf", new[]{ new[]{11,9}, new[]{12,10} }),
            Mv("red", "rifle", new[]{10,10}), Mv("red", "arty", new[]{9,9}),
            Mv("red", "engineer", new[]{11,10}), Mv("red", "signals", new[]{9,10}),
            Mv("red", "recy", new[]{8,9}),
            Mv("blue", "mec_inf", new[]{18,9}), Mv("blue", "tank", new[]{19,8}),
            Mv("blue", "gun", new[]{19,10}), Mv("blue", "arty", new[]{18,10}),
            Mv("blue", "aa", new[]{18,8}), Mv("blue", "radar", new[]{19,11}),
        },
        // ===== 二战欧洲 =====
        ["rhine_corridor"] = new()
        {
            Cmd("red", "蒙哥马利（第21集团军群司令）", 0.90),
            Cmd("blue", "布拉斯科维茨（H集团军群司令）", 0.55),
            Desc("1945年3月，盟军第21集团军群自莱茵河西岸渡河突破（战利品行动+大学校队行动），东岸河防集群迟滞阻击，鲁尔合围收官。默认使用真实 GIS 六角格数据包；若无数据包则程序化地形降级。"),
        },
    };

    private static readonly HashSet<string> MecTypes = new() { "mec_inf", "tank", "radar", "recon_air" };

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("update-scenarios — 按 docs 修正批量更新 data/scenarios/*.json");
            Console.WriteLine("  --scen DIR   剧本目录（默认 <repo>/data/scenarios）");
            Console.WriteLine("  --dry-run     只打印将做的变更，不写盘");
            return 0;
        }
        if (a.Bool("log")) Log.SetFile(a.Get("log"));

        var scenDir = a.Get("scen") ?? UtilsPath.DataSub("scenarios");
        if (!Directory.Exists(scenDir))
        {
            Log.Error($"剧本目录不存在: {scenDir}");
            return 1;
        }
        var dryRun = a.Bool("dry-run");
        var changed = new Dictionary<string, List<string>>();

        foreach (var (fid, spec) in Updates)
        {
            var p = Path.Combine(scenDir, fid + ".json");
            if (!File.Exists(p))
            {
                Log.Warn($"跳过（无文件）: {fid}.json");
                continue;
            }
            var d = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
            var log = ApplyOps(d, spec);
            if (log.Count > 0)
            {
                if (!dryRun)
                    File.WriteAllText(p, d.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                changed[fid] = log;
            }
        }

        var total = changed.Values.Sum(v => v.Count);
        Log.Info($"更新剧本 {changed.Count} 个，共 {total} 项变更");
        foreach (var (fid, log) in changed)
        {
            Console.WriteLine($"### {fid}（{log.Count} 项）");
            foreach (var l in log) Console.WriteLine("  - " + l);
        }
        return 0;
    }

    private static List<string> ApplyOps(JsonObject d, List<Op> ops)
    {
        var log = new List<string>();
        foreach (var op in ops)
        {
            var s = op.Side;
            switch (op.Kind)
            {
                case OpKind.Cmd:
                    {
                        var oob = d["sides"]![s]!["oob"]!.AsArray();
                        var hq = oob.First(u => u!["type"]!.GetValue<string>() == "hq")!;
                        var c = new JsonObject { ["name"] = op.Name, ["quality"] = op.Q };
                        hq["commander"] = c;
                        log.Add($"{s}.hq 指挥官→{op.Name}@{op.Q}");
                        break;
                    }
                case OpKind.Move:
                    {
                        var oob = d["sides"]![s]!["oob"]!.AsArray();
                        var es = oob.Where(u => u!["type"]!.GetValue<string>() == op.Typ).ToArray();
                        var node = (JsonObject)es[op.Occ]!;
                        node["at"] = new JsonArray(op.At![0], op.At![1]);
                        log.Add($"{s}.{op.Typ}[{op.Occ}]→{op.At}");
                        break;
                    }
                case OpKind.Split:
                    {
                        var oob = d["sides"]![s]!["oob"]!.AsArray();
                        var idxs = Enumerable.Range(0, oob.Count)
                            .Where(i => oob[i]!["type"]!.GetValue<string>() == op.Typ).ToList();
                        if (idxs.Count == 0) throw new KeyNotFoundException($"split: {s} 无 {op.Typ} 条目");
                        if (idxs.Count == 1)
                        {
                            var idx = idxs[0];
                            var u = (JsonObject)oob[idx]!;
                            var n = op.Positions!.Length;
                            int baseQ = u["qty"]?.GetValue<int>() ?? 1;
                            int bq = baseQ / n;
                            int rem = baseQ - bq * (n - 1);
                            var fresh = new List<JsonNode>();
                            for (int k = 0; k < n; k++)
                            {
                                var e = (JsonNode)u.DeepClone()!;
                                e["qty"] = k == n - 1 ? rem : bq;
                                e["at"] = new JsonArray(op.Positions[k][0], op.Positions[k][1]);
                                fresh.Add(e);
                            }
                            oob.RemoveAt(idx);
                            foreach (var f in fresh) oob.Insert(idx, f);
                        }
                        else
                        {
                            for (int k = 0; k < op.Positions!.Length && k < idxs.Count; k++)
                                ((JsonObject)oob[idxs[k]]!)["at"] = new JsonArray(op.Positions[k][0], op.Positions[k][1]);
                        }
                        log.Add($"{s}.{op.Typ} 分组→{string.Join(";", op.Positions!.Select(p => string.Join(",", p)))}");
                        break;
                    }
                case OpKind.Align:
                    {
                        var ls = op.LightSide!;
                        var bs = ls == "red" ? "blue" : "red";
                        bool IsMec(JsonArray oob) => oob.Any(u => MecTypes.Contains(u!["type"]!.GetValue<string>()));
                        var lightOob = (JsonArray)d["sides"]![ls]!["oob"]!;
                        var mecOob = (JsonArray)d["sides"]![bs]!["oob"]!;
                        if (IsMec(lightOob) && !IsMec(mecOob))
                        {
                            d["sides"]![ls]!["oob"] = mecOob;
                            d["sides"]![bs]!["oob"] = lightOob;
                            log.Add($"OOB体系归位：{ls}=轻步兵体（原机械化体对调回 {bs}）");
                        }
                        else if (IsMec(lightOob) && IsMec(mecOob))
                        {
                            throw new InvalidOperationException("align_bodies: 两侧都含机械化条目，无法判别");
                        }
                        else
                        {
                            log.Add("OOB体系归位：已是目标状态，跳过");
                        }
                        break;
                    }
                case OpKind.Flags:
                    {
                        var flags = d["flags"] as JsonObject ?? new JsonObject();
                        d["flags"] = flags;
                        foreach (var (k, v) in op.Kw!)
                            flags[k] = JsonValue.Create(v);
                        log.Add($"flags.update({string.Join(",", op.Kw.Select(kv => $"{kv.Key}={kv.Value}"))})");
                        break;
                    }
                case OpKind.Schedule:
                    {
                        var weather = d["weather"] as JsonObject ?? new JsonObject();
                        d["weather"] = weather;
                        var arr = new JsonArray();
                        foreach (var (f, t, w) in op.Segs!)
                            arr.Add(new JsonObject { ["from"] = f, ["to"] = t, ["weather"] = w });
                        weather["schedule"] = arr;
                        log.Add("weather.schedule 已按文档建议写入");
                        break;
                    }
                case OpKind.Victory:
                    {
                        var vic = d["victory"]?.AsArray();
                        if (vic != null)
                            foreach (var v in vic)
                                if (v!["side"]!.GetValue<string>() == op.Side &&
                                    v!["kind"]!.GetValue<string>() == "capture" &&
                                    v!["target"]!.GetValue<string>() == op.OldVictory)
                                    v!["target"] = op.NewVictory;
                        log.Add($"victory {op.Side} capture {op.OldVictory}→{op.NewVictory}");
                        break;
                    }
                case OpKind.Desc:
                    {
                        d["description"] = op.Text;
                        log.Add("description 已更新");
                        break;
                    }
            }
        }
        return log;
    }
}
