# PurrLobby

Lightweight, fast lobby service for [PurrNet](https://github.com/PurrNet/PurrNet) multiplayer games. Alternative to Steam or Unity lobby lobby services.

Dashboard: [https://purrlobby.exil.dev](https://purrlobby.exil.dev)
API docs: [https://purrlobby.exil.dev/swagger](https://purrlobby.exil.dev/swagger)

## Implementation Overview

* **Multi-game scoping**: Each game has isolated lobbies via Game ID
* **Real-time WebSocket events**: member join/leave, ready status, lobby data updates
* **Custom lobby properties**: Store arbitrary key-value data per lobby
* **Built-in stats**: Global & per-game player/lobby counts
* **Rate limiting**: Protects against abuse by default
* **Web dashboard + Swagger**: Monitor lobbies and test API

## Quick Start

```bash
git clone https://github.com/ExilProductions/PurrLobby.git
cd PurrLobby
dotnet build
dotnet run
```

## Configuration

* `ASPNETCORE_ENVIRONMENT=Production`
* `ASPNETCORE_URLS=https://localhost:7225;http://localhost:5123`
* Cookie domain (production): `Program.cs → Domain = "your-domain.com"`

## API Essentials

Set game context (required):

```bash
curl -X POST "https://your-domain.com/session/game" \
-H "Content-Type: application/json" \
-d '{"gameId":"your-game-guid"}'
```

Create a lobby:

```bash
curl -X POST "https://your-domain.com/lobbies" \
-H "Content-Type: application/json" \
-H "Cookie: gameId=your-game-guid" \
-d '{"ownerUserId":"player1","ownerDisplayName":"Player","maxPlayers":4,"properties":{"mode":"casual"}}'
```

Join/search lobbies using `/lobbies/{id}/join` and `/lobbies/search`.

**WebSocket** (live updates):

```javascript
const ws = new WebSocket(`wss://your-domain.com/ws/lobbies/${lobbyId}?userId=${userId}`);
ws.onmessage = e => console.log(JSON.parse(e.data));
```

## Deployment

**Docker**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
EXPOSE 80 443
COPY . .
ENTRYPOINT ["dotnet","PurrLobby.dll"]
```

**Reverse Proxy (nginx)**

```nginx
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Forwarded-Host $host;
```

## Contributing

Fork → branch → PR. Use tests & pass code analysis. Report issues on GitHub.

## License

MIT
