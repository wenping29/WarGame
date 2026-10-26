using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using HexFront.Core;
using HexFront.Core.Models;
using HexFront.Core.Services;
using HexFront.Tools.Common;

namespace HexFront.Tools.Commands;

/// <summary>
/// hexgen 子命令：把 Python hexgen.py 的 CLI 封装到已有的 C# HexGenService 上。
/// C# 版目前内置：合成 DEM + 派生地形/路网/水系/城镇/铁路/电力/机场/加油站；
/// open-meteo / 真实 GIS 叠加为后续增强项（HexGenService.UseRealDem 已预留，网络失败时降级合成）。
/// </summary>
public static class HexgenCommand
{
    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("hexgen — 生成六角格地形包");
            Console.WriteLine("  --bbox W S E N      经纬度范围（度）");
            Console.WriteLine("  --cell-km FLOAT     每格地面边长 km（默认 5）");
            Console.WriteLine("  --seed INT          确定性种子（默认 42）");
            Console.WriteLine("  --name STR          输出包名（默认 hexgen）");
            Console.WriteLine("  --out PATH          输出 JSON（默认 <repo>/data/gis/<name>.json）");
            Console.WriteLine("  --features on|off   是否派生路网/铁路/电力/机场（默认 on）");
            Console.WriteLine("  --dem auto|local|open-meteo|synthetic  高程来源（默认 synthetic）");
            Console.WriteLine("  --offline           不联网（强制合成 DEM）");
            Console.WriteLine("  --print-meta        仅打印 meta 不写盘");
            return 0;
        }

        var bbox = a.GetList("bbox");
        if (bbox.Count != 4)
        {
            Log.Error("--bbox 需要 4 个值：W S E N");
            return 1;
        }
        var ci = CultureInfo.InvariantCulture;
        var b = new double[4]
        {
            double.Parse(bbox[0], ci), double.Parse(bbox[1], ci),
            double.Parse(bbox[2], ci), double.Parse(bbox[3], ci),
        };

        var name = a.Get("name", "hexgen");
        var cellKm = a.Dbl("cell-km", 5.0);
        var seed = a.Int("seed", 42);
        var features = a.Get("features", "on") != "off";
        var offline = a.Bool("offline");
        // C# HexGenService 暂未实现 open-meteo 真实抓取（会降级合成），按需开启
        var useRealDem = (a.Get("dem", "synthetic") == "open-meteo") && !offline;

        var spec = new GenSpec
        {
            Name = name,
            Bbox = b,
            CellKm = cellKm,
            Seed = seed,
            Offline = offline,
            UseRealDem = useRealDem,
            Features = features,
        };

        Log.Info($"生成 {name}：bbox={string.Join(",", b)} cellKm={cellKm} seed={seed} features={features}");
        var pkg = HexGenService.Generate(spec);
        Log.Info($"完成：{pkg.Hexes.Count} 格 / {pkg.Meta.Width}x{pkg.Meta.Height}");

        if (a.Has("print-meta"))
        {
            var metaJson = JsonSerializer.Serialize(pkg.Meta, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            Console.WriteLine(metaJson);
            return 0;
        }

        var outPath = a.Get("out") ?? RepoLocator.DataSub($"gis/{name}.json");
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
        var json = JsonSerializer.Serialize(pkg, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(outPath, json);
        Log.Info($"已写入 {outPath}");
        return 0;
    }
}
