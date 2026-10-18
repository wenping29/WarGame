using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HexFront.Tests.Protocol;

/// <summary>
/// /ws 终端：与 web/js/ws.js、hexfront/bin/smoke_ws.dart 的 Client 同形 ——
/// 后台收包、按类型入队、waitFor 轮询条件。测试只描述「谁该看见什么」，不等时间的断言一律失败。
/// </summary>
public sealed class WsClient : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>服务端推来的每一条消息，按到达顺序。</summary>
    public ConcurrentQueue<JsonObject> Messages { get; } = new();

    public string Role { get; }

    public ClientWebSocket Socket { get; } = new();

    public JsonObject? Welcome { get; private set; }

    public JsonObject? LastSaved { get; private set; }

    public JsonObject? Kicked { get; private set; }

    public WsClient(string role) => Role = role;

    public static async Task<WsClient> ConnectAsync(ApiFixture api, string code, string role,
        string? token = null, bool force = false, string? name = null, int ms = 8000)
    {
        var query = $"room={code}&role={role}";
        if (!string.IsNullOrEmpty(token)) query += "&token=" + token;
        if (force) query += "&force=1";
        if (!string.IsNullOrEmpty(name)) query += "&name=" + name;

        var ws = new WsClient(role);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ws._cts.Token);
        timeout.CancelAfter(ms);
        try
        {
            await ws.Socket.ConnectAsync(new Uri(api.WsBaseUrl + "?" + query), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            ws.Dispose();
            throw new TimeoutException($"连接 /ws?{query} 在 {ms}ms 内没有建立（可能被拒）");
        }
        catch (WebSocketException ex)
        {
            ws.Dispose();
            throw new InvalidOperationException($"连接 /ws?{query} 失败：{ex.Message}", ex);
        }

        _ = Task.Run(ws.ReceiveLoop);
        return ws;
    }

    /// <summary>未知房间码这类「连不上」的分支：只报告成没成，不留半开的会话。</summary>
    public static async Task<bool> OpensAsync(ApiFixture api, string code, string role, int ms = 5000)
    {
        try
        {
            var ws = await ConnectAsync(api, code, role, ms: ms);
            await ws.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe", CancellationToken.None);
            ws.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[256 * 1024];
        using var ms = new System.IO.MemoryStream();
        try
        {
            while (Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var obj = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length))!.AsObject();
                Messages.Enqueue(obj);
                switch (obj["type"]?.GetValue<string>())
                {
                    case "welcome":
                        Welcome = obj;
                        break;
                    case "saved":
                        LastSaved = obj;
                        break;
                    case "kicked":
                        Kicked = obj;
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 连接被服务端关掉（顶号 / 角色位占用）是协议的一部分，交给断言去读 Messages
        }
    }

    public Task SendAsync(JsonObject message) =>
        Socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()),
            WebSocketMessageType.Text, true, _cts.Token);

    /// <summary>镜像 Dart Client.waitFor：轮询到条件成立，超时即返回 false 让断言报「没等到」。</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    public JsonObject[] OfType(string type) => Messages.Where(m => m["type"]?.GetValue<string>() == type).ToArray();

    public Task<bool> WaitForTypeAsync(string type, int count = 1, int ms = 8000) =>
        WaitUntilAsync(() => OfType(type).Length >= count, ms);

    public Task<bool> WaitForTypesAsync(params string[] types) =>
        WaitUntilAsync(() => types.All(t => OfType(t).Length >= 1), 8000);

    /// <summary>最后一包 state 的视图；welcome.view 也算，所以接入后立刻可读。</summary>
    public JsonObject? StateView => States.LastOrDefault()?["view"]?.AsObject();

    /// <summary>断言用视图：一包都没到就直接失败，别让 null 变成「字段缺失」的假线索。</summary>
    public JsonObject View => StateView
        ?? Welcome?["view"]?.AsObject()
        ?? throw new InvalidOperationException($"{Role} 端还没收到任何视图包");

    public JsonObject[] States => OfType("state");

    /// <summary>最后一包 state 顶层的 pending.&lt;side&gt;（草案条数，服务端对每个角色都带）。</summary>
    public int PendingOf(string side) =>
        States.LastOrDefault()?["pending"]?[side]?.GetValue<int>() ?? -1;

    /// <summary>视图里的 turn（welcome/state 都带）。</summary>
    public int Turn => NumberField(View, "turn") ?? -1;

    /// <summary>本方就绪态；导演部/观战视图里 ready 是 dict 不是布尔，返回 null。</summary>
    public bool? ReadyFlag => Role is "director" or "spectator" ? null : BoolOf(View, "ready");

    public static bool? BoolOf(JsonObject? node, string key) => node?[key] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        _ => null,
    };

    public List<string> Errors => OfType("error")
        .Select(m => m["message"]?.GetValue<string>() ?? "").ToList();

    /// <summary>收到的全部事件明细（服务端一包 events 里是数组，这里摊平成 Dart 冒烟的 events 列表）。</summary>
    public List<JsonObject> Events => OfType("events")
        .SelectMany(m => m["events"]?.AsArray() ?? new JsonArray())
        .Select(e => e!.AsObject())
        .ToList();

    public JsonObject[] OwnUnits => ArrayOf(View, "own");

    public JsonObject[] DirectorUnits => ArrayOf(View, "units");

    public static JsonObject[] ArrayOf(JsonObject? view, string key) =>
        view?[key] is JsonArray arr ? arr.Select(x => x!.AsObject()).ToArray() : Array.Empty<JsonObject>();

    /// <summary>标量数组字段（如回放帧 turn 列表、重放警告）——元素不是对象，不能用 ArrayOf。</summary>
    public static JsonNode?[] NodesOf(JsonObject? node, string key) =>
        node?[key] is JsonArray arr ? arr.Select(x => x).ToArray() : Array.Empty<JsonNode?>();

    /// <summary>字符串数组字段（如 /api/validate 的 errors）——元素不是对象，不能用 ArrayOf。</summary>
    public static string[] StringsOf(JsonObject? node, string key) =>
        NodesOf(node, key).Select(n => n?.GetValueKind() == JsonValueKind.String ? n!.GetValue<string>() : n?.ToJsonString() ?? "").ToArray();

    public static int? NumberField(JsonObject? view, string key)
    {
        var n = view?[key];
        if (n is null) return null;
        try { return n.GetValue<int>(); }
        catch (InvalidOperationException) { return (int)n.GetValue<double>(); }
    }

    public static string StrOf(JsonObject? node, string key) => node?[key]?.GetValue<string>() ?? "";

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        Socket.Dispose();
    }
}
