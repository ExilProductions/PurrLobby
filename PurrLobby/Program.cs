using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi.Models;
using PurrLobby.Services;
using PurrLobby.Models;

// boot app
var builder = WebApplication.CreateBuilder(args);

// problem details
builder.Services.AddProblemDetails();

// basic http logging
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestScheme | HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode;
});

// compression brotli gzip
builder.Services.AddResponseCompression(o =>
{
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});

// rate limit per ip
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
});

// trust proxy headers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// swagger setup
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "PurrLobby API",
        Version = "v1",
        Description = "PurrLobby is a lightweight lobby service. Many endpoints require a 'gameId' cookie to scope requests to your game. Obtain it by calling POST /session/game.",
        Contact = new OpenApiContact { Name = "PurrLobby", Url = new Uri("https://purrlobby.exil.dev") }
    });

    o.AddSecurityDefinition("gameIdCookie", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Cookie,
        Name = "gameId",
        Description = "Game scope cookie set by POST /session/game."
    });

    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "gameIdCookie" }
            },
            Array.Empty<string>()
        }
    });
});

// service singletons
builder.Services.AddSingleton<ILobbyEventHub, LobbyEventHub>();
builder.Services.AddSingleton<ILobbyService, LobbyService>();

var app = builder.Build();

// prod vs dev
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}
app.UseHttpsRedirection();

// middleware order
app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseHttpLogging();
app.UseRateLimiter();
app.UseWebSockets();

// static files
app.UseDefaultFiles();
app.UseStaticFiles();

// swagger ui
app.UseSwagger();
app.UseSwaggerUI();

// set gameId cookie
app.MapPost("/session/game", (HttpContext http, SetGameRequest req) =>
{
    var gameId = req.GameId;
    if (gameId == Guid.Empty)
        return Results.BadRequest("Invalid GameId");

    var opts = new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Expires = DateTimeOffset.UtcNow.AddDays(7),
        Domain = "purrlobby.exil.dev"
    };
    http.Response.Cookies.Append("gameId", gameId.ToString(), opts);
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

// cookie helper
static bool TryGetGameIdFromCookie(HttpRequest request, out Guid gameId)
{
    gameId = Guid.Empty;
    if (!request.Cookies.TryGetValue("gameId", out var v)) return false;
    return Guid.TryParse(v, out gameId);
}

// create lobby
app.MapPost("/lobbies", async (HttpContext http, ILobbyService service, CreateLobbyRequest req, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    if (req.MaxPlayers <= 0) return Results.BadRequest("MaxPlayers must be > 0");
    var lobby = await service.CreateLobbyAsync(gameId, req.OwnerUserId, req.OwnerDisplayName, req.MaxPlayers, req.Properties, ct);
    return Results.Ok(lobby);
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Create a lobby";
    op.Description = "Creates a new lobby for the current game (scoped by the 'gameId' cookie). The creator is added as the owner and first member.";
    return op;
})
.Accepts<CreateLobbyRequest>("application/json")
.Produces<Lobby>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// join lobby
app.MapPost("/lobbies/{lobbyId}/join", async (HttpContext http, ILobbyService service, string lobbyId, JoinLobbyRequest req, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    var lobby = await service.JoinLobbyAsync(gameId, lobbyId, req.UserId, req.DisplayName, ct);
    return lobby is null ? Results.NotFound() : Results.Ok(lobby);
}).WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Join a lobby";
    op.Description = "Adds the user to the specified lobby if it belongs to the current game and has capacity.";
    return op;
})
.Accepts<JoinLobbyRequest>("application/json")
.Produces<Lobby>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// leave lobby by id
app.MapPost("/lobbies/{lobbyId}/leave", async (HttpContext http, ILobbyService service, string lobbyId, LeaveLobbyRequest req, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    var ok = await service.LeaveLobbyAsync(gameId, lobbyId, req.UserId, ct);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Leave a lobby";
    op.Description = "Removes the user from the lobby. If the owner leaves, ownership transfers to the first remaining member. Empty lobbies are deleted.";
    return op;
})
.Accepts<LeaveLobbyRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// leave any lobby by user id
app.MapPost("/users/{userId}/leave", async (HttpContext http, ILobbyService service, string userId, CancellationToken ct) =>
{
    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");
    var ok = await service.LeaveLobbyAsync(gameId, userId, ct);
    return ok ? Results.Ok() : Results.NotFound();
}).WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Force a user to leave their lobby";
    op.Description = "Removes the user from whichever lobby they are currently in for the current game.";
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
    op.Description = "Finds joinable lobbies for the current game. Excludes started or full lobbies.";
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
    op.Description = "Returns current members of the lobby in the current game, including readiness state.";
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
    var ok = await service.SetLobbyDataAsync(gameId, lobbyId, req.Key, req.Value, ct);
    return ok ? Results.Ok() : Results.NotFound();
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Set a lobby data value";
    op.Description = "Sets or updates a single property on the lobby within the current game. Broadcasts a lobby_data event to subscribers.";
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
    if (string.IsNullOrWhiteSpace(req.UserId)) return Results.BadRequest("Id is required");
    var ok = await service.SetIsReadyAsync(gameId, lobbyId, req.UserId, req.IsReady, ct);
    return ok ? Results.Ok() : Results.NotFound();
})
.WithTags("Lobbies")
.WithOpenApi(op =>
{
    op.Summary = "Set member ready state";
    op.Description = "Sets the readiness of a member in the specified lobby within the current game. Broadcasts a member_ready event to subscribers.";
    return op;
})
.Accepts<ReadyRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status400BadRequest);

// lobby websocket
app.Map("/ws/lobbies/{lobbyId}", async (HttpContext http, string lobbyId, ILobbyEventHub hub, CancellationToken ct) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
        return Results.BadRequest("Expected WebSocket");

    if (!TryGetGameIdFromCookie(http.Request, out var gameId))
        return Results.BadRequest("Missing or invalid gameId cookie");

    var userId = http.Request.Query["userId"].ToString();
    if (string.IsNullOrWhiteSpace(userId))
        return Results.BadRequest("Missing userId query parameter");

    using var socket = await http.WebSockets.AcceptWebSocketAsync();
    await hub.HandleConnectionAsync(gameId, lobbyId, userId, socket, ct);
    return Results.Empty;
}).WithTags("Lobbies").WithOpenApi(op =>
{
    op.Summary = "Lobby Websocket";
    op.Description = "The Websocket that is used to recive lobby specifc updates";
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
public record CreateLobbyRequest(string OwnerUserId, string OwnerDisplayName, int MaxPlayers, Dictionary<string, string>? Properties);
public record JoinLobbyRequest(string UserId, string DisplayName);
public record LeaveLobbyRequest(string UserId);
public record ReadyRequest(string UserId, bool IsReady);
public record LobbyDataRequest(string Key, string Value);
