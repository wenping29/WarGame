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
        var side = GetStr(root, "side");
        if (side == null) side = conn.Role is "red" or "blue" ? conn.Role : null;
        if (side == null)
        {
            await conn.SendAsync(ErrorPayload("只有红/蓝方可以提交命令"));
            return;
        }
        if (conn.Role is "red" or "blue" && side != conn.Role)
        {
            await conn.SendAsync(ErrorPayload("不能替对方提交命令"));
            return;
        }

        var replace = GetBool(root, "replace", false);
        var cmds = new List<Command>();
        if (root.TryGetProperty("commands", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var cmd = ParseCommand(el);
                if (cmd != null) cmds.Add(cmd);
            }
        }

        List<CommandError> errors;
        List<Command> draft;
        lock (room.GameLock)
        {
            var list = room.Drafts[side];
            if (replace) list.Clear();
            list.AddRange(cmds);
            draft = list.Select(c => c.Clone()).ToList();
            errors = CommandValidator.ValidateOrders(room.Game, side, draft);
        }

        await conn.SendAsync(BuildState(room, conn.Role));
        await BroadcastStateToDirector(room);
    }

    private async Task HandleReady(Room room, WsConn conn, JsonElement root)
    {
        if (conn.Role is not ("red" or "blue"))
        {
            await conn.SendAsync(ErrorPayload("只有红/蓝方可以设置准备状态"));
            return;
        }
        lock (room.GameLock)
        {
            room.Ready[conn.Role] = GetBool(root, "value", true);
        }
        await BroadcastAll(room);
    }

    private async Task HandleResolve(Room room, WsConn conn)
    {
        if (conn.Role != "director")
        {
            await conn.SendAsync(ErrorPayload("只有导演可以结算回合"));
            return;
        }
        await ResolveTurnAsync(room);
    }

    private async Task ResolveTurnAsync(Room room)
    {
        TurnResolver.TurnReport report;
        lock (room.GameLock)
        {
            var orders = new Dictionary<string, List<Command>>
            {
                ["red"] = room.Drafts["red"].Select(c => c.Clone()).ToList(),
                ["blue"] = room.Drafts["blue"].Select(c => c.Clone()).ToList(),
            };
            if (room.AiRed && orders["red"].Count == 0)
                orders["red"] = AiService.GenerateOrders(room.Game, "red");
            if (room.AiBlue && orders["blue"].Count == 0)
                orders["blue"] = AiService.GenerateOrders(room.Game, "blue");

            report = TurnResolver.ResolveTurn(room.Game, orders);
            room.Drafts["red"].Clear();
            room.Drafts["blue"].Clear();
            room.Ready["red"] = false;
            room.Ready["blue"] = false;

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

        var eventsPayload = new Dictionary<string, object?>
        {
            ["type"] = "events",
            ["events"] = EventsToMaps(report.Events),
            ["rejected"] = report.Rejected.Select(e => new Dictionary<string, object?>
            {
                ["unit"] = e.UnitId,
                ["reason"] = e.Reason,
                ["message"] = e.Message,
            }).ToList(),
        };

        foreach (var c in room.Slots.Values.ToArray())
        {
            if (!c.IsOpen) continue;
            await c.SendAsync(eventsPayload);
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
                ["instance"] = room.Game.InstanceId,
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
            await conn.SendAsync(ErrorPayload("只有导演可以执行控制指令"));
            return;
        }
        var action = GetStr(root, "action") ?? "";
        List<WarEvent>? newEvents = null;

        switch (action)
        {
            case "weather":
            {
                var value = GetStr(root, "value");
                if (string.IsNullOrEmpty(value)) { await conn.SendAsync(ErrorPayload("缺少天气值")); return; }
                lock (room.GameLock) { room.Game.Weather = value; }
                break;
            }
            case "fog":
            {
                var value = GetBool(root, "value", true);
                lock (room.GameLock)
                {
                    room.Game.Flags["fog"] = value;
                    FogService.UpdateObservation(room.Game);
                }
                break;
            }
            case "spectator_omniscient":
            {
                var value = GetBool(root, "value", false);
                lock (room.GameLock) { room.Game.Flags["spectator_omniscient"] = value; }
                break;
            }
            case "ai":
            {
                var side = GetStr(root, "side");
                if (side is not ("red" or "blue")) { await conn.SendAsync(ErrorPayload("缺少 side")); return; }
                lock (room.GameLock)
                {
                    room.Drafts[side] = AiService.GenerateOrders(room.Game, side);
                }
                break;
            }
            case "inject_intel":
            {
                var side = GetStr(root, "side") ?? "red";
                var text = GetStr(root, "text") ?? GetStr(root, "message") ?? "导演注入情报";
                lock (room.GameLock)
                {
                    var e = new WarEvent
                    {
                        Turn = room.Game.Turn,
                        Code = "intel",
                        Text = text,
                        Sides = new List<string> { side },
                    };
                    room.Game.Events.Add(e);
                    newEvents = new List<WarEvent> { e };
                }
                break;
            }
            case "jump_turn":
            {
                var count = Math.Clamp(GetInt(root, "count", 1), 1, 24);
                for (var i = 0; i < count; i++)
                {
                    await ResolveTurnAsync(room);
                }
                return; // ResolveTurnAsync already broadcast
            }
            default:
                await conn.SendAsync(ErrorPayload($"未知指令: {action}"));
                return;
        }

        if (newEvents != null)
        {
            await BroadcastAll(room, new Dictionary<string, object?>
            {
                ["type"] = "events",
                ["events"] = EventsToMaps(newEvents),
            });
        }
        await BroadcastAll(room);
    }

    // ==================== State / views ====================

    private Dictionary<string, object?> BuildState(Room room, string role)
    {
        lock (room.GameLock)
        {
            Dictionary<string, object?> view = role switch
            {
                "director" => FogService.DirectorView(room.Game),
                "spectator" => FlagBool(room.Game, "spectator_omniscient", false)
                    ? FogService.DirectorView(room.Game)
                    : FogService.SpectatorView(room.Game),
                _ => FogService.SideView(room.Game, role),
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
                ["turn"] = room.Game.Turn,
                ["over"] = room.Game.Over,
                ["winner"] = room.Game.Winner,
                ["scenario"] = room.Game.ScenarioId,
                ["events"] = EventsToMaps(room.Game.Events.TakeLast(80).ToList()),
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
        public List<Dictionary<string, object?>> Frames { get; init; } = new();
        public bool AiRed { get; set; }
        public bool AiBlue { get; set; }
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
