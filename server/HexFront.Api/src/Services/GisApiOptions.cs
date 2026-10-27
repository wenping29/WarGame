namespace HexFront.Api.Services;

/// <summary>
/// 进程内六角格生成配置（appsettings.json 的 "GisApi" 段）。
///
/// 2026-10-27 起六角格生成已并入 HexFront.Api（不再有独立进程 HexFront.GisApi）。
/// 这一节只负责「真实地形从哪来 / 高程档位怎么定」，不再有 BaseUrl/Enabled：
/// 生成发生在本进程内，直接调用 Core 的 HexGenService.Generate（真实 .ter 瓦片或合成兜底）。
///
/// 路径采用「通用键 + 按系统特化键」三套：运行时优先取当前操作系统对应的特化键
/// （TerrainDirWindows / TerrainDirLinux），取不到再退回通用键（TerrainDir）；
/// 都为空则按仓库根目录（server 目录）推导默认 data/terrain。
///
/// 所有路径内部统一归一（展开 $ENV / %ENV% 与 ~、统一 / 分隔符），逻辑集中在
/// HexFront.Core.UtilsPath，同一份 appsettings.json 即可在 Windows 与 Linux 之间通用。
/// </summary>
public sealed class GisApiOptions
{
    /// <summary>GIS 输入数据（.ter 地形瓦片目录，含 terrain_manifest.json）。通用键，任意系统生效。</summary>
    public string? TerrainDir { get; set; }

    /// <summary>仅 Windows 生效的地形目录，优先级高于 <see cref="TerrainDir"/>。</summary>
    public string? TerrainDirWindows { get; set; }

    /// <summary>仅 Linux 生效的地形目录，优先级高于 <see cref="TerrainDir"/>。</summary>
    public string? TerrainDirLinux { get; set; }

    /// <summary>
    /// 高程来源：
    /// auto   —— 瓦片可用即真实 GIS，否则合成（默认，最稳）；
    /// real   —— 强制真实 .ter（缺瓦片则作业失败）；
    /// synthetic —— 强制合成 DEM（无需任何 GIS 数据即可跑）。
    /// </summary>
    public string DemMode { get; set; } = "auto";
}
