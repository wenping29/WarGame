using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §4 之外的导调补项：把 DirectorCmd 的发送收敛成一行，
/// 顺便保住「导调包只带 action + 参数」这个线上格式（前端 ws.js 就是这么发的）。
/// </summary>
internal static class DirectorCmdHelper
{
    public static Task SendAsync(this WsClient dir, string action, JsonObject? args = null)
    {
        var cmd = new JsonObject { ["type"] = "director_cmd", ["action"] = action };
        if (args != null) foreach (var (key, value) in args) cmd[key] = value?.DeepClone();
        return dir.SendAsync(cmd);
    }
}
