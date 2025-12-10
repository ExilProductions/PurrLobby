using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi.Models;
using PurrLobby.Services;
using PurrLobby.Models;


var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestScheme | HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode;
});
builder.Services.AddResponseCompression(o =>
{
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 100
        });
    });
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsync("Woah there partner calm down, you sending to much info :O", ct);
    };
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PurrLobby API",
        Version = "v1",
        Description = "PurrLobby is a secure lightweight lobby service with token-based authentication. All lobby endpoints require a valid session token and gameId cookie.",
        Contact = new OpenApiContact { Name = "PurrLobby", Url = new Uri("https://purrlobby.exil.dev") }
    });

    o.AddSecurityDefinition("gameIdCookie", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Cookie,
        Name = "gameId",
        Description = "Game scope cookie set by POST /session/game."
    });

    o.AddSecurityDefinition("bearerAuth", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Secure session token obtained from POST /auth/create. Include in Authorization header as 'Bearer <token>' or as 'token' query parameter."
    });

    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "gameIdCookie" }
            },
            Array.Empty<string>()
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "bearerAuth" }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();
builder.Services.AddSingleton<ILobbyEventHub, LobbyEventHub>();
builder.Services.AddSingleton<ILobbyService, LobbyService>();
builder.Services.AddRazorPages(); // Register Razor Pages services
var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
    app.UseHttpsRedirection();
}
else
{
    app.UseDeveloperExceptionPage();
}
app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseHttpLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseWebSockets();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.MapRazorPages(); // Enable Razor Pages

// Authentication endpoints
app.MapPost("/auth/create", async (IAuthenticationService authService, CreateSessionRequest req, CancellationToken ct) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.DisplayName))
            return Results.BadRequest(new { error = "UserId and DisplayName are required" });

        var session = await authService.CreateSessionAsync(req.UserId, req.DisplayName, ct);
        return Results.Ok(session);
    }
    catch (ArgumentException ax)
    {
        return Results.BadRequest(new { error = ax.Message });
    }
    catch
    {
        return Results.Problem("Internal server error", statusCode: 500);
    }
})
.WithTags("Authentication")
.WithOpenApi(op =>
{
    op.Summary = "Create authentication session";
    op.Description = "Creates a secure session token for a user. Returns a session token that must be used for all subsequent API calls.";
    op.Security.Clear();
    return op;
})
.Accepts<CreateSessionRequest>("application/json")
.Produces<UserSession>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status500InternalServerError);

app.MapPost("/auth/validate", async (IAuthenticationService authService, ValidateTokenRequest req, CancellationToken ct) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return Results.BadRequest(new { error = "Token is required" });

        var result = await authService.ValidateTokenAsync(req.Token, ct);
        return Results.Ok(result);
    }
    catch
    {
        return Results.Problem("Internal server error", statusCode: 500);
    }
})
.WithTags("Authentication")
.WithOpenApi(op =>
{
    op.Summary = "Validate session token";
    op.Description = "Validates a session token and returns user information if valid.";
    op.Security.Clear();
    return op;
})
.Accepts<ValidateTokenRequest>("application/json")
.Produces<TokenValidationResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status500InternalServerError);

app.MapPost("/auth/revoke", async (IAuthenticationService authService, RevokeTokenRequest req, CancellationToken ct) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return Results.BadRequest(new { error = "Token is required" });

        var result = await authService.RevokeTokenAsync(req.Token, ct);
        return Results.Ok(result);
    }
    catch
    {
        return Results.Problem("Internal server error", statusCode: 500);
    }
})
.WithTags("Authentication")
.WithOpenApi(op =>
{
    op.Summary = "Revoke session token";
    op.Description = "Revokes a session token, making it invalid for future use.";
    op.Security.Clear();
    return op;
})
.Accepts<RevokeTokenRequest>("application/json")
.Produces<bool>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status500InternalServerError);

static CookieOptions BuildStdCookieOptions() => new()
{
    HttpOnly = true,
    Secure = true,
    SameSite = SameSiteMode.Lax,
    IsEssential = true,
    Expires = DateTimeOffset.UtcNow.AddDays(7),
    Domain = "purrlobby.exil.dev"
};

