using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;

namespace HexFront.Api.Services;

/// <summary>
/// Runtime manager for multiplayer rooms served over the raw /ws endpoint.
/// Protocol matches web/js/ws.js: server messages welcome|state|events|saved|frames|error|kicked|pong,
/// client messages orders|ready|resolve|save|frames|state|director_cmd|ping.
/// </summary>
public sealed class RoomService
{
    private static readonly string[] Roles = { "director", "red", "blue", "spectator" };
    private static readonly string[] Sides = { "red", "blue" };

    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ILogger<RoomService> _logger;
    private readonly string _scenarioDir;
    private readonly string _saveDir;
    private readonly string _gisDir;

    public RoomService(ILogger<RoomService> logger)
    {
        _logger = logger;
        var root = GetRepoRoot();
        _scenarioDir = Environment.GetEnvironmentVariable("HF_SCENARIO_DIR")
            ?? Path.Combine(root, "data", "scenarios");
        _saveDir = Environment.GetEnvironmentVariable("HF_SAVE_DIR")
            ?? Path.Combine(root, "saves");
        _gisDir = Path.Combine(root, "data", "gis");
    }

    // ==================== Public API for REST controller ====================

    public string ScenarioDirectory => _scenarioDir;
    public string SaveDirectory => _saveDir;
    public string GisDirectory => _gisDir;

    /// <summary>Create a room from an existing game state (REST instance creation / save resume).</summary>
    public string CreateRoomFromState(GameState game, string? instanceName = null, bool aiRed = false, bool aiBlue = false)
    {
        string code;
        do
        {
            code = RandomCode();
        }
        while (_rooms.ContainsKey(code));

        var room = new Room
        {
            Code = code,
            Game = game,
            InstanceName = instanceName,
            AiRed = aiRed,
            AiBlue = aiBlue,
            Drafts = new Dictionary<string, List<Command>> { ["red"] = new(), ["blue"] = new() },
            Ready = new Dictionary<string, bool> { ["red"] = false, ["blue"] = false },
        };

        // Resumed mid-game state gets an initial replay frame, matching Dart saves/load behavior.
        if (game.Turn > 1)
        {
            lock (room.GameLock)
            {
                room.Frames.Add(new Dictionary<string, object?>
                {
                    ["turn"] = game.Turn - 1,
                    ["hour"] = game.HourOfDay,
                    ["weather"] = game.Weather,
                    ["director"] = FogService.DirectorView(game),
                    ["events"] = EventsToMaps(game.Events.TakeLast(200).ToList()),
                });
            }
        }

        _rooms[code] = room;
        _logger.LogInformation("room {Code} opened for instance {Scenario} turn {Turn}", code, game.ScenarioId, game.Turn);
        return code;
    }

    /// <summary>Public room listing for lobby (matches Dart publicInfo() shape).</summary>
    public Dictionary<string, object?>? PublicInfo(string code)
    {
        if (!TryFindRoom(code, out var room)) return null;
        lock (room.GameLock)
        {
            var g = room.Game;
            bool Taken(string role) => room.Slots.TryGetValue(role, out var c) && c.IsOpen;
            return new Dictionary<string, object?>
            {
                ["code"] = room.Code,
                ["instance"] = g.InstanceId,
                ["name"] = room.InstanceName ?? (string.IsNullOrEmpty(g.Name) ? g.ScenarioName : g.Name),
                ["scenario"] = g.ScenarioId,
                ["scenario_name"] = g.ScenarioName,
                ["turn"] = g.Turn,
                ["max_turns"] = g.MaxTurns,
                ["weather"] = g.Weather,
                ["over"] = g.Over,
                ["winner"] = g.Winner,
                ["roles"] = new Dictionary<string, object?>
                {
                    ["director"] = Taken("director") ? 1 : 0,
                    ["red"] = Taken("red") ? 1 : 0,
                    ["blue"] = Taken("blue") ? 1 : 0,
                    ["spectator"] = Taken("spectator") ? 1 : 0,
                },
                ["side_taken"] = new Dictionary<string, object?>
                {
                    ["red"] = Taken("red"),
                    ["blue"] = Taken("blue"),
                },
                ["ai"] = new Dictionary<string, object?> { ["red"] = room.AiRed, ["blue"] = room.AiBlue },
                ["created_at"] = room.CreatedAt.ToString("o"),
                ["fog"] = FlagBool(g, "fog", true),
                ["width"] = g.Width,
                ["height"] = g.Height,
            };
        }
    }

