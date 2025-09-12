using System.Collections.Concurrent;
using PurrLobby.Models;

namespace PurrLobby.Services;

// lobby service core logic
public interface ILobbyService
{
    Task<Lobby> CreateLobbyAsync(Guid gameId, string ownerUserId, string ownerDisplayName, int maxPlayers, Dictionary<string, string>? properties, CancellationToken ct = default);
    Task<Lobby?> JoinLobbyAsync(Guid gameId, string lobbyId, string userId, string displayName, CancellationToken ct = default);
    Task<bool> LeaveLobbyAsync(Guid gameId, string lobbyId, string userId, CancellationToken ct = default);
    Task<bool> LeaveLobbyAsync(Guid gameId, string userId, CancellationToken ct = default);
    Task<List<Lobby>> SearchLobbiesAsync(Guid gameId, int maxRoomsToFind, Dictionary<string, string>? filters, CancellationToken ct = default);
    Task<bool> SetIsReadyAsync(Guid gameId, string lobbyId, string userId, bool isReady, CancellationToken ct = default);
    Task<bool> SetLobbyDataAsync(Guid gameId, string lobbyId, string key, string value, CancellationToken ct = default);
    Task<string?> GetLobbyDataAsync(Guid gameId, string lobbyId, string key, CancellationToken ct = default);
    Task<List<LobbyUser>> GetLobbyMembersAsync(Guid gameId, string lobbyId, CancellationToken ct = default);
    Task<bool> SetAllReadyAsync(Guid gameId, string lobbyId, CancellationToken ct = default);
    Task<bool> SetLobbyStartedAsync(Guid gameId, string lobbyId, CancellationToken ct = default);
    Task<Lobby?> GetLobbyAsync(Guid gameId, string lobbyId, string currentUserId, CancellationToken ct = default);

    // stats
    Task<int> GetGlobalPlayerCountAsync(CancellationToken ct = default);
    Task<int> GetGlobalLobbyCountAsync(CancellationToken ct = default);
    Task<int> GetLobbyCountByGameAsync(Guid gameId, CancellationToken ct = default);
    Task<List<LobbyUser>> GetActivePlayersByGameAsync(Guid gameId, CancellationToken ct = default);
}

// internal lobby state
internal class LobbyState
{
    public required string Id { get; init; }
    public required Guid GameId { get; init; }
    public required string OwnerUserId { get; set; }
    public int MaxPlayers { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LobbyUser> Members { get; } = new();
    public bool Started { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LobbyCode { get; set; } = string.Empty;
}

public class LobbyService : ILobbyService
{
    private const int MinPlayers = 2;
    private const int MaxPlayersLimit = 64;
    private const int NameMaxLength = 64;
    private const int DisplayNameMaxLength = 64;
    private const int PropertyKeyMaxLength = 64;
    private const int PropertyValueMaxLength = 256;
    private const int MaxPropertyCount = 32;

    private readonly ConcurrentDictionary<string, LobbyState> _lobbies = new();
    // user index key gameIdN:userId -> lobbyId
    private readonly ConcurrentDictionary<string, string> _userLobbyIndexByGame = new();
    private readonly ILobbyEventHub _events;

    public LobbyService(ILobbyEventHub events)
    {
        _events = events;
    }

    private static string SanitizeString(string? s, int maxLen)
        => string.IsNullOrWhiteSpace(s) ? string.Empty : (s.Length <= maxLen ? s : s.Substring(0, maxLen)).Trim();

    private static bool IsInvalidId(string? id) => string.IsNullOrWhiteSpace(id) || id.Length > 128;

    private static Lobby Project(LobbyState s, string? currentUserId = null)
    {
        var lobby = new Lobby
        {
            Name = !string.IsNullOrWhiteSpace(s.Name) ? s.Name : (s.Properties.TryGetValue("Name", out var n) ? n : string.Empty),
            IsValid = true,
            LobbyId = s.Id,
            LobbyCode = s.LobbyCode,
            MaxPlayers = s.MaxPlayers,
            IsOwner = currentUserId != null && string.Equals(s.OwnerUserId, currentUserId, StringComparison.Ordinal)
        };
        foreach (var kv in s.Properties)
            lobby.Properties[kv.Key] = kv.Value;
        foreach (var m in s.Members)
            lobby.Members.Add(new LobbyUser { Id = m.Id, DisplayName = m.DisplayName, IsReady = m.IsReady });
        return lobby;
    }

    public async Task<Lobby> CreateLobbyAsync(Guid gameId, string ownerUserId, string ownerDisplayName, int maxPlayers, Dictionary<string, string>? properties, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(ownerUserId))
            throw new ArgumentException("Invalid gameId or ownerUserId");

        var display = SanitizeString(ownerDisplayName, DisplayNameMaxLength);
        var clampedPlayers = Math.Clamp(maxPlayers, MinPlayers, MaxPlayersLimit);

        string GenerateLobbyCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var rng = Random.Shared;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Span<char> s = stackalloc char[6];
                for (int i = 0; i < s.Length; i++) s[i] = chars[rng.Next(chars.Length)];
                var code = new string(s);
                if (!_lobbies.Values.Any(l => string.Equals(l.LobbyCode, code, StringComparison.OrdinalIgnoreCase)))
                    return code;
            }
            // fallback
            return Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
        }