// set / update gameId cookie
app.MapPost("/session/game", (HttpContext http, SetGameRequest req) =>
{
    var gameId = req.GameId;
    if (gameId == Guid.Empty)
        return Results.BadRequest("Invalid GameId");
    http.Response.Cookies.Append("gameId", gameId.ToString(), BuildStdCookieOptions());
    return Results.Ok(new { message = "GameId stored" });
})
.WithTags("Session")
.WithOpenApi(op =>
{
    op.Summary = "Identify game by setting a cookie";
    op.Description = "Sets a 'gameId' cookie used by lobby endpoints. Provide your game GUID in the request body.";
    op.Security.Clear();
    return op;
})
.Accepts<SetGameRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// create / ensure userId cookie
app.MapPost("/session/user", (HttpContext http) =>
{
    if (!http.Request.Cookies.TryGetValue("userId", out var existing) || string.IsNullOrWhiteSpace(existing))
    {
        existing = Guid.NewGuid().ToString("N");
        http.Response.Cookies.Append("userId", existing, BuildStdCookieOptions());
    }
    return Results.Ok(new { userId = existing });
})
.WithTags("Session")
.WithOpenApi(op =>
{
    op.Summary = "Ensure a server-generated user id";
    op.Description = "Creates (if absent) and returns the server-generated 'userId' cookie used to identify the player. No input required.";
    op.Security.Clear();
    return op;
})
.Produces(StatusCodes.Status200OK);

// cookie helpers
static bool TryGetGameIdFromCookie(HttpRequest request, out Guid gameId)
{
    gameId = Guid.Empty;
    if (!request.Cookies.TryGetValue("gameId", out var v)) return false;
    return Guid.TryParse(v, out gameId);
}

// WebSocket token extraction helper
static string? ExtractTokenForWebSocket(HttpContext context)
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



// create lobby
app.MapPost("/lobbies", async (HttpContext http, ILobbyService service, CreateLobbyRequest req, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    if (req.MaxPlayers <= 0) return Results.BadRequest("MaxPlayers must be > 0");
    
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    try
    {
        var lobby = await service.CreateLobbyAsync(gameId, user.SessionToken, req.MaxPlayers, req.Properties, ct);
        return Results.Ok(lobby);
    }
    catch (ArgumentException ax)
    {
        return Results.BadRequest(ax.Message);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Create a lobby";
    op.Description = "Creates a new lobby for the current game. Requires valid session token for authentication.";
    return op;
})
.Accepts<CreateLobbyRequest>("application/json")
.Produces<Lobby>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// join lobby
app.MapPost("/lobbies/{lobbyId}/join", async (HttpContext http, ILobbyService service, string lobbyId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var lobby = await service.JoinLobbyAsync(gameId, lobbyId, user.SessionToken, ct);
    return lobby is null ? Results.NotFound() : Results.Ok(lobby);
}).WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Join a lobby";
    op.Description = "Adds the authenticated user to the specified lobby. Session token is automatically extracted from request.";
    return op;
})

.Produces<Lobby>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// leave lobby by id
app.MapPost("/lobbies/{lobbyId}/leave", async (HttpContext http, ILobbyService service, string lobbyId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var ok = await service.LeaveLobbyAsync(gameId, lobbyId, user.SessionToken, ct);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Leave a lobby";
    op.Description = "Removes the authenticated user from the lobby.";
    return op;
})
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);



