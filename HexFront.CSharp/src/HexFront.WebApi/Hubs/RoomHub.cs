using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace HexFront.WebApi.Hubs;

/// <summary>
/// SignalR hub for real-time multiplayer game sessions.
/// Manages rooms, broadcasts orders, and supports spectator mode.
/// </summary>
public class RoomHub : Hub
{
    private static readonly ConcurrentDictionary<string, GameRoom> Rooms = new();

    /// <summary>Create a new game room.</summary>
    public async Task CreateRoom(string roomId, string hostId)
    {
        var room = new GameRoom
        {
            Id = roomId,
            HostId = hostId,
            CreatedAt = DateTime.UtcNow,
        };

        Rooms[roomId] = room;
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Caller.SendAsync("room_created", roomId);
    }

    /// <summary>Join an existing game room.</summary>
    public async Task JoinRoom(string roomId, string playerId, string side)
    {
        if (!Rooms.TryGetValue(roomId, out var room))
        {
            await Clients.Caller.SendAsync("error", "Room not found");
            return;
        }

        // Check if room is full (max 2 players + spectators)
        if (room.Players.Count >= 2 && side != "spectator")
        {
            await Clients.Caller.SendAsync("error", "Room is full");
            return;
        }

        // Add player to room
        var player = new PlayerInfo
        {
            ConnectionId = Context.ConnectionId,
            PlayerId = playerId,
            Side = side,
            IsHost = room.HostId == playerId,
        };

        room.Players[playerId] = player;
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        // Notify all players in room
        await Clients.Group(roomId).SendAsync("player_joined", new
        {
            roomId,
            playerId,
            side,
            playerCount = room.Players.Count,
        });

        // Send current room state to joining player
        await Clients.Caller.SendAsync("room_state", new
        {
            roomId,
            players = room.Players.Values.Select(p => new { p.PlayerId, p.Side, p.IsHost }),
            gameState = room.GameState,
        });
    }

    /// <summary>Leave a game room.</summary>
    public async Task LeaveRoom(string roomId)
    {
        if (Rooms.TryGetValue(roomId, out var room))
        {
            var player = room.Players.Values.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player != null)
            {
                room.Players.Remove(player.PlayerId);
                
                await Clients.Group(roomId).SendAsync("player_left", new
                {
                    roomId,
                    playerId = player.PlayerId,
                });

                // Remove room if empty
                if (room.Players.Count == 0)
                {
                    Rooms.TryRemove(roomId, out _);
                }
            }
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
    }

    /// <summary>Submit orders for a side (broadcast to all players).</summary>
    public async Task SubmitOrders(string roomId, string side, object orders)
    {
        if (!Rooms.TryGetValue(roomId, out var room))
        {
            await Clients.Caller.SendAsync("error", "Room not found");
            return;
        }

        // Validate player is in room and has correct side
        var player = room.Players.Values.FirstOrDefault(p => 
            p.ConnectionId == Context.ConnectionId && p.Side == side);
        
        if (player == null)
        {
            await Clients.Caller.SendAsync("error", "Not authorized for this side");
            return;
        }

        // Store orders in room state
        room.PendingOrders[side] = orders;

        // Broadcast to all players (including spectators)
        await Clients.Group(roomId).SendAsync("orders_submitted", new
        {
            roomId,
            side,
            orderCount = ((List<object>)orders).Count,
        });
    }

    /// <summary>Broadcast turn advancement to all players.</summary>
    public async Task AdvanceTurn(string roomId, object gameState)
    {
        if (!Rooms.TryGetValue(roomId, out var room))
        {
            await Clients.Caller.SendAsync("error", "Room not found");
            return;
        }

        // Only host can advance turn
        var host = room.Players.Values.FirstOrDefault(p => p.IsHost && p.ConnectionId == Context.ConnectionId);
        if (host == null)
        {
            await Clients.Caller.SendAsync("error", "Only host can advance turn");
            return;
        }

        // Update room game state
        room.GameState = gameState;
        room.PendingOrders.Clear();

        // Broadcast new state to all players
        await Clients.Group(roomId).SendAsync("turn_advanced", new
        {
            roomId,
            gameState,
        });
    }

    /// <summary>Send chat message to room.</summary>
    public async Task SendMessage(string roomId, string playerId, string message)
    {
        if (!Rooms.TryGetValue(roomId, out _))
        {
            await Clients.Caller.SendAsync("error", "Room not found");
            return;
        }

        await Clients.Group(roomId).SendAsync("message_received", new
        {
            roomId,
            playerId,
            message,
            timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>Get list of active rooms.</summary>
    public Task<List<object>> ListRooms()
    {
        var roomList = Rooms.Values.Select(r => new
        {
            r.Id,
            playerCount = r.Players.Count,
            r.CreatedAt,
            hasPendingOrders = r.PendingOrders.Count > 0,
        }).ToList();

        return Task.FromResult(roomList.Cast<object>().ToList());
    }

    /// <summary>Clean up on disconnect.</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Find and remove from all rooms
        var roomsToRemove = new List<string>();
        
        foreach (var (roomId, room) in Rooms)
        {
            var player = room.Players.Values.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player != null)
            {
                room.Players.Remove(player.PlayerId);
                
                await Clients.Group(roomId).SendAsync("player_left", new
                {
                    roomId,
                    playerId = player.PlayerId,
                    disconnected = true,
                });

                if (room.Players.Count == 0)
                {
                    roomsToRemove.Add(roomId);
                }
            }
        }

        foreach (var roomId in roomsToRemove)
        {
            Rooms.TryRemove(roomId, out _);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ==================== Internal Models ====================

    private class GameRoom
    {
        public string Id { get; init; } = "";
        public string HostId { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public ConcurrentDictionary<string, PlayerInfo> Players { get; init; } = new();
        public ConcurrentDictionary<string, object> PendingOrders { get; init; } = new();
        public object? GameState { get; set; }
    }

    private class PlayerInfo
    {
        public string ConnectionId { get; init; } = "";
        public string PlayerId { get; init; } = "";
        public string Side { get; init; } = "";
        public bool IsHost { get; init; }
    }
}
