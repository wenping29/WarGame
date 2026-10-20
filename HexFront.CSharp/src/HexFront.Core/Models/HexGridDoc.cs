using System;
using System.Collections.Generic;

namespace HexFront.Core.Models;

/// <summary>
/// 服务器端六角格网络包的文档（zip 内 grid.json 的结构，snake_case 落盘）。
/// 剧本只保存 <c>hexgrid: "&lt;id&gt;"</c> 引用；几何与格数据全部在本包里，
/// 包文件位于 data/hexgrids/&lt;id&gt;.zip。
/// </summary>
public sealed class HexGridDoc
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Rows { get; set; }
    public int Cols { get; set; }
    /// <summary>对边距，Mercator 米（与六角格管理页口径一致）。</summary>
    public double FlatToFlat { get; set; }
    /// <summary>棋盘西南角锚点（WGS84 经度）。</summary>
    public double West { get; set; }
    /// <summary>棋盘西南角锚点（WGS84 纬度）。</summary>
    public double South { get; set; }
    /// <summary>未指定格的默认地形 id（LibraryService.STANDARD_TERRAIN 键）。</summary>
    public string DefaultTerrain { get; set; } = "clear";
    /// <summary>挖除的格键 "col,row"（引擎内落成 void，不入 Cells）。</summary>
    public List<string> Holes { get; set; } = new();
    /// <summary>格键 → 地形 id 覆盖。</summary>
    public Dictionary<string, string> Terrains { get; set; } = new();
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    /// <summary>来源：scenmgr（剧本管理页）/ gridmgr（六角格管理页）/ api。</summary>
    public string Source { get; set; } = "";
}
