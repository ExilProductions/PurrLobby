namespace PurrLobby.Models;

// user in lobby
public class LobbyUser
{
    public required string SessionToken { get; init; }
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public int userPing { get; set; }
    public bool IsReady { get; set; }
}

// lobby model
public class Lobby
{
    public string Name { get; set; } = string.Empty;
    public bool IsValid { get; set; } = true;
    public string LobbyId { get; set; } = string.Empty;
    public string LobbyCode { get; set; } = string.Empty;
    public int MaxPlayers { get; set; }
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsOwner { get; set; }
    public List<LobbyUser> Members { get; } = new();
    public object? ServerObject { get; set; }
}
