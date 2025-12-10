using System.Collections.Concurrent;
using PurrLobby.Models;

namespace PurrLobby.Services;

// lobby service core logic
public interface ILobbyService
{
    Task<Lobby> CreateLobbyAsync(Guid gameId, string sessionToken, int maxPlayers, Dictionary<string, string>? properties, CancellationToken ct = default);
    Task<Lobby?> JoinLobbyAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default);
    Task<bool> LeaveLobbyAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default);
    Task<bool> LeaveLobbyAsync(Guid gameId, string sessionToken, CancellationToken ct = default);
    Task<List<Lobby>> SearchLobbiesAsync(Guid gameId, int maxRoomsToFind, Dictionary<string, string>? filters, CancellationToken ct = default);
    Task<bool> SetIsReadyAsync(Guid gameId, string lobbyId, string sessionToken, bool isReady, CancellationToken ct = default);
    Task<bool> SetLobbyDataAsync(Guid gameId, string lobbyId, string sessionToken, string key, string value, CancellationToken ct = default);
    Task<string?> GetLobbyDataAsync(Guid gameId, string lobbyId, string key, CancellationToken ct = default);
    Task<List<LobbyUser>> GetLobbyMembersAsync(Guid gameId, string lobbyId, CancellationToken ct = default);
    
    Task<bool> SetLobbyStartedAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default);
    Task<Lobby?> GetLobbyAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default);

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
    public required string OwnerSessionToken { get; set; }
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
    // user index key gameIdN:sessionToken -> lobbyId
    private readonly ConcurrentDictionary<string, string> _userLobbyIndexByGame = new();
    private readonly ILobbyEventHub _events;
    private readonly IAuthenticationService _authService;

    public LobbyService(ILobbyEventHub events, IAuthenticationService authService)
    {
        _events = events;
        _authService = authService;
    }

    private static string SanitizeString(string? s, int maxLen)
        => string.IsNullOrWhiteSpace(s) ? string.Empty : (s.Length <= maxLen ? s : s.Substring(0, maxLen)).Trim();

    private static bool IsInvalidId(string? id) => string.IsNullOrWhiteSpace(id) || id.Length > 128;

    private async Task<Lobby> ProjectAsync(LobbyState s, string? currentSessionToken = null, CancellationToken ct = default)
    {
        var lobby = new Lobby
        {
            Name = !string.IsNullOrWhiteSpace(s.Name) ? s.Name : (s.Properties.TryGetValue("Name", out var n) ? n : string.Empty),
            IsValid = true,
            LobbyId = s.Id,
            LobbyCode = s.LobbyCode,
            MaxPlayers = s.MaxPlayers,
            IsOwner = false
        };

        if (currentSessionToken != null)
        {
            var validation = await _authService.ValidateTokenAsync(currentSessionToken, ct);
            if (validation.IsValid && string.Equals(s.OwnerUserId, validation.UserId, StringComparison.Ordinal))
            {
                lobby.IsOwner = true;
            }
        }

        foreach (var kv in s.Properties)
            lobby.Properties[kv.Key] = kv.Value;
        foreach (var m in s.Members)
            lobby.Members.Add(new LobbyUser { SessionToken = m.SessionToken, UserId = m.UserId, DisplayName = m.DisplayName, IsReady = m.IsReady });
        return lobby;
    }

    public async Task<Lobby> CreateLobbyAsync(Guid gameId, string sessionToken, int maxPlayers, Dictionary<string, string>? properties, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(sessionToken))
            throw new ArgumentException("Invalid gameId or sessionToken");

        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            throw new UnauthorizedAccessException("Invalid session token");

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
            OwnerSessionToken = sessionToken,
            OwnerUserId = validation.UserId!,
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
            SessionToken = sessionToken,
            UserId = validation.UserId!,
            DisplayName = validation.DisplayName!,
            IsReady = false
        });

        _lobbies[state.Id] = state;
        _userLobbyIndexByGame[$"{gameId:N}:{sessionToken}"] = state.Id;

        await _events.BroadcastAsync(gameId, state.Id, new { type = "lobby_created", lobbyId = state.Id, ownerUserId = validation.UserId, ownerDisplayName = validation.DisplayName, maxPlayers = state.MaxPlayers }, ct);

        return await ProjectAsync(state, sessionToken, ct);
    }

    public async Task<Lobby?> JoinLobbyAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(sessionToken))
            return null;

        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            return null;

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return null;
        if (state.GameId != gameId)
            return null;

        // prevent multi lobby join per game
        if (_userLobbyIndexByGame.TryGetValue($"{gameId:N}:{sessionToken}", out var existingLobbyId) && existingLobbyId != lobbyId)
            return null;

        LobbyUser? existingMember = null;
        bool canJoin = false;
        
        lock (state)
        {
            if (state.Started) return null;
            existingMember = state.Members.FirstOrDefault(m => m.SessionToken == sessionToken);
            if (existingMember != null)
                canJoin = true;
            else if (state.Members.Count < state.MaxPlayers)
            {
                state.Members.Add(new LobbyUser { 
                    SessionToken = sessionToken, 
                    UserId = validation.UserId!, 
                    DisplayName = validation.DisplayName!, 
                    IsReady = false 
                });
                canJoin = true;
            }
        }
        
        if (!canJoin) return null;
        if (existingMember != null)
            return await ProjectAsync(state, sessionToken, ct);
        _userLobbyIndexByGame[$"{gameId:N}:{sessionToken}"] = lobbyId;
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "member_joined", userId = validation.UserId, displayName = validation.DisplayName }, ct);
        return await ProjectAsync(state, sessionToken, ct);
    }

    public async Task<bool> LeaveLobbyAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(sessionToken))
            return false;

        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            return false;

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return false;
        if (state.GameId != gameId)
            return false;
        var removed = false;
        string? newOwner = null;
        lock (state)
        {
            var idx = state.Members.FindIndex(m => m.SessionToken == sessionToken);
            if (idx >= 0)
            {
                var member = state.Members[idx];
                state.Members.RemoveAt(idx);
                removed = true;
                if (state.OwnerUserId == member.UserId && state.Members.Count > 0)
                {
                    var newOwnerMember = state.Members[0];
                    state.OwnerUserId = newOwnerMember.UserId;
                    state.OwnerSessionToken = newOwnerMember.SessionToken;
                    newOwner = newOwnerMember.UserId;
                }
            }
        }
        _userLobbyIndexByGame.TryRemove($"{gameId:N}:{sessionToken}", out _);
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
                _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "member_left", userId = validation.UserId, newOwnerUserId = newOwner }, ct);
            }
        }
        return removed;
    }

    public async Task<bool> LeaveLobbyAsync(Guid gameId, string sessionToken, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(sessionToken))
            return false;

        if (_userLobbyIndexByGame.TryGetValue($"{gameId:N}:{sessionToken}", out var lobbyId))
        {
            return await LeaveLobbyAsync(gameId, lobbyId, sessionToken, ct);
        }
        return false;
    }

    public async Task<List<Lobby>> SearchLobbiesAsync(Guid gameId, int maxRoomsToFind, Dictionary<string, string>? filters, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty)
            return new List<Lobby>();

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
        var list = new List<Lobby>();
        foreach (var state in query.OrderByDescending(l => l.CreatedAtUtc).Take(take))
        {
            list.Add(await ProjectAsync(state, null, ct));
        }
        return list;
    }

    public async Task<bool> SetIsReadyAsync(Guid gameId, string lobbyId, string sessionToken, bool isReady, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(sessionToken))
            return false;

        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            return false;

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return false;
        if (state.GameId != gameId)
            return false;
        lock (state)
        {
            if (state.Started) return false;
            var m = state.Members.FirstOrDefault(x => x.SessionToken == sessionToken);
            if (m is null) return false;
            m.IsReady = isReady;
        }
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "member_ready", userId = validation.UserId, isReady }, ct);
        return true;
    }

    public async Task<bool> SetLobbyDataAsync(Guid gameId, string lobbyId, string sessionToken, string key, string value, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(sessionToken))
            return false;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            return false;

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return false;
        if (state.GameId != gameId)
            return false;
        
        // Only owner can set lobby data
        if (state.OwnerUserId != validation.UserId)
            return false;
            
        lock (state)
        {
            var k = SanitizeString(key, PropertyKeyMaxLength);
            if (string.IsNullOrEmpty(k)) return false;
            var v = SanitizeString(value, PropertyValueMaxLength);
            if (!state.Properties.ContainsKey(k) && state.Properties.Count >= MaxPropertyCount)
                return false;

            state.Properties[k] = v;
            if (string.Equals(k, "Name", StringComparison.OrdinalIgnoreCase))
                state.Name = SanitizeString(v, NameMaxLength);
        }
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "lobby_data", key, value }, ct);
        return true;
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
                SessionToken = m.SessionToken,
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                IsReady = m.IsReady
            }).ToList());
        }
    }

    

    public async Task<bool> SetLobbyStartedAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(sessionToken))
            return false;

        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            return false;

        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return false;
        if (state.GameId != gameId)
            return false;
        
        // Only owner can start lobby
        if (state.OwnerUserId != validation.UserId)
            return false;
            
        if (state.Started) return false;
        state.Started = true;
        _ = _events.BroadcastAsync(gameId, lobbyId, new { type = "lobby_started" }, ct);
        return true;
    }

    public async Task<Lobby?> GetLobbyAsync(Guid gameId, string lobbyId, string sessionToken, CancellationToken ct = default)
    {
        if (gameId == Guid.Empty || IsInvalidId(lobbyId) || IsInvalidId(sessionToken))
            return null;
        
        var validation = await _authService.ValidateTokenAsync(sessionToken, ct);
        if (!validation.IsValid)
            return null;
            
        if (!_lobbies.TryGetValue(lobbyId, out var state))
            return null;
        if (state.GameId != gameId)
            return null;
        
        // Check if user is member of this lobby
        if (!state.Members.Any(m => m.SessionToken == sessionToken))
            return null;
            
        return await ProjectAsync(state, sessionToken, ct);
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
                    players[m.UserId] = new LobbyUser { SessionToken = m.SessionToken, UserId = m.UserId, DisplayName = m.DisplayName, IsReady = m.IsReady };
                }
            }
        }
        return Task.FromResult(players.Values.ToList());
    }
}
