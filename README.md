# PurrLobby 🐱

A lightweight, fast, and reliable lobby service for multiplayer games. PurrLobby provides an alternative to Steam Lobby Service and Unity Lobby Service, offering real-time lobby management with WebSocket communication.

## 🌟 Features

- **Multi-Game Support**: Scope lobbies per game using unique Game IDs
- **Real-time Updates**: WebSocket-based live lobby updates and member status changes
- **Player Management**: Join/leave lobbies, ready status, custom display names
- **Custom Properties**: Store arbitrary key-value data on lobbies
- **Search & Discovery**: Find available lobbies with filtering
- **Rate Limiting**: Built-in protection against abuse (300 requests/minute per IP)
- **Statistics**: Global and per-game player/lobby statistics
- **Production Ready**: Includes compression, HTTPS, security headers, and proxy support
- **Web Interface**: Built-in dashboard for monitoring and testing
- **OpenAPI/Swagger**: Full API documentation with interactive testing

## 🚀 Quick Start

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows, macOS, or Linux

### Installation & Running

1. Clone the repository:
```bash
git clone https://github.com/ExilProductions/PurrLobby.git
cd PurrLobby
```

2. Build the project:
```bash
dotnet build
```

3. Run the service:
```bash
cd PurrLobby
dotnet run
```