    public List<Dictionary<string, object?>> ListRooms() =>
        _rooms.Keys.Select(k => PublicInfo(k)).Where(x => x != null).Select(x => x!).ToList();

    public List<Dictionary<string, object?>>? Frames(string code)
    {
        if (!TryFindRoom(code, out var room)) return null;
        lock (room.GameLock)
        {
            return room.Frames.ToList();
        }
    }

    private bool TryFindRoom(string code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Room? room) =>
        _rooms.TryGetValue(code.ToUpperInvariant(), out room);

    private static string RandomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    // ==================== Connection entry point ====================

    /// <summary>Handles an incoming /ws request: handshake, role takeover, message loop.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var code = query["room"].ToString().ToUpperInvariant();
        var role = query["role"].ToString();
        if (string.IsNullOrEmpty(role)) role = "spectator";
        var token = query["token"].ToString();
        var name = query["name"].ToString();
        var force = query["force"].ToString() == "1";
        var scenarioId = query["scenario"].ToString();
        var saveId = query["save"].ToString();

        if (string.IsNullOrEmpty(code) || !Roles.Contains(role))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("room and role query parameters are required");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("websocket request expected");
            return;
        }

        Room room;
        try
        {
            room = _rooms.GetOrAdd(code, c => CreateRoom(c, scenarioId, saveId));
            if (room.Game == null)
                throw new InvalidOperationException("房间状态无效");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "room {Code} init failed", code);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("room init failed: " + ex.Message);
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var conn = new WsConn
        {
            Socket = socket,
            Role = role,
            Name = string.IsNullOrEmpty(name) ? null : name,
            Token = Guid.NewGuid().ToString("N"),
        };

        // Role takeover: same token (reconnect) or force=1 replaces the current occupant.
        WsConn? kickedOld = null;
        lock (room.GameLock)
        {
            if (room.Slots.TryGetValue(role, out var old) && !ReferenceEquals(old, conn) && old.IsOpen)
            {
                if (force || (!string.IsNullOrEmpty(token) && token == old.Token))
                {
                    kickedOld = old;
                }
                else
                {
                    conn.PendingError = "该角色位已被占用（加 force=1 可接管）";
                }
            }
            if (kickedOld != null || conn.PendingError == null)
            {
                room.Slots[role] = conn;
            }
        }

        if (conn.PendingError != null)
        {
            await conn.SendAsync(new Dictionary<string, object?> { ["type"] = "error", ["message"] = conn.PendingError });
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation, "role occupied");
            return;
        }

        if (kickedOld != null)
        {
            await kickedOld.SendAsync(new Dictionary<string, object?>
            {
                ["type"] = "kicked",
                ["reason"] = "本角色已在其他终端打开",
            });
            await CloseQuietly(kickedOld.Socket, WebSocketCloseStatus.NormalClosure, "kicked");
        }

        _logger.LogInformation("room {Code}: {Role} connected ({Name})", code, role, conn.Name ?? "-");

        await conn.SendAsync(new Dictionary<string, object?>
        {
            ["type"] = "welcome",
            ["token"] = conn.Token,
            ["room"] = room.Code,
            ["role"] = role,
        });
        await conn.SendAsync(BuildState(room, role));

        await ReceiveLoop(room, conn, context.RequestAborted);

        // Deregister only if our slot was not taken over by a newer connection.
        if (room.Slots.TryGetValue(role, out var cur) && ReferenceEquals(cur, conn))
        {
            room.Slots.TryRemove(role, out _);
        }
        _logger.LogInformation("room {Code}: {Role} disconnected", code, role);
    }

    private async Task ReceiveLoop(Room room, WsConn conn, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (conn.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                try
                {
                    result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                }
                catch (WebSocketException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseQuietly(conn.Socket, WebSocketCloseStatus.NormalClosure, null);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 4 * 1024 * 1024)
                {
                    await conn.SendAsync(ErrorPayload("消息过大，已丢弃"));
                    return;
                }
            }
            while (!result.EndOfMessage);

            string text;
            try { text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length); }
            catch (DecoderFallbackException) { continue; }
            if (string.IsNullOrWhiteSpace(text)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(text); }
            catch (JsonException)
            {
                await conn.SendAsync(ErrorPayload("无效 JSON"));
                continue;
            }

            await Dispatch(room, conn, root);
        }
    }

    // ==================== Message dispatch ====================

    private async Task Dispatch(Room room, WsConn conn, JsonElement root)
    {
        var type = GetStr(root, "type") ?? "";
        switch (type)
        {
            case "ping":
                await conn.SendAsync(new Dictionary<string, object?> { ["type"] = "pong" });
                break;

            case "orders":
                await HandleOrders(room, conn, root);
                break;

            case "clear_orders":
                await HandleClearOrders(room, conn);
                break;

            case "ready":
                await HandleReady(room, conn, root);
                break;

            case "resolve":
                await HandleResolve(room, conn);
                break;

            case "save":
                await HandleSave(room, conn, root);
                break;

            case "frames":
                await conn.SendAsync(new Dictionary<string, object?>
                {
                    ["type"] = "frames",
                    ["frames"] = room.Frames.ToList(),
                });
                break;

            case "state":
                await conn.SendAsync(BuildState(room, conn.Role));
                break;

            case "director_cmd":
                await HandleDirectorCmd(room, conn, root);
                break;

            default:
                await conn.SendAsync(ErrorPayload($"未知消息类型: {type}"));
                break;
        }
    }

    private async Task HandleOrders(Room room, WsConn conn, JsonElement root)
    {
        // room.dart submitOrders：仅对抗方，角色即阵营，不接受代发
        if (conn.Role is not ("red" or "blue"))
        {
            await conn.SendAsync(ErrorPayload("只有对抗方可以提交指令"));
            return;
        }
        var side = conn.Role;

        bool over;
        lock (room.GameLock) over = room.Game.Over;
        if (over)
        {
            await conn.SendAsync(ErrorPayload("推演已结束"));
            return;
        }

        // Dart: msg['replace'] != false —— 缺省即替换
        var replace = GetBool(root, "replace", true);
        var cmds = new List<Command>();
        if (root.TryGetProperty("commands", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var cmd = ParseCommand(el);
                if (cmd != null) cmds.Add(cmd);
            }
        }

        lock (room.GameLock)
        {
            var list = room.Drafts[side];
            if (replace) list.Clear();
            list.AddRange(cmds);
            var draft = list.Select(c => c.Clone()).ToList();
            var errors = CommandValidator.ValidateOrders(room.Game, side, draft);
            // Dart submitOrders：新指令到达即撤销本方准备态
            room.Ready[side] = false;
            room.LastErrors[side] = errors.Select(e => new Dictionary<string, object?>
            {
                ["unit"] = e.UnitId,
                ["reason"] = e.Reason,
                ["message"] = e.Message,
            }).ToList();
        }

        await BroadcastAll(room);
    }

    private async Task HandleClearOrders(Room room, WsConn conn)
    {
        if (conn.Role is not ("red" or "blue"))
        {
            await conn.SendAsync(ErrorPayload("无权操作"));
            return;
        }
        lock (room.GameLock)
        {
            room.Drafts[conn.Role].Clear();
            room.Ready[conn.Role] = false;
        }
        await BroadcastAll(room);
    }

    private async Task HandleReady(Room room, WsConn conn, JsonElement root)
    {
        if (conn.Role is not ("red" or "blue"))
        {
            await conn.SendAsync(ErrorPayload("无权操作"));
            return;
        }
        var value = GetBool(root, "value", true);
        string? reject = null;
        bool bothReady;
        lock (room.GameLock)
        {
            // room.dart setReady：置就绪前先校验草案，有非法指令则报错并保持未就绪
            if (value)
            {
                var errs = CommandValidator.ValidateOrders(
                    room.Game, conn.Role, room.Drafts[conn.Role].Select(c => c.Clone()).ToList());
                room.LastErrors[conn.Role] = errs.Select(e => new Dictionary<string, object?>
                {
                    ["unit"] = e.UnitId,
                    ["reason"] = e.Reason,
                    ["message"] = e.Message,
                }).ToList();
                if (errs.Count > 0)
                    reject = $"存在 {errs.Count} 条非法指令，已剔除后需重新确认";
                else
                    room.Ready[conn.Role] = true;
            }
            else
            {
                room.Ready[conn.Role] = false;
            }
            bothReady = !room.Game.Over && SideReadyLocked(room, "red") && SideReadyLocked(room, "blue");
        }

        if (reject != null)
        {
            await conn.SendAsync(ErrorPayload(reject));
            return;
        }
        await BroadcastAll(room);
        // AI 代管方视为已就绪，否则人类玩家点就绪后会永久等待
        if (bothReady) ScheduleAutoResolve(room, "auto");
    }

    private async Task HandleResolve(Room room, WsConn conn)
    {
        if (conn.Role != "director")
        {
            await conn.SendAsync(ErrorPayload("只有导演部可以强制结算"));
            return;
        }
        await ResolveTurnAsync(room, "director");
    }

    /// <summary>AI 代管方按本方视图补全未下令单位（须在 GameLock 内调用）。</summary>
    private void AiFillLocked(Room room, string side)
    {
        var cmds = AiService.GenerateOrders(room.Game, side);
        var have = room.Drafts[side].Select(c => c.UnitId).ToHashSet();
        room.Drafts[side].AddRange(cmds.Where(c => !have.Contains(c.UnitId)));
    }

    private static bool SideReadyLocked(Room room, string side) =>
        room.Ready[side] || (side == "red" ? room.AiRed : room.AiBlue);

    /// <summary>就绪屏障：双方（含 AI 代管）就绪后延时 300ms 自动结算，重复调度合并。</summary>
    private void ScheduleAutoResolve(Room room, string byRole)
    {
        if (Interlocked.CompareExchange(ref room.AutoResolvePending, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            Interlocked.Exchange(ref room.AutoResolvePending, 0);
            bool go;
            lock (room.GameLock)
                go = !room.Game.Over && SideReadyLocked(room, "red") && SideReadyLocked(room, "blue");
            if (go) await ResolveTurnAsync(room, byRole);
        });
    }

    private async Task ResolveTurnAsync(Room room, string byRole = "director")
    {
        TurnResolver.TurnReport report;
        List<Dictionary<string, object?>> rejectedAll;
        var rejectedAllList = new List<Dictionary<string, object?>>();
        lock (room.GameLock)
        {
            if (room.Game.Over) return;

            // 结算前 AI 副官按本方迷雾视图补全未下令单位
            if (room.AiRed) AiFillLocked(room, "red");
            if (room.AiBlue) AiFillLocked(room, "blue");

            var orders = new Dictionary<string, List<Command>>
            {
                ["red"] = room.Drafts["red"].Select(c => c.Clone()).ToList(),
                ["blue"] = room.Drafts["blue"].Select(c => c.Clone()).ToList(),
            };

            report = TurnResolver.ResolveTurn(room.Game, orders);
            room.Drafts["red"].Clear();
            room.Drafts["blue"].Clear();
            room.Ready["red"] = false;
            room.Ready["blue"] = false;

            // 被拒指令按归属方进入 last_errors（红方只能看到自己的错误）
            foreach (var s in Sides) room.LastErrors[s] = new List<Dictionary<string, object?>>();
            foreach (var e in report.Rejected)
            {
                var map = new Dictionary<string, object?>
                {
                    ["unit"] = e.UnitId,
                    ["reason"] = e.Reason,
                    ["message"] = e.Message,
                };
                rejectedAllList.Add(map);
                var side = room.Game.FindUnit(e.UnitId)?.Side;
                if (side is "red" or "blue") room.LastErrors[side].Add(map);
            }

            room.Frames.Add(new Dictionary<string, object?>
            {
                ["turn"] = room.Game.Turn - 1,
                ["hour"] = room.Game.HourOfDay,
                ["weather"] = room.Game.Weather,
                ["director"] = FogService.DirectorView(room.Game),
                ["events"] = EventsToMaps(report.Events),
                ["battles"] = report.Battles.Select(b => new Dictionary<string, object?>
                {
                    ["turn"] = b.Turn,
                    ["kind"] = b.Kind,
                    ["attacker"] = b.Attacker,
                    ["defender"] = b.Defender,
                    ["losses"] = b.Losses,
                }).ToList(),
            });
        }
        rejectedAll = rejectedAllList;

        foreach (var c in room.Slots.Values.ToArray())
        {
            if (!c.IsOpen) continue;
            var visible = VisibleEvents(room, c.Role, report.Events).ToList();
            var rejected = c.Role switch
            {
                "director" => rejectedAll,
                "spectator" => new List<Dictionary<string, object?>>(),
                _ => room.LastErrors.TryGetValue(c.Role, out var errs) ? errs : new List<Dictionary<string, object?>>(),
            };
            if (visible.Count > 0 || rejected.Count > 0)
            {
                await c.SendAsync(new Dictionary<string, object?>
                {
                    ["type"] = "events",
                    ["events"] = EventsToMaps(visible),
                    ["rejected"] = rejected,
                });
            }
            await c.SendAsync(BuildState(room, c.Role));
        }
    }

    private async Task HandleSave(Room room, WsConn conn, JsonElement root)
    {
        string name = GetStr(root, "name") ?? $"room {room.Code}";
        string? desc = GetStr(root, "description");
        try
        {
            StoreService.SaveMetadata meta;
            lock (room.GameLock)
            {
                meta = StoreService.SaveGame(room.Game, name, desc, _saveDir);
            }
            await BroadcastAll(room, new Dictionary<string, object?>
            {
                ["type"] = "saved",
                ["turn"] = meta.Turn,
                ["instance"] = meta.Id, // 与存档列表的 instance 同为可载入的 12 位 id
                ["meta"] = new Dictionary<string, object?>
                {
                    ["id"] = meta.Id,
                    ["name"] = meta.Name,
                    ["description"] = meta.Description,
                    ["saved_at"] = new DateTimeOffset(meta.SavedAt).ToUnixTimeMilliseconds(),
                    ["turn"] = meta.Turn,
                    ["weather"] = meta.Weather,
                    ["unit_count"] = meta.UnitCount,
                    ["file_size"] = meta.FileSize,
                },
            });
        }
        catch (Exception ex)
        {
            await conn.SendAsync(ErrorPayload("存档失败: " + ex.Message));
        }
    }

    private async Task HandleDirectorCmd(Room room, WsConn conn, JsonElement root)
    {
        if (conn.Role != "director")
        {
            await conn.SendAsync(ErrorPayload("只有导演部可以导调"));
            return;
        }
        var action = GetStr(root, "action") ?? "";
        var newEvents = new List<WarEvent>();
        bool recheckBarrier = false;

        switch (action)
        {
            case "weather":
            {
                // web 客户端发 { action:'weather', id }；兼容 value
                var id = GetStr(root, "id") ?? GetStr(root, "value");
                if (string.IsNullOrEmpty(id) || !room.Game.Weathers.ContainsKey(id))
                {
                    await conn.SendAsync(ErrorPayload("未知天气"));
                    return;
                }
                lock (room.GameLock)
                {
                    room.Game.Weather = id;
                    room.Game.WeatherMode = "director";
                    newEvents.Add(EmitLocked(room, "weather_set",
                        $"导演部将天气设为 {room.Game.WeatherOf().Label}",
                        new List<string> { "red", "blue", "spectator" }));
                    FogService.UpdateObservation(room.Game);
                }
                break;
            }
            case "fog":
            {
                var on = GetBool(root, "on", false);
                lock (room.GameLock)
                {
                    room.Game.Flags["fog"] = on;
                    FogService.UpdateObservation(room.Game);
                    newEvents.Add(EmitLocked(room, "fog",
                        $"导演部{(on ? "开启" : "关闭")}战争迷雾",
                        new List<string> { "director" }));
                }
                break;
            }
            case "spectator_omniscient":
            {
                var on = GetBool(root, "on", false);
                lock (room.GameLock) { room.Game.Flags["spectator_omniscient"] = on; }
                break;
            }
            case "ai":
            {
                var side = GetStr(root, "side");
                if (side is not ("red" or "blue"))
                {
                    await conn.SendAsync(ErrorPayload("参数错误"));
                    return;
                }
                var on = GetBool(root, "on", false);
                lock (room.GameLock)
                {
                    if (side == "red") room.AiRed = on; else room.AiBlue = on;
                    // 导调开启代管后立即检查屏障，避免人类玩家还要再点一次就绪
                    recheckBarrier = !room.Game.Over
                        && SideReadyLocked(room, "red") && SideReadyLocked(room, "blue");
                }
                break;
            }
            case "inject_intel":
            {
                var side = GetStr(root, "side");
                var unitId = GetStr(root, "unit");
                string? text = null;
                lock (room.GameLock)
                {
                    var target = unitId == null ? null : room.Game.FindUnit(unitId);
                    if (side is not ("red" or "blue") || target == null)
                    {
                        break; // → 参数错误
                    }
                    if (!room.Game.Tracks.TryGetValue(side, out var tracks))
                    {
                        tracks = new Dictionary<string, Track>();
                        room.Game.Tracks[side] = tracks;
                    }
                    tracks[target.Id] = new Track
                    {
                        UnitId = target.Id,
                        Level = 3,
                        LastSeen = target.Pos,
                        LastSeenTurn = room.Game.Turn,
                    };
                    text = $"导演部向{(side == "red" ? "红方" : "蓝方")}通报敌情：{target.Id}";
                    newEvents.Add(EmitLocked(room, "intel", text, new List<string> { side }));
                }
                if (text == null)
                {
                    await conn.SendAsync(ErrorPayload("参数错误"));
                    return;
                }
                break;
            }
            case "jump_turn":
            {
                var t = GetInt(root, "turn", 0);
                if (t < 1 || t > room.Game.Turn)
                {
                    await conn.SendAsync(ErrorPayload("只能回跳到已推演回合（回放由前端帧完成）"));
                    return;
                }
                // 与 Dart 一致：合法即 no-op，前端用 frames 回放
                break;
            }
            default:
                await conn.SendAsync(ErrorPayload($"未知导调动作 {action}"));
                return;
        }

        if (newEvents.Count > 0)
        {
            foreach (var c in room.Slots.Values.ToArray())
            {
                if (!c.IsOpen) continue;
                var visible = VisibleEvents(room, c.Role, newEvents).ToList();
                if (visible.Count == 0) continue;
                await c.SendAsync(new Dictionary<string, object?>
                {
                    ["type"] = "events",
                    ["events"] = EventsToMaps(visible),
                });
            }
        }
        await BroadcastAll(room);
        if (recheckBarrier) ScheduleAutoResolve(room, "director-ai");
    }

    /// <summary>追加全局事件（须在 GameLock 内调用）。</summary>
    private static WarEvent EmitLocked(Room room, string code, string text, List<string> sides)
    {
        var e = new WarEvent
        {
            Turn = room.Game.Turn,
            Code = code,
            Text = text,
            Sides = sides,
        };
        room.Game.Events.Add(e);
        return e;
    }

    // ==================== State / views ====================

    private Dictionary<string, object?> BuildState(Room room, string role)
    {
        lock (room.GameLock)
        {
            var g = room.Game;
            var omniscient = FlagBool(g, "spectator_omniscient", false);
            Dictionary<string, object?> view = role switch
            {
                "director" => WithDirectorExtras(room, FogService.DirectorView(g)),
                "spectator" => omniscient
                    ? FogService.DirectorView(g)
                    : FogService.SpectatorView(g),
                _ => WithSideExtras(room, role, FogService.SideView(g, role)),
            };

            List<Dictionary<string, object?>>? draftMaps = null;
            if (role is "red" or "blue")
            {
                draftMaps = room.Drafts[role].Select(CommandToMap).ToList();
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "state",
                ["view"] = view,
                ["turn"] = g.Turn,
                ["over"] = g.Over,
                ["winner"] = g.Winner,
                ["scenario"] = g.ScenarioId,
                ["events"] = EventsToMaps(VisibleEvents(room, role, g.Events.TakeLast(200)).ToList()),
                ["pending"] = new Dictionary<string, object?>
                {
                    ["red"] = room.Drafts["red"].Count,
                    ["blue"] = room.Drafts["blue"].Count,
                },
                ["ready"] = new Dictionary<string, object?>
                {
                    ["red"] = room.Ready["red"],
                    ["blue"] = room.Ready["blue"],
                },
                ["draft"] = draftMaps,
            };
        }
    }

    /// <summary>导演部视图附加：双方已受领指令、准备态、被拒错误、AI 托管标记（对齐 room.dart viewFor）。</summary>
    private Dictionary<string, object?> WithDirectorExtras(Room room, Dictionary<string, object?> view)
    {
        view["orders"] = new Dictionary<string, object?>
        {
            ["red"] = room.Drafts["red"].Select(CommandToMap).ToList(),
            ["blue"] = room.Drafts["blue"].Select(CommandToMap).ToList(),
        };
        view["ready"] = new Dictionary<string, object?>
        {
            ["red"] = room.Ready["red"],
            ["blue"] = room.Ready["blue"],
        };
        // Dart 线上格式：导演部 last_errors 为 JSON 字符串列表（阵营视图 errors 才是对象）
        view["last_errors"] = new Dictionary<string, object?>
        {
            ["red"] = room.LastErrors["red"].Select(m => JsonSerializer.Serialize(m)).ToList(),
            ["blue"] = room.LastErrors["blue"].Select(m => JsonSerializer.Serialize(m)).ToList(),
        };
        view["ai"] = new Dictionary<string, object?>
        {
            ["red"] = room.AiRed,
            ["blue"] = room.AiBlue,
        };
        view["spectator_omniscient"] = FlagBool(room.Game, "spectator_omniscient", false);
        return view;
    }

    private Dictionary<string, object?> WithSideExtras(Room room, string side, Dictionary<string, object?> view)
    {
        view["draft"] = room.Drafts[side].Select(CommandToMap).ToList();
        view["ready"] = room.Ready[side];
        view["errors"] = room.LastErrors[side];
        view["ai_managed"] = side == "red" ? room.AiRed : room.AiBlue;
        return view;
    }

    /// <summary>事件按可见阵营过滤（导演部全收；观战按 spectator 标记或全知；红蓝仅本方相关）。</summary>
    private IEnumerable<WarEvent> VisibleEvents(Room room, string role, IEnumerable<WarEvent> events)
    {
        var omniscient = FlagBool(room.Game, "spectator_omniscient", false);
        return events.Where(e => role switch
        {
            "director" => true,
            "spectator" => omniscient || e.Sides.Contains("spectator"),
            _ => e.Sides.Contains(role),
        });
    }

    private async Task BroadcastAll(Room room, Dictionary<string, object?>? extra = null)
    {
        foreach (var c in room.Slots.Values.ToArray())
        {
            if (!c.IsOpen) continue;
            if (extra != null) await c.SendAsync(extra);
            await c.SendAsync(BuildState(room, c.Role));
        }
    }

    private async Task BroadcastStateToDirector(Room room)
    {
        if (room.Slots.TryGetValue("director", out var d) && d.IsOpen)
        {
            await d.SendAsync(BuildState(room, "director"));
        }
    }

    private static List<Dictionary<string, object?>> EventsToMaps(List<WarEvent> events) =>
        events.Select(e => new Dictionary<string, object?>
        {
            ["turn"] = e.Turn,
            ["code"] = e.Code,
            ["text"] = e.Text,
            ["sides"] = e.Sides,
        }).ToList();

    // ==================== Room creation ====================

    private Room CreateRoom(string code, string scenarioId, string saveId)
    {
        GameState game;
        if (!string.IsNullOrEmpty(saveId))
        {
            game = StoreService.LoadGame(saveId, _saveDir);
        }
        else
        {
            var scenarios = ScenarioService.ListScenarios(_scenarioDir);
            var info = string.IsNullOrEmpty(scenarioId)
                ? scenarios.FirstOrDefault()
                : scenarios.FirstOrDefault(s => s.Id == scenarioId) ?? scenarios.FirstOrDefault();
            if (info == null)
                throw new InvalidOperationException($"目录 {_scenarioDir} 中没有可用剧本");
            game = ScenarioService.CreateGameState(ScenarioService.LoadFromFile(info.Path), _gisDir);
        }

        _logger.LogInformation("room {Code} created from scenario {Scenario} turn {Turn}",
            code, game.ScenarioId, game.Turn);

        return new Room
        {
            Code = code,
            Game = game,
            Drafts = new Dictionary<string, List<Command>> { ["red"] = new(), ["blue"] = new() },
            Ready = new Dictionary<string, bool> { ["red"] = false, ["blue"] = false },
        };
    }

    // ==================== Parsing helpers ====================

    /// <summary>Lenient command parser: accepts web format (unit, path=[[q,r]]) and React format (unitId, path=[{q,r}]).</summary>
    private static Command? ParseCommand(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var opStr = GetStr(el, "op");
        CommandOp? op = opStr switch
        {
            "move" => CommandOp.Move,
            "attack" => CommandOp.Attack,
            "fire" => CommandOp.Fire,
            "dig" => CommandOp.Dig,
            "rest" => CommandOp.Rest,
            "air_mission" => CommandOp.AirMission,
            "halt" => CommandOp.Halt,
            _ => null,
        };
        if (op == null) return null;

        var path = new List<Hex>();
        if (el.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pathEl.EnumerateArray())
            {
                var h = ParseHex(p);
                if (h != null) path.Add(h.Value);
            }
        }

        Hex? zone = el.TryGetProperty("zone", out var zoneEl) &&
                    zoneEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? ParseHex(zoneEl)
            : null;

        return new Command
        {
            Op = op.Value,
            UnitId = GetStr(el, "unit") ?? GetStr(el, "unitId") ?? "",
            Path = path,
            Target = GetStr(el, "target"),
            Zone = zone,
            Mission = GetStr(el, "mission") ?? "none",
        };
    }

    private static Hex? ParseHex(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
        {
            var q = el[0]; var r = el[1];
            if (q.TryGetInt32(out var qq) && r.TryGetInt32(out var rr)) return new Hex(qq, rr);
            return null;
        }
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("q", out var qEl) && el.TryGetProperty("r", out var rEl) &&
            qEl.TryGetInt32(out var qv) && rEl.TryGetInt32(out var rv))
        {
            return new Hex(qv, rv);
        }
        return null;
    }

    private static Dictionary<string, object?> CommandToMap(Command c) => new()
    {
        ["op"] = c.Op switch
        {
            CommandOp.Move => "move",
            CommandOp.Attack => "attack",
            CommandOp.Fire => "fire",
            CommandOp.Dig => "dig",
            CommandOp.Rest => "rest",
            CommandOp.AirMission => "air_mission",
            _ => "halt",
        },
        ["unit"] = c.UnitId,
        ["path"] = c.Path.Select(h => new int[] { h.Q, h.R }).ToList(),
        ["target"] = c.Target,
        ["zone"] = c.Zone != null ? new int[] { c.Zone.Value.Q, c.Zone.Value.R } : null,
        ["mission"] = c.Mission,
    };

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement el, string name, bool fallback)
    {
        if (!el.TryGetProperty(name, out var p)) return fallback;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int GetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;

    private static bool FlagBool(GameState g, string key, bool fallback) =>
        g.Flags.GetValueOrDefault(key) switch
        {
            bool b => b,
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            _ => fallback,
        };

    private static Dictionary<string, object?> ErrorPayload(string message) => new()
    {
        ["type"] = "error",
        ["message"] = message,
    };

    private static async Task CloseQuietly(WebSocket socket, WebSocketCloseStatus status, string? reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, reason ?? "", cts.Token);
            }
        }
        catch
        {
            socket.Abort();
        }
    }

    private static string GetRepoRoot()
    {
        var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var binDir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(binDir)) return Directory.GetCurrentDirectory();
        // bin/Debug/net10.0 → HexFront.Api → src → HexFront.CSharp → wargame
        return Path.GetFullPath(Path.Combine(binDir, "..", "..", "..", "..", "..", ".."));
    }

    // ==================== Internal types ====================

    private sealed class Room
    {
        public string Code { get; init; } = "";
        public GameState Game { get; init; } = null!;
        public readonly object GameLock = new();
        public ConcurrentDictionary<string, WsConn> Slots { get; init; } = new(); // role → conn
        public Dictionary<string, List<Command>> Drafts { get; init; } = new();
        public Dictionary<string, bool> Ready { get; init; } = new();
        public Dictionary<string, List<Dictionary<string, object?>>> LastErrors { get; init; } = new()
        {
            ["red"] = new(),
            ["blue"] = new(),
        };
        public List<Dictionary<string, object?>> Frames { get; init; } = new();
        public bool AiRed { get; set; }
        public bool AiBlue { get; set; }
        // Interlocked 防抖：0=空闲 1=已调度自动结算
        public int AutoResolvePending;
        public string? InstanceName { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    private sealed class WsConn
    {
        public WebSocket Socket { get; init; } = null!;
        public string Role { get; init; } = "";
        public string Token { get; init; } = "";
        public string? Name { get; init; }
        public string? PendingError { get; set; }
        private readonly SemaphoreSlim SendGate = new(1, 1);

        public bool IsOpen => Socket.State == WebSocketState.Open;

        public async Task SendAsync(object payload)
        {
            if (Socket.State != WebSocketState.Open) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await SendGate.WaitAsync();
            try
            {
                await Socket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException) {}
            catch (ObjectDisposedException) { }
            finally { SendGate.Release(); }
        }
    }
}
