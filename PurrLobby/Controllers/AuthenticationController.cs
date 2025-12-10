using Microsoft.AspNetCore.Mvc;
using PurrLobby.Models;
using PurrLobby.Services;

namespace PurrLobby.Controllers;

[ApiController]
[Route("auth")]
public class AuthenticationController : ControllerBase
{
    private readonly IAuthenticationService _authService;

    public AuthenticationController(IAuthenticationService authService)
    {
        _authService = authService;
    }

    [HttpPost("create")]
    public async Task<ActionResult<UserSession>> CreateSession([FromBody] CreateSessionRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return BadRequest(new { error = "UserId and DisplayName are required" });
            }

            var session = await _authService.CreateSessionAsync(request.UserId, request.DisplayName);
            return Ok(session);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
catch
    {
        return StatusCode(500, new { error = "Internal server error" });
    }
    }

    [HttpPost("validate")]
    public async Task<ActionResult<TokenValidationResult>> ValidateToken([FromBody] ValidateTokenRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return BadRequest(new { error = "Token is required" });
            }

            var result = await _authService.ValidateTokenAsync(request.Token);
            return Ok(result);
        }
catch
    {
        return StatusCode(500, new { error = "Internal server error" });
    }
    }

    [HttpPost("revoke")]
    public async Task<ActionResult<bool>> RevokeToken([FromBody] RevokeTokenRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return BadRequest(new { error = "Token is required" });
            }

            var result = await _authService.RevokeTokenAsync(request.Token);
            return Ok(result);
        }
catch
    {
        return StatusCode(500, new { error = "Internal server error" });
    }
    }
}

public class CreateSessionRequest
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
}

public class ValidateTokenRequest
{
    public required string Token { get; set; }
}

public class RevokeTokenRequest
{
    public required string Token { get; set; }
}