// search lobbies
app.MapGet("/lobbies/search", async (HttpContext http, ILobbyService service, int maxRoomsToFind = 10, CancellationToken ct = default) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    var lobbies = await service.SearchLobbiesAsync(gameId, maxRoomsToFind, null, ct);
    return Results.Ok(lobbies);
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Search available lobbies";
    op.Description = "Finds joinable lobbies for the current game. Excludes started or full lobbies. Does not require authentication.";
    op.Parameters.Add(new OpenApiParameter
    {
        Name = "maxRoomsToFind",
        In = ParameterLocation.Query,
        Required = false,
        Description = "Max rooms to return (default 10)",
        Schema = new OpenApiSchema { Type = "integer", Format = "int32" }
    });
    return op;
})
.Produces<List<Lobby>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// get lobby by id
app.MapGet("/lobbies/{lobbyId}", async (HttpContext http, ILobbyService service, string lobbyId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var lobby = await service.GetLobbyAsync(gameId, lobbyId, user.SessionToken, ct);
    return lobby is null ? Results.NotFound() : Results.Ok(lobby);
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Get lobby details";
    op.Description = "Returns lobby object including ownership flag relative to the authenticated user.";
    return op;
})
.Produces<Lobby>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest);

// lobby members
app.MapGet("/lobbies/{lobbyId}/members", async (HttpContext http, ILobbyService service, string lobbyId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    var members = await service.GetLobbyMembersAsync(gameId, lobbyId, ct);
    return members.Count == 0 ? Results.NotFound() : Results.Ok(members);
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Get members of a lobby";
    op.Description = "Returns current members of lobby in the current game, including readiness state. Requires authentication and membership in the lobby.";
    return op;
})
.Produces<List<LobbyUser>>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// lobby data get
app.MapGet("/lobbies/{lobbyId}/data/{key}", async (HttpContext http, ILobbyService service, string lobbyId, string key, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    var v = await service.GetLobbyDataAsync(gameId, lobbyId, key, ct);
    return v is null ? Results.NotFound() : Results.Ok(v);
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Get a lobby data value";
    op.Description = "Retrieves a single property value for the lobby within the current game.";
    return op;
})
.Produces<string>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// lobby data set
app.MapPost("/lobbies/{lobbyId}/data", async (HttpContext http, ILobbyService service, string lobbyId, LobbyDataRequest req, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    if (string.IsNullOrWhiteSpace(req.Key)) return Results.BadRequest("Key is required");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var ok = await service.SetLobbyDataAsync(gameId, lobbyId, user.SessionToken, req.Key, req.Value, ct);
    return ok ? Results.Ok() : Results.NotFound();
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Set a lobby data value";
    op.Description = "Sets or updates a single property on lobby within the current game. Only lobby owners can set data. Broadcasts a lobby_data event to subscribers.";
    return op;
})
.Accepts<LobbyDataRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// ready toggle
app.MapPost("/lobbies/{lobbyId}/ready", async (HttpContext http, ILobbyService service, string lobbyId, ReadyRequest req, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var ok = await service.SetIsReadyAsync(gameId, lobbyId, user.SessionToken, req.IsReady, ct);
    return ok ? Results.Ok() : Results.NotFound();
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Set member ready state";
    op.Description = "Sets readiness for the authenticated user in the lobby. Request body must include isReady. Broadcasts a member_ready event.";
    return op;
})
.Accepts<ReadyRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// set everyone ready
app.MapPost("/lobbies/{lobbyId}/ready-all", async (HttpContext http, ILobbyService service, string lobbyId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var ok = await service.SetEveryoneReadyAsync(gameId, lobbyId, user.SessionToken, ct);
    return ok ? Results.Ok() : Results.NotFound();
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Set all members ready";
    op.Description = "Owner only. Sets all lobby members as ready. Broadcastes an everyone_ready event with affected members list.";
    return op;
})
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// start lobby
app.MapPost("/lobbies/{lobbyId}/start", async (HttpContext http, ILobbyService service, string lobbyId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
        
    var user = http.Items["User"] as AuthenticatedUser;
    if (user == null)
        return Results.Unauthorized();
        
    var ok = await service.SetLobbyStartedAsync(gameId, lobbyId, user.SessionToken, ct);
    return ok ? Results.Ok() : Results.NotFound();
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Start lobby";
    op.Description = "Owner only. Marks lobby as started and broadcasts lobby_started. Only lobby owners can start lobbies.";
    return op;
})
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest);