        var state = new LobbyState
        {
            Id = Guid.NewGuid().ToString("N"),
            GameId = gameId,
            OwnerUserId = ownerUserId,
            MaxPlayers = clampedPlayers,
            Name = properties != null && properties.TryGetValue("Name", out var n) ? SanitizeString(n, NameMaxLength) : string.Empty,
            LobbyCode = GenerateLobbyCode()
        };

        if (properties != null)
        {
            foreach (var kv in properties)
            {
                if (state.Properties.Count >= MaxPropertyCount) break;
                var key = SanitizeString(kv.Key, PropertyKeyMaxLength);
                if (string.IsNullOrEmpty(key)) continue;
                var val = SanitizeString(kv.Value, PropertyValueMaxLength);
                state.Properties[key] = val;
            }
        }

        state.Members.Add(new LobbyUser
        {
            Id = ownerUserId,
            DisplayName = display,
            IsReady = false
        });

        _lobbies[state.Id] = state;
        _userLobbyIndexByGame[$"{gameId:N}:{ownerUserId}"] = state.Id;

        await _events.BroadcastAsync(gameId, state.Id, new { type = "lobby_created", lobbyId = state.Id, ownerUserId, ownerDisplayName = display, maxPlayers = state.MaxPlayers }, ct);

        return Project(state, ownerUserId);
    }

    public Task<Lobby?> JoinLobbyAsync(Guid gameId, string lobbyId, string userId, string displayName, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(userId))
            return Task.FromResult<Lobby?>(null);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult<Lobby?>(null);
        if (state.GameId != gameId)
            return Task.FromResult<Lobby?>(null);

        // prevent multi lobby join per game
        if (_userLobbyIndexByGame.TryGetValue($"{gameId:N}:{userId}", out var existingLobbyId) && existingLobbyId != lobbyId)
            return Task.FromResult<Lobby?>(null);

        var name = SanitizeString(displayName, DisplayNameMaxLength);

        lock (state)
        {
            if (state.Started) return Task.FromResult<Lobby?>(null);
            if (state.Members.Any(m => m.Id == userId))
                return Task.FromResult<Lobby?>(Project(state, userId));
            if (state.Members.Count >= state.MaxPlayers)
                return Task.FromResult<Lobby?>(null);
            state.Members.Add(new LobbyUser { Id = userId, DisplayName = name, IsReady = false });
        }
        _userLobbyIndexByGame[$"{gameId:N}:{userId}"] = lobbyId;
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "member_joined", userId, displayName = name }, ct);
        return Task.FromResult<Lobby?>(Project(state, userId));
    }

    public Task<bool> LeaveLobbyAsync(Guid gameId, string lobbyId, string userId, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(userId))
            return Task.FromResult(false);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult(false);
        if (state.GameId != gameId)
            return Task.FromResult(false);
        var removed = false;
        string? newOwner = null;
        lock (state)
        {
            var idx = state.Members.FindIndex(m => m.Id == userId);
            if (idx >= 0)
            {
                state.Members.RemoveAt(idx);
                removed = true;
                if (state.OwnerUserId == userId && state.Members.Count > 0)
                {
                    state.OwnerUserId = state.Members[0].Id; // promote first
                    newOwner = state.OwnerUserId;
                }
            }
        }
        _userLobbyIndexByGame.TryRemove($"{gameId:N}:{userId}", out _);
        if (removed)
        {
            // remove lobby if empty
            if (state.Members.Count == 0)
            {
                _lobbies.TryRemove(lobbyId, out _);
                _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "lobby_empty" }, ct);
                _ = _events.CloseLobbyAsync(gameId, lobbyId, ct);
            }
            else
            {
                _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "member_left", userId, newOwnerUserId = newOwner }, ct);
            }
        }
        return Task.FromResult(removed);
    }

    public Task<bool> LeaveLobbyAsync(Guid gameId, string userId, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(userId))
            return Task.FromResult(false);

        if (_userLobbyIndexByGame.TryGetValue($"{gameId:N}:{userId}", out var lobbyId))
        {
            return LeaveLobbyAsync(gameId, lobbyId, userId, ct);
        }
        return Task.FromResult(false);
    }

    public Task<List<Lobby>> SearchLobbiesAsync(Guid gameId, int maxRoomsToFind, Dictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty)
            return Task.FromResult(new List<Lobby>());

        var take = Math.Clamp(maxRoomsToFind, 1, 100);
        IEnumerable<LobbyState> query = _lobbies.Values.Where(l => l.GameId == gameId && !l.Started && l.Members.Count < l.MaxPlayers);
        if (filters != null)
        {
            foreach (var kv in filters)
            {
                var k = SanitizeString(kv.Key, PropertyKeyMaxLength);
                var v = SanitizeString(kv.Value, PropertyValueMaxLength);
                if (string.IsNullOrEmpty(k)) continue;
                query = query.Where(l => l.Properties.TryGetValue(k, out var pv) && string.Equals(pv, v, StringComparison.OrdinalIgnoreCase));
            }
        }
        var list = query.OrderByDescending(l => l.CreatedAtUtc).Take(take).Select(s => Project(s)).ToList();
        return Task.FromResult(list);
    }

    public Task<bool> SetIsReadyAsync(Guid gameId, string lobbyId, string userId, bool isReady, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(userId))
            return Task.FromResult(false);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult(false);
        if (state.GameId != gameId)
            return Task.FromResult(false);
        lock (state)
        {
            if (state.Started) return Task.FromResult(false);
            var m = state.Members.FirstOrDefault(x => x.Id == userId);
            if (m is null) return Task.FromResult(false);
            m.IsReady = isReady;
        }
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "member_ready", userId, isReady }, ct);
        return Task.FromResult(true);
    }

    public Task<bool> SetLobbyDataAsync(Guid gameId, string lobbyId, string key, string value, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId))
            return Task.FromResult(false);
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(false);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult(false);
        if (state.GameId != gameId)
            return Task.FromResult(false);
        lock (state)
        {
            var k = SanitizeString(key, PropertyKeyMaxLength);
            if (string.IsNullOrEmpty(k)) return Task.FromResult(false);
            var v = SanitizeString(value, PropertyValueMaxLength);
            if (!state.Properties.ContainsKey(k) && state.Properties.Count >= MaxPropertyCount)
                return Task.FromResult(false);

            state.Properties[k] = v;
            if (string.Equals(k, "Name", StringComparison.OrdinalIgnoreCase))
                state.Name = SanitizeString(v, NameMaxLength);
        }
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "lobby_data", key, value }, ct);
        return Task.FromResult(true);
    }

    public Task<string?> GetLobbyDataAsync(Guid gameId, string lobbyId, string key, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || string.IsNullOrWhiteSpace(key))
            return Task.FromResult<string?>(null);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult<string?>(null);
        if (state.GameId != gameId)
            return Task.FromResult<string?>(null);
        return Task.FromResult(state.Properties.TryGetValue(key, out var v) ? v : null);
    }

    public Task<List<LobbyUser>> GetLobbyMembersAsync(Guid gameId, string lobbyId, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId))
            return Task.FromResult(new List<LobbyUser>());

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult(new List<LobbyUser>());
        if (state.GameId != gameId)
            return Task.FromResult(new List<LobbyUser>());
        lock (state)
        {
            return Task.FromResult(state.Members.Select(m => new LobbyUser
            {
                Id = m.Id,
                DisplayName = m.DisplayName,
                IsReady = m.IsReady
            }).ToList());
        }
    }

    public Task<bool> SetAllReadyAsync(Guid gameId, string lobbyId, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId))
            return Task.FromResult(false);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult(false);
        if (state.GameId != gameId)
            return Task.FromResult(false);
        lock (state)
        {
            if (state.Started) return Task.FromResult(false);
            foreach (var m in state.Members)
                m.IsReady = true;
        }
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "all_ready" }, ct);
        return Task.FromResult(true);
    }

    public Task<bool> SetLobbyStartedAsync(Guid gameId, string lobbyId, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId))
            return Task.FromResult(false);

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult(false);
        if (state.GameId != gameId)
            return Task.FromResult(false);
        if (state.Started) return Task.FromResult(false);
        state.Started = true;
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "lobby_started" }, ct);
        return Task.FromResult(true);
    }

    public Task<Lobby?> GetLobbyAsync(Guid gameId, string lobbyId, string currentUserId, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(currentUserId))
            return Task.FromResult<Lobby?>(null);
        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return Task.FromResult<Lobby?>(null);
        if (state.GameId != gameId)
            return Task.FromResult<Lobby?>(null);
        return Task.FromResult<Lobby?>(Project(state, currentUserId));
    }

    public Task<int> GetGlobalPlayerCountAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var state in _lobbies.Values)
        {
            lock (state)
            {
                total += state.Members.Count;
            }
        }
        return Task.FromResult(total);
    }

    public Task<int> GetGlobalLobbyCountAsync(CancellationToken ct = default)
    {
        var count = _lobbies.Count;
        return Task.FromResult(count);
    }

    public Task<int> GetLobbyCountByGameAsync(Guid gameId, CancellationToken ct = default)
    {
        var count = _lobbies.Values.Count(l => l.GameId == gameId);
        return Task.FromResult(count);
    }

    public Task<List<LobbyUser>> GetActivePlayersByGameAsync(Guid gameId, CancellationToken ct = default)
    {
        var players = new Dictionary<string, LobbyUser>();
        foreach (var state in _lobbies.Values)
        {
            if (state.GameId != gameId) continue;
            lock (state)
            {
                foreach (var m in state.Members)
                {
                    // unique per user id in game
                    players[m.Id] = new LobbyUser { Id = m.Id, DisplayName = m.DisplayName, IsReady = m.IsReady };
                }
            }
        }
        return Task.FromResult(players.Values.ToList());
    }
}
