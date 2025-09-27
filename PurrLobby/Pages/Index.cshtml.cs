using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace PurrLobby.Pages
{
    public class IndexModel : PageModel
    {
        [BindProperty]
        public string GameIdInput { get; set; } = string.Empty;
        public string CookieStatus { get; set; } = string.Empty;
        public int GlobalPlayers { get; set; } = 0;
        public int GlobalLobbies { get; set; } = 0;
        public int GamePlayers { get; set; } = 0;
        public int GameLobbies { get; set; } = 0;

        public async Task OnGetAsync()
        {
            await LoadStatsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!Guid.TryParse(GameIdInput, out var gameId))
            {
                CookieStatus = "Invalid Game ID format.";
                await LoadStatsAsync();
                return Page();
            }
            // Set gameId cookie via backend API
            try
            {
                using var client = new HttpClient();
                var response = await client.PostAsync($"{Request.Scheme}://{Request.Host}/session/game", new StringContent(JsonSerializer.Serialize(new { GameId = gameId }), System.Text.Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode)
                {
                    CookieStatus = "Game ID set!";
                }
                else
                {
                    CookieStatus = "Failed to set Game ID.";
                }
            }
            catch
            {
                CookieStatus = "Error contacting backend.";
            }
            await LoadStatsAsync(gameId);
            return Page();
        }

        private async Task LoadStatsAsync(Guid? gameId = null)
        {
            using var client = new HttpClient();
            try
            {
                var globalPlayersResp = await client.GetStringAsync($"{Request.Scheme}://{Request.Host}/stats/global/players");
                GlobalPlayers = int.TryParse(globalPlayersResp, out var gp) ? gp : 0;
            }
            catch { GlobalPlayers = 0; }
            try
            {
                var globalLobbiesResp = await client.GetStringAsync($"{Request.Scheme}://{Request.Host}/stats/global/lobbies");
                GlobalLobbies = int.TryParse(globalLobbiesResp, out var gl) ? gl : 0;
            }
            catch { GlobalLobbies = 0; }
            if (gameId.HasValue)
            {
                try
                {
                    var gamePlayersResp = await client.GetStringAsync($"{Request.Scheme}://{Request.Host}/stats/{gameId}/players");
                    GamePlayers = int.TryParse(gamePlayersResp, out var gp) ? gp : 0;
                }
                catch { GamePlayers = 0; }
                try
                {
                    var gameLobbiesResp = await client.GetStringAsync($"{Request.Scheme}://{Request.Host}/stats/{gameId}/lobbies");
                    GameLobbies = int.TryParse(gameLobbiesResp, out var gl) ? gl : 0;
                }
                catch { GameLobbies = 0; }
            }
            else
            {
                GamePlayers = 0;
                GameLobbies = 0;
            }
        }
    }
}
