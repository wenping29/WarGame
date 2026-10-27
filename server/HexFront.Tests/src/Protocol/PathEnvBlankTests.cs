using System;
using HexFront.Core;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 路径类环境变量的「设了但留空」回归测试。
///
/// 早期 RoomService/AppDbService/GameController 直接写 GetEnvironmentVariable("HF_*") ?? 下一级，
/// 而部署面板里留空的字段会以空串传给进程：空串不是 null，会赢过 appsettings 与默认值，
/// 于是 data 根变成 ""，所有目录退化成相对 CWD 的 scenarios/、saves/（启动不报错，剧本直接看不见）。
/// EnvOrNull 把空白统一折叠成 null，让下一级配置照常生效。
/// </summary>
public sealed class PathEnvBlankTests
{
    /// <summary>每次用独立变量名，避免与并行执行的其他测试相互污染进程环境。</summary>
    private static string FreshName() => "HF_TEST_PATH_" + Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白环境变量视同未设置(string raw)
    {
        var name = FreshName();
        Environment.SetEnvironmentVariable(name, raw);
        try
        {
            Assert.Null(UtilsPath.EnvOrNull(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void 未设置的环境变量返回null()
    {
        Assert.Null(UtilsPath.EnvOrNull(FreshName()));
    }

    [Fact]
    public void 有效路径去空白后返回()
    {
        var name = FreshName();
        Environment.SetEnvironmentVariable(name, "  D:/hexfront/data  ");
        try
        {
            Assert.Equal("D:/hexfront/data", UtilsPath.EnvOrNull(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
