namespace HexFront.GisApi;

/// <summary>
/// 六角格生成 Web API 的配置。
/// DataDir / TerrainDir 留空时按仓库根目录（含 data/ 的祖先目录）推导默认值。
/// </summary>
public sealed class GisApiOptions
{
    /// <summary>独立存储根目录；为空则默认 &lt;repoRoot&gt;/data/gis-api（含 jobs/ 与 grids/）。</summary>
    public string? DataDir { get; set; }

    /// <summary>.ter 地形瓦片目录（含 terrain_manifest.json）；为空则默认 &lt;repoRoot&gt;/data/terrain。</summary>
    public string? TerrainDir { get; set; }

    /// <summary>
    /// 高程来源：
    /// auto   —— 瓦片可用即真实 GIS，否则合成（默认，最稳）；
    /// real   —— 强制真实 .ter（缺瓦片则作业失败）；
    /// synthetic —— 强制合成 DEM（无需任何 GIS 数据即可跑）。
    /// </summary>
    public string DemMode { get; set; } = "auto";
}
