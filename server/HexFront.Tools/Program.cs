using HexFront.Tools.Common;
using HexFront.Tools.Commands;

var all = new[]
{
    "hexgen", "extract-terrain", "rasters",
    "fetch-dem", "fetch-landcover", "fetch-water",
    "update-scenarios", "pack-release",
};

if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "?")
{
    Console.WriteLine("HexFront 工具集（C# 版，对应 tools/ 下的 Python 脚本）");
    Console.WriteLine("用法: hexfront-tools <command> [options]");
    Console.WriteLine();
    Console.WriteLine("子命令:");
    Console.WriteLine("  hexgen             生成六角格地形包（封装 HexGenService）");
    Console.WriteLine("  extract-terrain    从 GeoTIFF 提取 .ter 地形瓦片（无 GDAL）");
    Console.WriteLine("  rasters            真实栅格/矢量按六角格聚合取样");
    Console.WriteLine("  fetch-dem          下载全球 DEM（terrarium 瓦片 -> GeoTIFF）");
    Console.WriteLine("  fetch-landcover    装配全球土地覆盖（ESA WorldCover v200）");
    Console.WriteLine("  fetch-water        下载全球河流湖泊矢量（Natural Earth 10m -> gpkg）");
    Console.WriteLine("  update-scenarios   按 docs 修正批量更新 data/scenarios/*.json");
    Console.WriteLine("  pack-release       把前后端分离产物打成 ZIP64 发布包");
    Console.WriteLine();
    Console.WriteLine("每个子命令加 -h 查看参数。环境变量 HF_REPO_ROOT 可指定 server/ 根目录。");
    return 0;
}

var cmd = args[0];
var rest = args[1..];

if (rest is ["-h"] or ["--help"])
{
    ShowCommandHelp(cmd);
    return 0;
}

try
{
    return cmd switch
    {
        "hexgen" => HexgenCommand.Run(rest),
        "extract-terrain" => ExtractTerrainCommand.Run(rest),
        "rasters" => RastersCommand.Run(rest),
        "fetch-dem" => FetchDemCommand.Run(rest),
        "fetch-landcover" => FetchLandcoverCommand.Run(rest),
        "fetch-water" => FetchWaterCommand.Run(rest),
        "update-scenarios" => UpdateScenariosCommand.Run(rest),
        "pack-release" => PackReleaseCommand.Run(rest),
        _ => Fail($"未知子命令: {cmd}（可用: {string.Join(", ", all)}）"),
    };
}
catch (Exception ex)
{
    Log.Error($"{ex.Message}");
    Log.Error(ex.StackTrace ?? "");
    return 1;
}

int Fail(string msg)
{
    Log.Error(msg);
    return 1;
}

void ShowCommandHelp(string c)
{
    // 简单提示：把参数说明交给各命令自己打印
    var rc = c switch
    {
        "hexgen" => HexgenCommand.Run(new[] { "-h" }),
        "extract-terrain" => ExtractTerrainCommand.Run(new[] { "-h" }),
        "rasters" => RastersCommand.Run(new[] { "-h" }),
        "fetch-dem" => FetchDemCommand.Run(new[] { "-h" }),
        "fetch-landcover" => FetchLandcoverCommand.Run(new[] { "-h" }),
        "fetch-water" => FetchWaterCommand.Run(new[] { "-h" }),
        "update-scenarios" => UpdateScenariosCommand.Run(new[] { "-h" }),
        "pack-release" => PackReleaseCommand.Run(new[] { "-h" }),
        _ => Fail($"未知子命令: {c}"),
    };
    _ = rc;
}
