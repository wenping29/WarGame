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
    private readonly OpsLogService _ops;
    private readonly AppDbService _db;
    private readonly string _scenarioDir;
    private readonly string _saveDir;
    private readonly string _gisDir;
    private readonly string _dataDir;

    public RoomService(ILogger<RoomService> logger, OpsLogService ops, AppDbService db)
    {
        _logger = logger;
        _ops = ops;
        _db = db;
        var root = GetRepoRoot();
        _dataDir = Path.Combine(root, "data");
        _scenarioDir = Environment.GetEnvironmentVariable("HF_SCENARIO_DIR")
            ?? Path.Combine(_dataDir, "scenarios");
        _saveDir = Environment.GetEnvironmentVariable("HF_SAVE_DIR")
            ?? Path.Combine(_dataDir, "saves"); // §12.1：副本存档落在 data/saves/<实例>/
        _gisDir = Path.Combine(_dataDir, "gis");
    }

    // ==================== Public API for REST controller ====================

    public string ScenarioDirectory => _scenarioDir;
    public string SaveDirectory => _saveDir;
    public string GisDirectory => _gisDir;
    /// <summary>仓库 data 目录（剧本备份、内容反馈等管理类文件落这里）。</summary>
    public string DataDirectory => _dataDir;

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
                room.Frames.Add(FrameOf(game, game.Turn - 1, EventsToMaps(game.Events.TakeLast(200).ToList()), null));
            }
        }

        _rooms[code] = room;
        // 应用库：房间目录登记
        _db.UpsertRoom(code, game.InstanceId, game.ScenarioId,
            instanceName ?? (string.IsNullOrEmpty(game.Name) ? game.ScenarioName : game.Name), game.Turn);
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
            // roles 是会话计数（room.dart:214 roleSessions(s).length）。本实现的 Slots 是角色→单条连接，
            // 因为 doc §12.1 规定每个角色位同时只有一条会话（阵营位并发直接拒，导演/观战顶号），
            // 所以计数取值只能是 0 或 1；终端关闭后归零即"断开即释放"的验收点。
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
                ["seed"] = g.Seed,
                ["fingerprint"] = g.Fingerprint(),
                ["width"] = g.Width,
                ["height"] = g.Height,
            };
        }
    }

    public List<Dictionary<string, object?>> ListRooms() =>
        _rooms.Keys.Select(k => PublicInfo(k)).Where(x => x != null).Select(x => x!).ToList();

    /// <summary>查找持有某实例的房间码（无则 null）。副本删除前判断占用用。</summary>
    public string? FindRoomByInstance(string instance)
    {
        foreach (var (code, room) in _rooms)
        {
            lock (room.GameLock)
            {
                if (string.Equals(room.Game.InstanceId, instance, StringComparison.Ordinal)) return code;
            }
        }
        return null;
    }

    /// <summary>移除一个没有任何在线连接的空闲房间（副本删除清场用）。
    /// 仍有人在线返回 false；房间不存在返回 true（视为已清场）。</summary>
    public bool RemoveIfIdle(string code)
    {
        if (!_rooms.TryGetValue(code, out var room)) return true;
        lock (room.GameLock)
        {
            foreach (var slot in room.Slots.Values)
                if (slot.IsOpen) return false;
        }
        _rooms.TryRemove(code, out _);
        return true;
    }

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

        // 副本只能由 POST /api/instances 或 POST /api/saves/load 创建；
        // 未知房间码在这里静默建局会让打错码的人开出一局没人知道的仗，且与 GET /api/room/{code} 的 404 自相矛盾。
        if (!TryFindRoom(code, out var roomFound) || roomFound.Game == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("room not found");
            return;
        }
        var room = roomFound;

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var conn = new WsConn
        {
            Socket = socket,
            Role = role,
            Name = string.IsNullOrEmpty(name) ? null : name,
            Token = Guid.NewGuid().ToString("N"),
        };

        // Role takeover: 阵营位（红/蓝）只能由同 token 重连或 force 接管；
        // 导演部/观战不是对抗信息位，后来者直接接管（doc §12.1「同角色顶号」，与 room.dart:399-406 一致）。
        WsConn? kickedOld = null;
        lock (room.GameLock)
        {
            if (room.Slots.TryGetValue(role, out var old) && !ReferenceEquals(old, conn) && old.IsOpen)
            {
                var sameTerminal = !string.IsNullOrEmpty(token) && token == old.Token;
                if (force || sameTerminal || role is "director" or "spectator")
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

        // 首包带视图与房间信息：Dart server.dart:418 与 web 各视图都直接读 welcome.view，
        // 缺它就要等第二包 state 才能落第一帧。
        Dictionary<string, object?> welcome;
        lock (room.GameLock)
        {
            welcome = new Dictionary<string, object?>
            {
                ["type"] = "welcome",
                ["token"] = conn.Token,
                ["room"] = PublicInfo(room.Code),
                ["role"] = role,
                ["view"] = ViewFor(room, role),
            };
        }
        await conn.SendAsync(welcome);
        await conn.SendAsync(BuildState(room, role));

        try
        {
            await ReceiveLoop(room, conn, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "room {Code}: {Role} receive aborted", code, role);
        }
        finally
        {
            // Deregister only if our slot was not taken over by a newer connection.
            if (room.Slots.TryGetValue(role, out var cur) && ReferenceEquals(cur, conn))
            {
                room.Slots.TryRemove(role, out _);
            }
            // 对端可能不发 close 帧就掉线（进程退出/断网），主动作废该 socket，
            // 否则 IsOpen 永远为 true，角色位被僵尸连接占死、force 接管也救不回来。
            try { conn.Socket.Abort(); } catch { /* 忽略 */ }
            _logger.LogInformation("room {Code}: {Role} disconnected", code, role);
        }
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
                // 回放帧含导演部全知视图，对抗方取用即等于看对手底牌
                if (conn.Role is not ("director" or "spectator"))
                {
                    await conn.SendAsync(ErrorPayload("只有导演部与观战可以获取回放帧"));
                    break;
                }
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
                var cmd = CommandWire.FromJson(el); // 宽松：unit/unitId、[[q,r]]/{q,r} 都收
                if (cmd != null) cmds.Add(cmd);
            }
        }

        int turnNow;
        lock (room.GameLock)
        {
            var list = room.Drafts[side];
            if (replace) list.Clear();
            list.AddRange(cmds);
            var draft = list.Select(c => c.Clone()).ToList();
            var errors = CommandValidator.ValidateOrders(room.Game, side, draft);
            turnNow = room.Game.Turn;
            // Dart submitOrders：新指令到达即撤销本方准备态
            room.Ready[side] = false;
            room.LastErrors[side] = errors.Select(e => new Dictionary<string, object?>
            {
                ["unit"] = e.UnitId,
                ["reason"] = e.Reason,
                ["message"] = e.Message,
            }).ToList();
        }

        // SQLite 操作流水：提交指令阶段（含 replace 语义与被拒条数）
        _ops.LogOrders(room.Game.InstanceId, side, turnNow, replace, cmds, room.LastErrors[side].Count);

        await BroadcastAll(room);
    }

    private async Task HandleClearOrders(Room room, WsConn conn)
    {
        if (conn.Role is not ("red" or "blue"))
        {
            await conn.SendAsync(ErrorPayload("无权操作"));
            return;
        }
        int turnNow;
        lock (room.GameLock)
        {
            room.Drafts[conn.Role].Clear();
            room.Ready[conn.Role] = false;
            turnNow = room.Game.Turn;
        }
        _ops.Log(room.Game.InstanceId, OpsLogService.StageClearOrders, conn.Role, turnNow, null);
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
        int turnNow = 0;
        lock (room.GameLock)
        {
            turnNow = room.Game.Turn;
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
            _ops.Log(room.Game.InstanceId, OpsLogService.StageReady, conn.Role, turnNow,
                new Dictionary<string, object?> { ["value"] = value, ["accepted"] = false, ["reject"] = reject });
            await conn.SendAsync(ErrorPayload(reject));
            return;
        }
        _ops.Log(room.Game.InstanceId, OpsLogService.StageReady, conn.Role, turnNow,
            new Dictionary<string, object?> { ["value"] = value, ["accepted"] = value });
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

            // AI 补令本身也抽随机数：记下结算起点的随机源状态，重放才能拨回同一位置
            var rngAtResolve = room.Game.RngState;
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

            room.Frames.Add(FrameOf(room.Game, room.Game.Turn - 1, EventsToMaps(report.Events),
                report.Battles.Select(BattleToMap).ToList()));
            TrimFrames(room);

            PersistTurn(room, byRole, rngAtResolve, orders, report);
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

    /// <summary>结算落盘：指令记录每回合必写，全量快照每 K 回合或终局一次。写盘失败只告警，不打断已算完的回合。</summary>
    private void PersistTurn(
        Room room, string byRole, uint rngAtResolve,
        Dictionary<string, List<Command>> orders, TurnResolver.TurnReport report)
    {
        var resolvedTurn = room.Game.Turn - 1;
        StoreService.SaveMetadata? snapMeta = null;
        try
        {
            StoreService.AppendTurn(room.Game.InstanceId, new StoreService.TurnRecord
            {
                Turn = resolvedTurn,
                At = DateTime.UtcNow,
                ResolvedBy = byRole,
                RngAtResolve = rngAtResolve,
                Red = orders["red"],
                Blue = orders["blue"],
                Rejected = report.Rejected.Select(StoreService.RejectedOrder.From).ToList(),
                FpBefore = report.FingerprintBefore,
                FpAfter = report.FingerprintAfter,
            }, _saveDir);

            if (StoreService.ShouldSnapshot(resolvedTurn, room.Game.Over))
                snapMeta = StoreService.WriteSnapshot(room.Game, _saveDir, room.InstanceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回合 {Turn} 落盘失败（副本 {Instance}）", resolvedTurn, room.Game.InstanceId);
        }

        if (snapMeta != null)
        {
            // 应用库：自动快照副本目录 upsert
            _db.UpsertSave(new StoreSaveMeta(snapMeta.Instance, snapMeta.Name, snapMeta.Scenario,
                snapMeta.Seed, snapMeta.LastTurn, snapMeta.MaxTurns,
                snapMeta.CreatedAt.ToString("o"), snapMeta.UpdatedAt.ToString("o"), "snapshot"));
        }

        // 应用库：把本回合落盘的内容（turns.jsonl + 新快照 + meta）同步进 save_docs，
        // 使 SQLite 成为统一真源；失败只告警，不阻断已结算的回合。
        try
        {
            _db.StoreSaveFile(_saveDir, room.Game.InstanceId, "turns", null, "turns.jsonl", DateTime.UtcNow);
            if (snapMeta != null)
            {
                _db.StoreSaveFile(_saveDir, room.Game.InstanceId, "snapshot", resolvedTurn,
                    $"snapshot_t{resolvedTurn}.json", snapMeta.UpdatedAt);
                _db.StoreSaveFile(_saveDir, room.Game.InstanceId, "meta", null, "meta.json", snapMeta.UpdatedAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "副本内容同步入库失败（副本 {Instance}）", room.Game.InstanceId);
        }

        // SQLite 操作流水：回合结算阶段（双方最终指令 + 随机源 + 指纹，供复盘/审计）
        _ops.LogTurnResolved(room.Game.InstanceId, resolvedTurn, byRole, rngAtResolve,
            new Dictionary<string, IReadOnlyList<Command>>
            {
                ["red"] = orders["red"],
                ["blue"] = orders["blue"],
            },
            report.Rejected.Count, report.FingerprintBefore, report.FingerprintAfter,
            room.Game.Over, room.Game.Winner);
    }

    private async Task HandleSave(Room room, WsConn conn, JsonElement root)
    {
        string? name = GetStr(root, "name");
        string? desc = GetStr(root, "description");
        try
        {
            StoreService.SaveMetadata meta;
            lock (room.GameLock)
            {
                // 手动存档 = 给同一副本再落一张全量快照，实例 id 不变，可反复覆盖
                meta = StoreService.WriteSnapshot(room.Game, _saveDir,
                    name ?? room.InstanceName, desc);
            }
            _ops.Log(room.Game.InstanceId, OpsLogService.StageSave, conn.Role, meta.LastTurn,
                new Dictionary<string, object?>
                {
                    ["instance"] = meta.Instance, ["name"] = meta.Name, ["turn"] = meta.LastTurn,
                });
            // 应用库：手动存档副本目录 upsert + 内容同步进 save_docs
            _db.UpsertSave(new StoreSaveMeta(meta.Instance, meta.Name, meta.Scenario,
                meta.Seed, meta.LastTurn, meta.MaxTurns,
                meta.CreatedAt.ToString("o"), meta.UpdatedAt.ToString("o"), "manual"));
            try
            {
                _db.StoreSaveFile(_saveDir, meta.Instance, "snapshot", meta.SnapshotTurn,
                    $"snapshot_t{meta.SnapshotTurn}.json", meta.UpdatedAt);
                _db.StoreSaveFile(_saveDir, meta.Instance, "meta", null, "meta.json", meta.UpdatedAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "手动存档内容同步入库失败（副本 {Instance}）", meta.Instance);
            }
            await BroadcastAll(room, new Dictionary<string, object?>
            {
                ["type"] = "saved",
                ["turn"] = meta.LastTurn,
                ["instance"] = meta.Instance, // 与存档列表的 instance 同为可载入的副本 id
                ["meta"] = new Dictionary<string, object?>
                {
                    ["instance"] = meta.Instance,
                    ["name"] = meta.Name,
                    ["description"] = meta.Description,
                    ["scenario"] = meta.Scenario,
                    ["seed"] = meta.Seed,
                    ["created_at"] = meta.CreatedAt.ToString("o"),
                    ["updated_at"] = meta.UpdatedAt.ToString("o"),
                    ["last_turn"] = meta.LastTurn,
                    ["max_turns"] = meta.MaxTurns,
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

        // SQLite 操作流水：导演导调阶段（记录原始指令，成败与否都留痕）
        _ops.Log(room.Game.InstanceId, OpsLogService.StageDirectorCmd, "director", room.Game.Turn, root);

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
            case "weather_schedule_set":
            {
                // { action:'weather_schedule_set', phases:[{from,to,weather}] } —— 整组替换计划
                var list = new List<WeatherPhase>();
                if (root.TryGetProperty("phases", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        var from = p.TryGetProperty("from", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetInt32() : 0;
                        var to = p.TryGetProperty("to", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetInt32() : 0;
                        var wx = p.TryGetProperty("weather", out var wv) ? wv.GetString() ?? "" : "";
                        if (from < 1 || to < from || !room.Game.Weathers.ContainsKey(wx))
                        {
                            await conn.SendAsync(ErrorPayload($"计划时段 {from}-{to}/{wx} 无效（需 1 ≤ from ≤ to，且天气存在）"));
                            return;
                        }
                        list.Add(new WeatherPhase { FromTurn = from, ToTurn = to, Weather = wx });
                    }
                }
                if (list.Count == 0)
                {
                    await conn.SendAsync(ErrorPayload("天气计划为空"));
                    return;
                }
                lock (room.GameLock)
                {
                    room.Game.WeatherSchedule = list;
                    var n = list.Count;
                    newEvents.Add(EmitLocked(room, "weather_schedule",
                        $"导演部更新天气计划（{n} 段时段）", new List<string> { "red", "blue", "spectator" }));
                }
                break;
            }
            case "weather_schedule_start":
            {
                if (room.Game.WeatherSchedule is not { Count: > 0 })
                {
                    await conn.SendAsync(ErrorPayload("尚未设置天气计划"));
                    return;
                }
                lock (room.GameLock)
                {
                    room.Game.WeatherScheduleState = "running";
                    // 启动即生效：当前回合若落在某段区间内立即切换
                    var cur = room.Game.WeatherSchedule
                        .FirstOrDefault(p => room.Game.Turn >= p.FromTurn && room.Game.Turn <= p.ToTurn);
                    if (cur != null && room.Game.Weather != cur.Weather)
                    {
                        room.Game.Weather = cur.Weather;
                        FogService.UpdateObservation(room.Game);
                    }
                    newEvents.Add(EmitLocked(room, "weather_schedule",
                        $"导演部启动天气计划（{room.Game.WeatherSchedule.Count} 段）",
                        new List<string> { "red", "blue", "spectator" }));
                }
                break;
            }
            case "weather_schedule_pause":
            {
                if (room.Game.WeatherScheduleState != "running")
                {
                    await conn.SendAsync(ErrorPayload("天气计划未在执行"));
                    return;
                }
                lock (room.GameLock)
                {
                    room.Game.WeatherScheduleState = "paused";
                    newEvents.Add(EmitLocked(room, "weather_schedule",
                        "导演部暂停天气计划（当前天气冻结）", new List<string> { "red", "blue", "spectator" }));
                }
                break;
            }
            case "weather_schedule_delete":
            {
                lock (room.GameLock)
                {
                    room.Game.WeatherSchedule = new List<WeatherPhase>();
                    room.Game.WeatherScheduleState = null;
                    newEvents.Add(EmitLocked(room, "weather_schedule",
                        "导演部删除天气计划（回到原演变模式）", new List<string> { "red", "blue", "spectator" }));
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
            var view = ViewFor(room, role);

            List<Dictionary<string, object?>>? draftMaps = null;
            if (role is "red" or "blue")
            {
                draftMaps = room.Drafts[role].Select(CommandWire.ToMap).ToList();
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

    /// <summary>按角色裁剪的权威视图（对齐 room.dart viewFor）。须在 GameLock 内调用。</summary>
    private Dictionary<string, object?> ViewFor(Room room, string role)
    {
        var g = room.Game;
        var omniscient = FlagBool(g, "spectator_omniscient", false);
        return role switch
        {
            "director" => WithDirectorExtras(room, FogService.DirectorView(g)),
            "spectator" => omniscient
                ? FogService.DirectorView(g)
                : FogService.SpectatorView(g),
            _ => WithSideExtras(room, role, FogService.SideView(g, role)),
        };
    }

    /// <summary>导演部视图附加：双方已受领指令、准备态、被拒错误、AI 托管标记（对齐 room.dart viewFor）。</summary>
    private Dictionary<string, object?> WithDirectorExtras(Room room, Dictionary<string, object?> view)
    {
        view["orders"] = new Dictionary<string, object?>
        {
            ["red"] = room.Drafts["red"].Select(CommandWire.ToMap).ToList(),
            ["blue"] = room.Drafts["blue"].Select(CommandWire.ToMap).ToList(),
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
        // 天气计划只进导演视图：红蓝不看到计划内容，仅通过事件得知天气切换结果
        view["weather_schedule"] = new Dictionary<string, object?>
        {
            ["state"] = room.Game.WeatherScheduleState ?? "off",
            ["phases"] = (room.Game.WeatherSchedule ?? new List<WeatherPhase>()).Select(p =>
                new Dictionary<string, object?>
                {
                    ["from_turn"] = p.FromTurn,
                    ["to_turn"] = p.ToTurn,
                    ["weather"] = p.Weather,
                }).ToList(),
        };
        return view;
    }

    private Dictionary<string, object?> WithSideExtras(Room room, string side, Dictionary<string, object?> view)
    {
        view["draft"] = room.Drafts[side].Select(CommandWire.ToMap).ToList();
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

    /// <summary>回放帧上限：房间是常驻内存的，不设上限则长局会把整局底牌留在帧表里。</summary>
    private const int MaxFrames = 200;

    private static Dictionary<string, object?> FrameOf(
        GameState g, int turn, List<Dictionary<string, object?>> events,
        List<Dictionary<string, object?>>? battles) => new()
    {
        ["turn"] = turn,
        ["hour"] = g.HourOfDay,
        ["weather"] = g.Weather,
        ["director"] = FogService.DirectorView(g),
        ["events"] = events,
        // 前端 Frame 类型要求 battles 恒在（无交战的回合是空数组，不是缺字段）
        ["battles"] = battles ?? new List<Dictionary<string, object?>>(),
    };

    private static void TrimFrames(Room room)
    {
        if (room.Frames.Count > MaxFrames)
            room.Frames.RemoveRange(0, room.Frames.Count - MaxFrames);
    }

    /// <summary>
    /// 战报帧：CF 因子链逐项随战报下发（doc M6「战报可解释各系数贡献」），
    /// 导演部据此回答「这一格为什么打不动」，而不是只看到两个乘积。
    /// </summary>
    private static Dictionary<string, object?> BattleToMap(TurnResolver.BattleRecord b) => new()
    {
        ["turn"] = b.Turn,
        ["kind"] = b.Kind,
        ["atk"] = b.Attacker,
        ["def"] = b.Defender,
        ["atk_loss"] = b.AtkLoss,
        ["def_loss"] = b.DefLoss,
        ["atk_cf"] = b.AtkCf,
        ["def_cf"] = b.DefCf,
        ["factors_atk"] = b.FactorsAtk.ToMap(),
        ["factors_def"] = b.FactorsDef.ToMap(),
        ["routed"] = b.Routed,
    };

    // ==================== Parsing helpers ====================

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
