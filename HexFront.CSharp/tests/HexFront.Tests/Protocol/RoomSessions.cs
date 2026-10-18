using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 一个房间 + 若干角色终端。协议用例都从这里开场：
/// 每个角色都要有真连接，才测得到「谁收到了什么」，而不是「服务端本来想发什么」。
/// </summary>
public sealed class RoomSessions : IAsyncDisposable
{
    private readonly ApiFixture _api;
    private readonly List<WsClient> _all = new();
    private readonly Dictionary<string, WsClient> _byRole = new(StringComparer.Ordinal);

    public string Code { get; }

    /// <summary>POST /api/instances 的原始返回（instance  id 要从这里取）。</summary>
    public JsonObject Created { get; }

    public IEnumerable<WsClient> Clients => _all;

    private RoomSessions(ApiFixture api, JsonObject created)
    {
        _api = api;
        Created = created;
        Code = created["code"]?.GetValue<string>()
            ?? throw new InvalidOperationException("建房失败：" + created.ToJsonString());
    }

    /// <summary>建一间新房并按角色接线；welcome 与紧随其后的 state 都到齐才算连上。</summary>
    public static async Task<RoomSessions> CreateAsync(ApiFixture api, string[] roles,
        bool aiBlue = false, int seed = 555, string? name = null)
    {
        var created = await api.CreateInstanceAsync(name ?? "协议冒烟", seed, aiBlue);
        var sessions = new RoomSessions(api, created);
        foreach (var role in roles) await sessions.OpenAsync(role);
        return sessions;
    }

    /// <summary>再挂一条连接：顶号、断线重连这类用例要的就是"第二个同角色终端"。</summary>
    public async Task<WsClient> OpenAsync(string role, string? token = null, bool force = false,
        string? name = null, bool awaitView = true)
    {
        var client = await WsClient.ConnectAsync(_api, Code, role, token, force, name);
        _all.Add(client);
        if (!_byRole.ContainsKey(role)) _byRole[role] = client;
        if (awaitView) await client.WaitForTypesAsync("welcome", "state");
        return client;
    }

    public WsClient this[string role] => _byRole.TryGetValue(role, out var client)
        ? client
        : throw new InvalidOperationException($"本用例没接 {role} 终端");

    public ValueTask DisposeAsync() =>
        new(Task.WhenAll(_all.Select(c => c.DisposeAsync().AsTask())));
}
