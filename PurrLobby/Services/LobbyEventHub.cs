using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PurrLobby.Services;

public interface ILobbyEventHub
{
    Task HandleConnectionAsync(Guid gameId, string lobbyId, string sessionToken, WebSocket socket, CancellationToken ct);
    Task BroadcastAsync(Guid gameId, string lobbyId, object evt, CancellationToken ct = default);
    Task CloseLobbyAsync(Guid gameId, string lobbyId, CancellationToken ct = default);
}

public class LobbyEventHub : ILobbyEventHub
{
    private sealed record LobbyKey(Guid GameId, string LobbyId)
    {
        public override string ToString() => $"{GameId:N}:{LobbyId}";
    }

    private sealed class Subscriber
    {
        public required string SessionToken { get; init; }
        public required string UserId { get; init; }
        public DateTime LastPongUtc { get; set; } = DateTime.UtcNow;
    }

    // subs per lobby
    private readonly ConcurrentDictionary<LobbyKey, ConcurrentDictionary<WebSocket, Subscriber>> _subscribers = new();
    // idle cleanup flags
    private readonly ConcurrentDictionary<LobbyKey, byte> _idleCleanupPending = new();
    // active ping loops
    private readonly ConcurrentDictionary<LobbyKey, byte> _pingLoopsActive = new();

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuthenticationService _authService;

    // ping settings
    private const int PingIntervalSeconds = 10;
    private const int PongTimeoutSeconds = 15;

    // idle cleanup delay
    private const int IdleLobbyCleanupDelaySeconds = 45;

