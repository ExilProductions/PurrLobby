using System.Net;
using System.Text.Json;
using PurrLobby.Models;
using PurrLobby.Services;

namespace PurrLobby.Middleware;

public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthenticationService _authService;
    private static readonly string[] ExcludedPaths = 
    {
        "/auth/create",
        "/health",
        "/metrics",
        "/ws/"
    };

    public AuthenticationMiddleware(RequestDelegate next, IAuthenticationService authService)
    {
        _next = next;
        _authService = authService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        
        // Skip authentication for excluded paths
        if (ExcludedPaths.Any(excluded => path.StartsWith(excluded)))
        {
            await _next(context);
            return;
        }

        // Extract token from Authorization header or query parameter
        var token = ExtractToken(context);
        
        if (string.IsNullOrEmpty(token))
        {
            await WriteUnauthorizedResponse(context, "Authentication token required");
            return;
        }

        var validation = await _authService.ValidateTokenAsync(token);
        if (!validation.IsValid)
        {
            await WriteUnauthorizedResponse(context, validation.ErrorMessage ?? "Invalid token");
            return;
        }

        // Add user info to context for downstream handlers
        context.Items["User"] = new AuthenticatedUser
        {
            UserId = validation.UserId!,
            DisplayName = validation.DisplayName!,
            SessionToken = token
        };

        await _next(context);
    }

    private static string? ExtractToken(HttpContext context)
    {
        // Try Authorization header first
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
        {
            return authHeader["Bearer ".Length..];
        }

        // Fall back to query parameter
        return context.Request.Query["token"].FirstOrDefault();
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context, string message)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.ContentType = "application/json";
        
        var response = new { error = "Unauthorized", message };
        var json = JsonSerializer.Serialize(response);
        
        await context.Response.WriteAsync(json);
    }
}

public static class AuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseAuthentication(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AuthenticationMiddleware>();
    }
}