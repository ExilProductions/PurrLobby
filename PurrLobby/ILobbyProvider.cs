using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Newtonsoft.Json;
using WebSocketSharp;
using PurrNet.Logging;
using PurrLobby;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PurrLobby.Providers
{
    public class PurrLobbyProvider : MonoBehaviour, ILobbyProvider
    {
        [Header("API Configuration")]
        public string apiBaseUrl = "https://purrlobby.exil.dev";
        public string wsBaseUrl = "wss://purrlobby.exil.dev";
        public float requestTimeout = 10f;

        [Tooltip("Must be a valid GUID that identifies your game")]
        public string gameId = "";

        [Header("Local Player")]
        public string playerName = "Player";

        private string localUserId;
        private Lobby? currentLobby;
        private string gameCookie;
        private WebSocket ws;

        // ---- Unity Events ----
        public event UnityAction<string> OnLobbyJoinFailed;
        public event UnityAction OnLobbyLeft;
        public event UnityAction<Lobby> OnLobbyUpdated;
        public event UnityAction<List<LobbyUser>> OnLobbyPlayerListUpdated;
        public event UnityAction<List<FriendUser>> OnFriendListPulled;
        public event UnityAction<string> OnError;

        // ---- Initialization ----
        public async Task InitializeAsync()
        {
            if (string.IsNullOrEmpty(localUserId))
                localUserId = Guid.NewGuid().ToString();

            if (string.IsNullOrEmpty(gameId) || !Guid.TryParse(gameId, out _))
            {
                OnError?.Invoke("Invalid Game ID. Please set a valid GUID.");
                return;
            }

            var req = new SetGameRequest { GameId = Guid.Parse(gameId) };
            var resp = await PostRequestRaw("/session/game", req, includeCookie: false);

            if (!resp.success)
            {
                OnError?.Invoke($"Failed to start session: {resp.error}");
                return;
            }

            if (resp.headers.TryGetValue("SET-COOKIE", out string cookieHeader))
                gameCookie = cookieHeader.Split(';')[0].Trim();
            else
            {
                OnError?.Invoke("Server did not return a gameId cookie.");
                return;
            }

            PurrLogger.Log("PurrLobbyProvider initialized with session cookie");
        }

        public void Shutdown() => CloseWebSocket();

        // ---- Lobby API ----
        public async Task<Lobby> CreateLobbyAsync(int maxPlayers, Dictionary<string, string> lobbyProperties = null)
        {
            var request = new CreateLobbyRequest
            {
                OwnerUserId = localUserId,
                OwnerDisplayName = playerName,
                MaxPlayers = maxPlayers,
                Properties = lobbyProperties
            };

            var response = await PostRequest<ServerLobby>("/lobbies", request);
            if (response != null)
            {
                currentLobby = ConvertServerLobbyToClientLobby(response);
                OpenWebSocket(currentLobby.Value.LobbyId);
                return currentLobby.Value;
            }

            OnError?.Invoke("Failed to create lobby");
            return new Lobby { IsValid = false };
        }

        public async Task<Lobby> JoinLobbyAsync(string lobbyId)
        {
            var request = new JoinLobbyRequest { UserId = localUserId, DisplayName = playerName };
            var response = await PostRequest<ServerLobby>($"/lobbies/{lobbyId}/join", request);
            if (response != null)
            {
                currentLobby = ConvertServerLobbyToClientLobby(response);
                OpenWebSocket(lobbyId);
                return currentLobby.Value;
            }

            OnLobbyJoinFailed?.Invoke($"Failed to join lobby {lobbyId}");
            return new Lobby { IsValid = false };
        }

        public async Task LeaveLobbyAsync() => currentLobby.HasValue ? await LeaveLobbyAsync(currentLobby.Value.LobbyId) : Task.CompletedTask;

        public async Task LeaveLobbyAsync(string lobbyId)
        {
            var request = new LeaveLobbyRequest { UserId = localUserId };
            var success = await PostRequest<bool>($"/lobbies/{lobbyId}/leave", request);
            if (!success) await PostRequest<bool>($"/users/{localUserId}/leave", null);

            CloseWebSocket();
            currentLobby = null;
            OnLobbyLeft?.Invoke();
        }

        public async Task<List<Lobby>> SearchLobbiesAsync(int maxRoomsToFind = 10, Dictionary<string, string> filters = null)
        {
            var response = await GetRequest<List<ServerLobby>>($"/lobbies/search?maxRoomsToFind={maxRoomsToFind}");
            var result = new List<Lobby>();
            if (response != null)
                response.ForEach(s => result.Add(ConvertServerLobbyToClientLobby(s)));
            return result;
        }

        public async Task SetIsReadyAsync(string userId, bool isReady)
        {
            if (!currentLobby.HasValue) return;
            await PostRequest($"/lobbies/{currentLobby.Value.LobbyId}/ready", new ReadyRequest { UserId = userId, IsReady = isReady });
        }

        public async Task SetLobbyDataAsync(string key, string value)
        {
            if (!currentLobby.HasValue) return;
            await PostRequest($"/lobbies/{currentLobby.Value.LobbyId}/data", new LobbyDataRequest { Key = key, Value = value });
        }

        public async Task<string> GetLobbyDataAsync(string key)
        {
            if (!currentLobby.HasValue) return string.Empty;
            var response = await GetRequest<string>($"/lobbies/{currentLobby.Value.LobbyId}/data/{key}");
            return response ?? string.Empty;
        }

        public async Task<List<LobbyUser>> GetLobbyMembersAsync()
        {
            if (!currentLobby.HasValue) return new List<LobbyUser>();
            var response = await GetRequest<List<ServerLobbyUser>>($"/lobbies/{currentLobby.Value.LobbyId}/members");

            var list = new List<LobbyUser>();
            if (response != null)
                response.ForEach(s => list.Add(new LobbyUser { Id = s.UserId, DisplayName = s.DisplayName, IsReady = s.IsReady }));
            return list;
        }

        public Task<string> GetLocalUserIdAsync() => Task.FromResult(localUserId);

        public async Task SetAllReadyAsync()
        {
            if (!currentLobby.HasValue) return;
            await PostRequest($"/lobbies/{currentLobby.Value.LobbyId}/ready/all", null);
        }

        public async Task SetLobbyStartedAsync()
        {
            if (!currentLobby.HasValue) return;
            await PostRequest($"/lobbies/{currentLobby.Value.LobbyId}/started", null);
        }

        public Task<List<FriendUser>> GetFriendsAsync(LobbyManager.FriendFilter filter)
        {
            return Task.FromResult(new List<FriendUser>());
        }

        public Task InviteFriendAsync(FriendUser user)
        {
            return Task.CompletedTask;
        }

        // ---- WebSocket ----
        private void OpenWebSocket(string lobbyId)
        {
            CloseWebSocket();
            var wsUrl = $"{wsBaseUrl}/ws/lobbies/{lobbyId}";
            ws = new WebSocket(wsUrl);
            if (!string.IsNullOrEmpty(gameCookie)) ws.SetCookie(new WebSocketSharp.Net.Cookie("gameId", gameId, "/"));

            ws.OnOpen += (s, e) => PurrLogger.Log($"WebSocket connected to {lobbyId}");
            ws.OnMessage += (s, e) =>
            {
                try
                {
                    var msg = JsonConvert.DeserializeObject<LobbyWebSocketMessage>(e.Data);
                    HandleWebSocketMessage(msg);
                }
                catch { }
            };
            ws.OnError += (s, e) => OnError?.Invoke($"WebSocket error: {e.Message}");
            ws.OnClose += (s, e) => PurrLogger.Log("WebSocket closed");

            ws.ConnectAsync();
        }

        private void CloseWebSocket()
        {
            if (ws != null)
            {
                ws.CloseAsync();
                ws = null;
            }
        }

        private void HandleWebSocketMessage(LobbyWebSocketMessage msg)
        {
            switch (msg.Type)
            {
                case "lobby.updated":
                    var lobby = JsonConvert.DeserializeObject<ServerLobby>(msg.Payload.ToString());
                    currentLobby = ConvertServerLobbyToClientLobby(lobby);
                    OnLobbyUpdated?.Invoke(currentLobby.Value);
                    break;
                case "player.list":
                    var users = JsonConvert.DeserializeObject<List<LobbyUser>>(msg.Payload.ToString());
                    OnLobbyPlayerListUpdated?.Invoke(users);
                    break;
                case "error":
                    OnError?.Invoke(msg.Payload.ToString());
                    break;
            }
        }

        // ---- HTTP helpers ----
        private async Task<T> GetRequest<T>(string endpoint)
        {
            using var request = UnityWebRequest.Get(apiBaseUrl + endpoint);
            request.timeout = (int)requestTimeout;
            if (!string.IsNullOrEmpty(gameCookie)) request.SetRequestHeader("Cookie", gameCookie);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                PurrLogger.LogError($"GET {endpoint} failed: {request.error}");
                return default;
            }
            return JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
        }

        private async Task PostRequest(string endpoint, object data)
        {
            await PostRequestRaw(endpoint, data, true);
        }

        private async Task<T> PostRequest<T>(string endpoint, object data)
        {
            var resp = await PostRequestRaw(endpoint, data, true);
            return resp.success ? JsonConvert.DeserializeObject<T>(resp.body) : default;
        }

        private async Task<(bool success, string body, string error, Dictionary<string, string> headers)> PostRequestRaw(string endpoint, object data, bool includeCookie)
        {
            var json = data != null ? JsonConvert.SerializeObject(data) : "{}";
            using var request = new UnityWebRequest(apiBaseUrl + endpoint, "POST")
            {
                downloadHandler = new DownloadHandlerBuffer(),
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json))
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = (int)requestTimeout;
            if (includeCookie && !string.IsNullOrEmpty(gameCookie))
                request.SetRequestHeader("Cookie", gameCookie);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            var headers = request.GetResponseHeaders() ?? new Dictionary<string, string>();
            if (request.result != UnityWebRequest.Result.Success)
                return (false, null, request.error, headers);
            return (true, request.downloadHandler.text, null, headers);
        }

        // ---- Helpers ----
        private Lobby ConvertServerLobbyToClientLobby(ServerLobby s)
        {
            return new Lobby
            {
                LobbyId = s.LobbyId,
                MaxPlayers = s.MaxPlayers,
                Properties = s.Properties,
                IsValid = true
            };
        }

        // ---- DTOs ----
        [Serializable] private class SetGameRequest { public Guid GameId; }
        [Serializable] private class CreateLobbyRequest { public string OwnerUserId; public string OwnerDisplayName; public int MaxPlayers; public Dictionary<string, string> Properties; }
        [Serializable] private class JoinLobbyRequest { public string UserId; public string DisplayName; }
        [Serializable] private class LeaveLobbyRequest { public string UserId; }
        [Serializable] private class ReadyRequest { public string UserId; public bool IsReady; }
        [Serializable] private class LobbyDataRequest { public string Key; public string Value; }

        [Serializable]
        private class ServerLobby
        {
            public string LobbyId;
            public int MaxPlayers;
            public Dictionary<string, string> Properties;
        }

        [Serializable]
        private class ServerLobbyUser
        {
            public string UserId;
            public string DisplayName;
            public bool IsReady;
        }

        [Serializable]
        private class LobbyWebSocketMessage
        {
            public string Type;
            public object Payload;
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(PurrLobbyProvider))]
    public class PurrLobbyProviderEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var provider = (PurrLobbyProvider)target;
            if (GUILayout.Button("Generate New GameId"))
            {
                provider.gameId = Guid.NewGuid().ToString();
                EditorUtility.SetDirty(provider);
            }
        }
    }
#endif
}
