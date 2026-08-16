using CryptoDecision.ApiService.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CryptoDecision.ApiService.API.Controllers;

[ApiController]
[Route("api/users")]
[Produces("application/json")]
public sealed class UsersController(IMediator mediator) : ControllerBase
{
    public sealed record RegisterRequest(string Name, string DeviceId);

    /// <summary>Register or update a mobile app user. Returns the user id.</summary>
    [HttpPost("register")]
    [ProducesResponseType<object>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100)
            return BadRequest(new { error = "Name must be 1–100 characters" });

        if (string.IsNullOrWhiteSpace(body.DeviceId) || body.DeviceId.Length > 200)
            return BadRequest(new { error = "Invalid deviceId" });

        var id = await mediator.Send(
            new RegisterUserCommand(body.Name.Trim(), body.DeviceId.Trim()), ct);

        return Ok(new { id });
    }

    /// <summary>Returns app user statistics for the web dashboard.</summary>
    [HttpGet("stats")]
    [ProducesResponseType<UserStatsDto>(StatusCodes.Status200OK)]
    [ResponseCache(Duration = 30)]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var result = await mediator.Send(new GetUserStatsQuery(), ct);
        return Ok(result);
    }
}
