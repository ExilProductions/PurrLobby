using System.Security.Cryptography;
using System.Text;

namespace PurrLobby.Models;

public record UserSession
{
    public required string SessionToken { get; init; }
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; init; }
    public string? PublicKey { get; init; }
}

public class AuthenticatedUser
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string SessionToken { get; init; }
}

public class TokenValidationResult
{
    public bool IsValid { get; init; }
    public string? UserId { get; init; }
    public string? DisplayName { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class SecurityConstants
{
    public const int TokenLength = 64;
    public const int SessionExpirationHours = 24;
    public const int MaxDisplayNameLength = 64;
    public const int MaxUserIdLength = 128;
}