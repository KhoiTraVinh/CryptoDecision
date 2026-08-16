using CryptoDecision.ApiService.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CryptoDecision.ApiService.API.Controllers;

[ApiController]
[Route("api/alerts")]
[Produces("application/json")]
public sealed class AlertController(IMediator mediator) : ControllerBase
{
    /// <summary>Create a new price alert.</summary>
    [HttpPost]
    [ProducesResponseType<PriceAlertDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateAlert(
        [FromBody] CreateAlertCommand command, CancellationToken ct = default)
    {
        var alert = await mediator.Send(command, ct);
        return Created($"/api/alerts/{alert.Id}", alert);
    }

    /// <summary>Get all active alerts (optionally filtered by symbol).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PriceAlertDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] string? symbol = null, CancellationToken ct = default)
        => Ok(await mediator.Send(new GetAlertsQuery(symbol?.ToUpperInvariant()), ct));

    /// <summary>Get alert history (triggered alerts).</summary>
    [HttpGet("history")]
    [ProducesResponseType<IReadOnlyList<AlertNotificationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlertHistory(
        [FromQuery] string? symbol = null, [FromQuery] int limit = 50, CancellationToken ct = default)
        => Ok(await mediator.Send(new GetAlertHistoryQuery(symbol?.ToUpperInvariant(), limit), ct));

    /// <summary>Delete (deactivate) an alert.</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAlert(long id, CancellationToken ct = default)
    {
        var deleted = await mediator.Send(new DeleteAlertCommand(id), ct);
        return deleted ? NoContent() : NotFound();
    }
}
