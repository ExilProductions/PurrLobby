using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PurrLobby.Models;

namespace PurrLobby.Services;

public interface IAuthenticationService
{
    Task<UserSession> CreateSessionAsync(string userId, string displayName, CancellationToken ct = default);
    Task<TokenValidationResult> ValidateTokenAsync(string token, CancellationToken ct = default);
    Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default);
    Task<bool> ExtendSessionAsync(string token, CancellationToken ct = default);
    Task CleanupExpiredSessionsAsync(CancellationToken ct = default);
}

public class AuthenticationService : IAuthenticationService
{
    private readonly ConcurrentDictionary<string, UserSession> _activeSessions = new();
    private readonly Timer _cleanupTimer;

    public AuthenticationService()
    {
        // Start cleanup timer to run every hour
        _cleanupTimer = new Timer(async _ => await CleanupExpiredSessionsAsync(), 
            null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public Task<UserSession> CreateSessionAsync(string userId, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > SecurityConstants.MaxUserIdLength)
            throw new ArgumentException("Invalid user ID", nameof(userId));
        
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name cannot be empty", nameof(displayName));

        var sanitizedDisplayName = SanitizeString(displayName, SecurityConstants.MaxDisplayNameLength);
        var sessionToken = GenerateSecureToken();
        var keyPair = GenerateKeyPair();
        
        var session = new UserSession
        {
            SessionToken = sessionToken,
            UserId = userId,
            DisplayName = sanitizedDisplayName,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(SecurityConstants.SessionExpirationHours),
            PublicKey = keyPair.Public
        };

        _activeSessions[sessionToken] = session;
        
        return Task.FromResult(session);
    }

    public Task<TokenValidationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(new TokenValidationResult 
            { 
                IsValid = false, 
                ErrorMessage = "Token is required" 
            });

        if (!_activeSessions.TryGetValue(token, out var session))
            return Task.FromResult(new TokenValidationResult 
            { 
                IsValid = false, 
                ErrorMessage = "Invalid token" 
            });

        if (DateTime.UtcNow > session.ExpiresAtUtc)
        {
            _activeSessions.TryRemove(token, out _);
            return Task.FromResult(new TokenValidationResult 
            { 
                IsValid = false, 
                ErrorMessage = "Token expired" 
            });
        }

        return Task.FromResult(new TokenValidationResult
        {
            IsValid = true,
            UserId = session.UserId,
            DisplayName = session.DisplayName
        });
    }

    public Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(false);

        return Task.FromResult(_activeSessions.TryRemove(token, out _));
    }

    public Task<bool> ExtendSessionAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(false);

        if (_activeSessions.TryGetValue(token, out var session))
        {
            var updatedSession = session with 
            { 
                ExpiresAtUtc = DateTime.UtcNow.AddHours(SecurityConstants.SessionExpirationHours) 
            };
            _activeSessions[token] = updatedSession;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task CleanupExpiredSessionsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expiredTokens = new List<string>();

        foreach (var kvp in _activeSessions)
        {
            if (now > kvp.Value.ExpiresAtUtc)
            {
                expiredTokens.Add(kvp.Key);
            }
        }

        foreach (var token in expiredTokens)
        {
            _activeSessions.TryRemove(token, out _);
        }

        await Task.CompletedTask;
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecurityConstants.TokenLength);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    private static (string Public, string Private) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey());
        var privateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
        return (publicKey, privateKey);
    }

    private static string SanitizeString(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var trimmed = input.Trim();
        if (trimmed.Length > maxLength)
            trimmed = trimmed.Substring(0, maxLength);

        // Remove potentially harmful characters
        var sb = new StringBuilder();
        foreach (var c in trimmed)
        {
            if (char.IsControl(c) && c != '\t' && c != '\n' && c != '\r')
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}