    public LobbyEventHub(IServiceScopeFactory scopeFactory, IAuthenticationService authService)
    {
        _scopeFactory = scopeFactory;
        _authService = authService;
    }

public async Task HandleConnectionAsync(Guid gameId, string lobbyId, string sessionToken, WebSocket socket, CancellationToken ct)
    {
        // Validate session token before allowing connection
        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid session token", ct);
            }
            catch { }
            return;
        }

        var key = new LobbyKey(gameId, lobbyId);
        var bag = _subscribers.GetOrAdd(key, _ => new());
        var sub = new Subscriber { SessionToken = sessionToken, UserId = validation.UserId!, LastPongUtc = DateTime.UtcNow };
        bag.TryAdd(socket, sub);

        EnsurePingLoopStarted(key);

        try
        {
            var buffer = new byte[8 * 1024];
            var segment = new ArraySegment<byte>(buffer);
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(segment, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (IsPong(text))
                    {
                        if (bag.TryGetValue(socket, out var s))
                            s.LastPongUtc = DateTime.UtcNow;
                        continue;
                    }
                }
            }
        }
        catch { }
        finally
        {
            bag.TryRemove(socket, out _);
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }

            if (bag.IsEmpty)
                ScheduleIdleCleanup(key);
        }
    }

    private void EnsurePingLoopStarted(LobbyKey key)
    {
        if (!_pingLoopsActive.TryAdd(key, 1)) return;
        _ = Task.Run(() => PingLoopAsync(key));
    }

    private async Task PingLoopAsync(LobbyKey key)
    {
        try
        {
            while (true)
            {
                if (!_subscribers.TryGetValue(key, out var bag) || bag.IsEmpty)
                    break;

                var pingSentAt = DateTime.UtcNow;
                var ping = JsonSerializer.SerializeToUtf8Bytes(new { type = "ping", ts = pingSentAt.Ticks }, _jsonOptions);

                var sockets = bag.Keys.ToList();
                foreach (var ws in sockets)
                {
                    if (ws.State != WebSocketState.Open) continue;
                    try { await ws.SendAsync(ping, WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(PongTimeoutSeconds)); } catch { }

                if (!_subscribers.TryGetValue(key, out bag) || bag.IsEmpty)
                    break;

                var responders = new List<(WebSocket ws, Subscriber sub)>();
                var nonResponders = new List<(WebSocket ws, Subscriber sub)>();
                foreach (var kv in bag)
                {
                    if (kv.Value.LastPongUtc >= pingSentAt)
                        responders.Add((kv.Key, kv.Value));
                    else
                        nonResponders.Add((kv.Key, kv.Value));
                }

                if (responders.Count == 0)
                {
                    await ForceCloseLobbyAsync(key);
                    break;
                }

                if (nonResponders.Count > 0)
                {
                    foreach (var (ws, sub) in nonResponders)
                    {
                        bag.TryRemove(ws, out _);
                        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "pong timeout", CancellationToken.None); } catch { }
try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var svc = scope.ServiceProvider.GetRequiredService<ILobbyService>();
                            await svc.LeaveLobbyAsync(key.GameId, sub.SessionToken, CancellationToken.None);
                        }
                        catch { }
                    }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(PingIntervalSeconds)); } catch { }
            }
        }
        finally
        {
            _pingLoopsActive.TryRemove(key, out _);
        }
    }

    private async Task ForceCloseLobbyAsync(LobbyKey key)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ILobbyService>();
            var members = await svc.GetLobbyMembersAsync(key.GameId, key.LobbyId, CancellationToken.None);
            if (members != null)
            {
                foreach (var m in members)
                {
                    try { await svc.LeaveLobbyAsync(key.GameId, key.LobbyId, m.SessionToken, CancellationToken.None); } catch { }
                }
            }
        }
        catch { }
        finally
        {
            await CloseLobbyAsync(key.GameId, key.LobbyId, CancellationToken.None);
        }
    }

    public async Task BroadcastAsync(Guid gameId, string lobbyId, object evt, CancellationToken ct = default)
    {
        var key = new LobbyKey(gameId, lobbyId);
        if (!_subscribers.TryGetValue(key, out var bag) || bag.IsEmpty)
        {
            ScheduleIdleCleanup(key);
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, _jsonOptions);
        var toRemove = new List<WebSocket>();
        foreach (var kv in bag)
        {
            var ws = kv.Key;
            if (ws.State != WebSocketState.Open)
            {
                toRemove.Add(ws);
                continue;
            }
            try { await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct); } catch { toRemove.Add(ws); }
        }
        foreach (var ws in toRemove)
        {
            bag.TryRemove(ws, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "removed", CancellationToken.None); } catch { }
        }

        if (bag.IsEmpty)
            ScheduleIdleCleanup(key);
        else
            EnsurePingLoopStarted(key);
    }

    public async Task CloseLobbyAsync(Guid gameId, string lobbyId, CancellationToken ct = default)
    {
        var key = new LobbyKey(gameId, lobbyId);
        if (!_subscribers.TryRemove(key, out var bag)) return;

        var evt = new { type = "lobby_deleted", lobbyId, gameId };
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt, _jsonOptions);
        foreach (var kv in bag)
        {
            var ws = kv.Key;
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "lobby deleted", ct);
                }
            }
            catch { }
        }
    }

    private static bool IsPong(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t == "pong" || t == "hb" || t == "heartbeat") return true;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var v = typeProp.GetString()?.Trim().ToLowerInvariant();
                return v == "pong" || v == "hb" || v == "heartbeat";
            }
        }
        catch { }
        return false;
    }

    private void ScheduleIdleCleanup(LobbyKey key)
    {
        if (!_idleCleanupPending.TryAdd(key, 1)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IdleLobbyCleanupDelaySeconds));

                if (_subscribers.TryGetValue(key, out var bag) && !bag.IsEmpty)
                    return;

                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ILobbyService>();

                var members = await svc.GetLobbyMembersAsync(key.GameId, key.LobbyId, CancellationToken.None);
                if (members != null && members.Count > 0)
                {
                    foreach (var m in members)
                    {
                        try { await svc.LeaveLobbyAsync(key.GameId, key.LobbyId, m.SessionToken, CancellationToken.None); } catch { }
                    }
                }

                await CloseLobbyAsync(key.GameId, key.LobbyId, CancellationToken.None);
            }
            catch { }
            finally
            {
                _idleCleanupPending.TryRemove(key, out _);
            }
        });
    }
}
