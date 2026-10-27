namespace HexFront.Api.Services;

/// <summary>
/// 数据根目录配置（appsettings.json 的 "DataPath" 段）。
///
/// 采用「通用键 + 按系统特化键」三套：运行时优先取当前操作系统对应的特化键
/// （DataDirWindows / DataDirLinux），取不到再退回通用键（DataDir）；都为空则
/// 回落到 &lt;server&gt;/data（server 目录含 HexFront.Api 与 data）。
///
/// 约定与 HexFront.Core.UtilsPath 保持一致：路径内部统一归一
/// （展开 $ENV / %ENV% 与 ~、统一 / 分隔符），同一份 appsettings.json 即可在
/// Windows 与 Linux 之间通用，无需为不同系统维护两份文件。
///
/// 例：
///   Windows 数据根： "D:/data"  或  "D:\\data"
///   Linux   数据根： "/opt/hexfront/data"
///   相对路径（按 server 目录解析）： "data"
///   环境变量展开： "%PROGRAMDATA%/hexfront/data" 或 "$HOME/hexfront/data"
/// </summary>
public sealed class DataPathOptions
{
    /// <summary>数据根目录（通用键，任意系统生效）。其下含 scenarios/saves/gis/hexgrids。</summary>
    public string? DataDir { get; set; }

    /// <summary>仅 Windows 生效的数据根目录，优先级高于 <see cref="DataDir"/>。</summary>
    public string? DataDirWindows { get; set; }

    /// <summary>仅 Linux 生效的数据根目录，优先级高于 <see cref="DataDir"/>。</summary>
    public string? DataDirLinux { get; set; }
}