4. Access the web interface: [https://localhost:7225](https://localhost:7225)
5. View API documentation: [https://localhost:7225/swagger](https://localhost:7225/swagger)

## 🔧 Configuration

### Environment Variables

Configure the service using `appsettings.json` or environment variables:

- `ASPNETCORE_ENVIRONMENT`: Set to `Production` for production deployment
- `ASPNETCORE_URLS`: Configure listening URLs (default: `https://localhost:7225;http://localhost:5123`)

### Domain Configuration

For production deployment, update the cookie domain in `Program.cs`:
```csharp
Domain = "your-domain.com"  // Line 134
```

### Rate Limiting

Default rate limits (configurable in `Program.cs`):
- **300 requests per minute** per IP address
- **100 queued requests** per IP
- Returns `429 Too Many Requests` when exceeded

## 📚 API Usage

### 1. Set Game Context

Before using lobby endpoints, set your game's GUID:

```bash
curl -X POST "https://your-domain.com/session/game" \
  -H "Content-Type: application/json" \
  -d '{"gameId": "your-game-guid-here"}'
```

This sets a secure cookie that scopes all subsequent requests to your game.

### 2. Create a Lobby

```bash
curl -X POST "https://your-domain.com/lobbies" \
  -H "Content-Type: application/json" \
  -H "Cookie: gameId=your-game-guid" \
  -d '{
    "ownerUserId": "player123",
    "ownerDisplayName": "PlayerName",
    "maxPlayers": 4,
    "properties": {
      "gameMode": "competitive",
      "map": "dust2"
    }
  }'
```

### 3. Search Lobbies

```bash
curl "https://your-domain.com/lobbies/search?maxRoomsToFind=10" \
  -H "Cookie: gameId=your-game-guid"
```

### 4. Join a Lobby

```bash
curl -X POST "https://your-domain.com/lobbies/{lobbyId}/join" \
  -H "Content-Type: application/json" \
  -H "Cookie: gameId=your-game-guid" \
  -d '{
    "userId": "player456",
    "displayName": "AnotherPlayer"
  }'
```

### 5. WebSocket Connection for Real-time Updates

Connect to lobby-specific WebSocket for live updates:

```javascript
const ws = new WebSocket(`wss://your-domain.com/ws/lobbies/${lobbyId}?userId=${userId}`);

ws.onmessage = function(event) {
    const data = JSON.parse(event.data);
    console.log('Lobby update:', data);
    // Handle events: member_joined, member_left, member_ready, lobby_data, etc.
};
```

## 🌐 Web Interface

The service includes a web dashboard at the root URL featuring:

- **Global Statistics**: Total players and lobbies across all games
- **Game-Specific Stats**: Players and lobbies for your specific game
- **Game ID Management**: Easy way to set and track your game
- **Direct API Access**: Links to Swagger documentation

## 📊 Monitoring & Statistics

### Global Statistics
- `GET /stats/global/players` - Total players across all games
- `GET /stats/global/lobbies` - Total lobbies across all games

### Game-Specific Statistics
- `GET /stats/{gameId}/players` - Active players for a specific game
- `GET /stats/{gameId}/lobbies` - Lobby count for a specific game

### Health Check
- `GET /health` - Service health status

## 🏗️ Architecture

### Components

- **Program.cs**: Main application configuration and API endpoints
- **LobbyService**: Core business logic for lobby management
- **LobbyEventHub**: WebSocket connection management and real-time events
- **Lobby Models**: Data structures for lobbies and users

### Key Features Implementation

- **Cookie-based Game Scoping**: Uses secure HTTP-only cookies for game context
- **In-Memory Storage**: Fast performance with concurrent collections
- **Event-Driven Updates**: Real-time WebSocket notifications for all lobby changes
- **Production Security**: Rate limiting, HTTPS enforcement, proxy header trust
- **Comprehensive Logging**: HTTP request logging and structured application logs

## 🚢 Deployment

### Docker (Recommended)

Create a `Dockerfile`:
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["PurrLobby/PurrLobby.csproj", "PurrLobby/"]
RUN dotnet restore "PurrLobby/PurrLobby.csproj"
COPY . .
WORKDIR "/src/PurrLobby"
RUN dotnet build "PurrLobby.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PurrLobby.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PurrLobby.dll"]
```

### Reverse Proxy Configuration

For production deployment behind a reverse proxy (nginx, Apache, etc.), ensure proper header forwarding for rate limiting and security:

```nginx
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Forwarded-Host $host;
```

## 🔌 Client Integration Examples

### Unity C#
```csharp
public async Task<bool> CreateLobby(string gameId, string userId, string displayName)
{
    var client = new HttpClient();
    
    // Set game context
    await client.PostAsync($"{baseUrl}/session/game", 
        new StringContent($"{{\"gameId\": \"{gameId}\"}}", Encoding.UTF8, "application/json"));
    
    // Create lobby
    var lobbyData = new {
        ownerUserId = userId,
        ownerDisplayName = displayName,
        maxPlayers = 4,
        properties = new { gameMode = "casual" }
    };
    
    var response = await client.PostAsync($"{baseUrl}/lobbies", 
        new StringContent(JsonUtility.ToJson(lobbyData), Encoding.UTF8, "application/json"));
    
    return response.IsSuccessStatusCode;
}
```

### JavaScript/Web
```javascript
class PurrLobbyClient {
    constructor(baseUrl, gameId) {
        this.baseUrl = baseUrl;
        this.gameId = gameId;
    }
    
    async setGameContext() {
        await fetch(`${this.baseUrl}/session/game`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'include',
            body: JSON.stringify({ gameId: this.gameId })
        });
    }
    
    async createLobby(userId, displayName, maxPlayers = 4) {
        const response = await fetch(`${this.baseUrl}/lobbies`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'include',
            body: JSON.stringify({
                ownerUserId: userId,
                ownerDisplayName: displayName,
                maxPlayers: maxPlayers,
                properties: {}
            })
        });
        return response.json();
    }
}
```

## 🛠️ Development

### Building from Source
```bash
# Clone repository
git clone https://github.com/ExilProductions/PurrLobby.git
cd PurrLobby

# Restore dependencies and build
dotnet restore
dotnet build

# Run with hot reload for development
cd PurrLobby
dotnet watch run
```

### Running Tests
```bash
dotnet test
```

### Code Analysis
The project includes code analysis rules. Fix any warnings before deploying:
```bash
dotnet build --verbosity normal
```

## 📝 API Reference

Full API documentation is available via Swagger UI when the service is running:
- **Local**: [https://localhost:7225/swagger](https://localhost:7225/swagger)
- **Production**: `https://your-domain.com/swagger`

### Authentication
Most endpoints require a `gameId` cookie set via `POST /session/game`. The cookie:
- Is HTTP-only and secure
- Expires after 7 days
- Scopes all requests to your specific game

### WebSocket Events
The lobby WebSocket (`/ws/lobbies/{lobbyId}`) broadcasts these events:
- `member_joined` - New player joined
- `member_left` - Player left lobby
- `member_ready` - Player readiness changed
- `lobby_data` - Custom lobby property updated
- `lobby_deleted` - Lobby was deleted

## 🤝 Contributing

This is an independent project by ExilProductions, not affiliated with PurrNet. 

### Issues & Support
- For bugs or feature requests, open an issue on GitHub
- For direct support, contact: **exil_s** on Discord
- Please don't contact PurrNet developers about this service

### Development Guidelines
1. Fork the repository
2. Create a feature branch
3. Make your changes with appropriate tests
4. Ensure code analysis passes
5. Submit a pull request

## ⚠️ Disclaimer

This service is independent and runs on personal infrastructure. It may experience downtime or instability. It's provided as-is for development and testing purposes. For production use, consider self-hosting or implementing additional redundancy.

## 📄 License

This project is open source. Check the repository for license details.

## 🔗 Links

- **Live Instance**: [https://purrlobby.exil.dev](https://purrlobby.exil.dev)
- **API Documentation**: [https://purrlobby.exil.dev/swagger](https://purrlobby.exil.dev/swagger)
- **GitHub Repository**: [https://github.com/ExilProductions/PurrLobby](https://github.com/ExilProductions/PurrLobby)

---

Made with 💜 by ExilProductions