// lobby websocket
app.Map("/ws/lobbies/{lobbyId}", async (HttpContext http, string lobbyId, ILobbyEventHub hub, IAuthenticationService authService, CancellationToken ct) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
        return Results.BadRequest("Expected WebSocket");

    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");

    // Extract token from Authorization header or query parameter for WebSocket
    var token = ExtractTokenForWebSocket(http);
    if (string.IsNullOrEmpty(token))
        return Results.Unauthorized();

    var validation = await authService.ValidateTokenAsync(token, ct);
    if (!validation.IsValid)
        return Results.Unauthorized();

    using var socket = await http.WebSockets.AcceptWebSocketAsync();
    await hub.HandleConnectionAsync(gameId, lobbyId, token, socket, ct);
    return Results.Empty;
}).WithTags("Lobbies").WithOpenApi(op =>
{
    op.Summary = "Lobby Websocket";
    op.Description = @"WebSocket for real-time lobby-specific updates. Requires valid session token provided via Authorization header or 'token' query parameter.

Authentication: 
- Bearer token in Authorization header: 'Authorization: Bearer <token>'
- Or token as query parameter: '?token=<token>'

WebSocket Events:
- lobby_created: New lobby created
- member_joined: User joined the lobby  
- member_left: User left the lobby
- member_ready: User ready state changed
- everyone_ready: Owner set all members as ready (includes affectedMembers array)
- lobby_data: Lobby property updated
- lobby_started: Lobby started by owner
- lobby_empty: Lobby closed due to no members
- lobby_deleted: Lobby forcefully closed
- ping: Server heartbeat (respond with 'pong')

Connection Requirements:
- Valid gameId cookie must be set
- Valid session token required
- User must be member of the lobby";
    return op;
});

// health
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithTags("Health")
   .WithOpenApi(op =>
   {
       op.Summary = "Service health";
       op.Description = "Returns a 200 response to indicate the service is running.";
       op.Security.Clear();
       return op;
   })
   .Produces(StatusCodes.Status200OK);

// stats
app.MapGet("/stats/global/players", async (ILobbyService service, CancellationToken ct) => Results.Ok(await service.GetGlobalPlayerCountAsync(ct)))
    .WithTags("Stats").WithSummary("Get total active players globally").WithDescription("Counts all players across all lobbies (all games).")
    .WithOpenApi(op => { op.Security.Clear(); return op; })
    .Produces<int>(StatusCodes.Status200OK);
app.MapGet("/stats/global/lobbies", async (ILobbyService service, CancellationToken ct) => Results.Ok(await service.GetGlobalLobbyCountAsync(ct)))
    .WithTags("Stats").WithSummary("Get total lobbies globally").WithDescription("Counts all lobbies across all games, including started ones.")
    .WithOpenApi(op => { op.Security.Clear(); return op; })
    .Produces<int>(StatusCodes.Status200OK);
app.MapGet("/stats/{gameId:guid}/lobbies", async (ILobbyService service, Guid gameId, CancellationToken ct) => Results.Ok(await service.GetLobbyCountByGameAsync(gameId, ct)))
    .WithTags("Stats").WithSummary("Get lobby count for a game").WithDescription("Counts all lobbies for the specified game.")
    .WithOpenApi()
    .Produces<int>(StatusCodes.Status200OK);
app.MapGet("/stats/{gameId:guid}/players", async (ILobbyService service, Guid gameId, CancellationToken ct) => Results.Ok(await service.GetActivePlayersByGameAsync(gameId, ct)))
    .WithTags("Stats").WithSummary("Get active players for a game").WithDescription("Returns distinct active players across all lobbies for the specified game.")
    .WithOpenApi()
    .Produces<List<LobbyUser>>(StatusCodes.Status200OK);

// run
app.Run();

// dto records
public record SetGameRequest(Guid GameId);
public record CreateLobbyRequest(string OwnerDisplayName, int MaxPlayers, Dictionary<string, string>? Properties);
public record ReadyRequest(bool IsReady);
public record LobbyDataRequest(string Key, string Value);

// Authentication DTOs
public record CreateSessionRequest(string UserId, string DisplayName);
public record ValidateTokenRequest(string Token);
public record RevokeTokenRequest(string Token);