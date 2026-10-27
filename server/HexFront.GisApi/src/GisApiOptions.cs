namespace HexFront.GisApi;

/// <summary>
/// 六角格生成 Web API 的配置。
///
/// 路径配置采用「通用键 + 按系统特化键」三套：运行时优先取当前操作系统对应的特化键
/// （TerrainDirWindows / TerrainDirLinux 等），取不到再退回通用键（TerrainDir 等）；
/// 都为空则按仓库根目录（含 data/ 的祖先目录）推导默认值。
///
/// 所有路径在内部统一归一（使用 / 分隔符、展开 ~ 为用户主目录、展开 $ENV / %ENV% 环境变量），
/// 因此同一份 appsettings.json 即可在 Windows 与 Linux 之间通用，无需为不同系统维护两份文件。
///
/// 例：
///   Windows 地形目录： "D:/data/terrain"  或  "D:\\data\\terrain"
///   Linux   地形目录： "/opt/hexfront/data/terrain"
///   相对路径（按仓库根解析）： "data/terrain"
///   环境变量展开： "%APPDATA%/hexfront/terrain" 或 "$HOME/hexfront/terrain"
/// </summary>
public sealed class GisApiOptions
{
    /// <summary>GIS 输入数据（.ter 地形瓦片目录，含 terrain_manifest.json）。通用键，任意系统生效。</summary>
    public string? TerrainDir { get; set; }

    /// <summary>仅 Windows 生效的地形目录，优先级高于 <see cref="TerrainDir"/>。</summary>
    public string? TerrainDirWindows { get; set; }

    /// <summary>仅 Linux 生效的地形目录，优先级高于 <see cref="TerrainDir"/>。</summary>
    public string? TerrainDirLinux { get; set; }

    /// <summary>生成数据（作业 jobs/ 与网格 grids/）存储根目录。通用键，任意系统生效。</summary>
    public string? DataDir { get; set; }

    /// <summary>仅 Windows 生效的存储根目录，优先级高于 <see cref="DataDir"/>。</summary>
    public string? DataDirWindows { get; set; }

    /// <summary>仅 Linux 生效的存储根目录，优先级高于 <see cref="DataDir"/>。</summary>
    public string? DataDirLinux { get; set; }

    /// <summary>
    /// 高程来源：
    /// auto   —— 瓦片可用即真实 GIS，否则合成（默认，最稳）；
    /// real   —— 强制真实 .ter（缺瓦片则作业失败）；
    /// synthetic —— 强制合成 DEM（无需任何 GIS 数据即可跑）。
    /// </summary>
    public string DemMode { get; set; } = "auto";
}
