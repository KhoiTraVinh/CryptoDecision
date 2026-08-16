using CryptoDecision.ApiService.Application;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CryptoDecision.ApiService.API.Controllers;

// ValidationException → 400 is handled centrally by GlobalExceptionMiddleware.
[ApiController]
[Route("api")]
[Produces("application/json")]
public sealed class MarketController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Returns the latest market status for a symbol:
    /// today's feature metrics + the latest model prediction.
    /// Optimized for real-time decision banners.
    /// </summary>
    [HttpGet("market-status/{symbol}")]
    [ProducesResponseType<MarketStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 30)]
    public async Task<IActionResult> GetMarketStatus(
        string symbol, [FromQuery] string exchange = "BINANCE", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetMarketStatusQuery(symbol.ToUpperInvariant(), exchange), ct));

    /// <summary>
    /// Returns the full decision dashboard for a symbol:
    /// 30-day feature history + latest prediction.
    /// Designed for charting and trend analysis.
    /// </summary>
    [HttpGet("dashboard/{symbol}")]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ResponseCache(Duration = 60)]
    public async Task<IActionResult> GetDashboard(
        string symbol, [FromQuery] int days = 30, [FromQuery] string exchange = "BINANCE", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetDashboardQuery(symbol.ToUpperInvariant(), days, exchange), ct));

    /// <summary>
    /// Returns 5-minute real-time buy/sell momentum with whale activity and signal.
    /// </summary>
    [HttpGet("momentum/{symbol}")]
    [ProducesResponseType<MomentumDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetMomentum(
        string symbol, [FromQuery] string exchange = "BINANCE", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetMomentumQuery(symbol.ToUpperInvariant(), exchange), ct));

    /// <summary>
    /// Returns the most recent 1-minute klines (OHLCV) in ascending time order.
    /// </summary>
    [HttpGet("klines/{symbol}")]
    [ProducesResponseType<IReadOnlyList<KlineDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ResponseCache(Duration = 10)]
    public async Task<IActionResult> GetKlines(
        string symbol, [FromQuery] int limit = 60, [FromQuery] string exchange = "BINANCE", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetKlinesQuery(symbol.ToUpperInvariant(), limit, exchange), ct));

    /// <summary>
    /// Returns buy/sell volume analysis across 1h, 24h, 7d and 30d windows,
    /// including whale activity breakdown.
    /// </summary>
    [HttpGet("volume/{symbol}")]
    [ProducesResponseType<VolumeAnalysisDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ResponseCache(Duration = 30)]
    public async Task<IActionResult> GetVolumeAnalysis(
        string symbol, [FromQuery] string exchange = "BINANCE", CancellationToken ct = default)
        => Ok(await mediator.Send(new GetVolumeAnalysisQuery(symbol.ToUpperInvariant(), exchange), ct));

    /// <summary>
    /// Returns the most recent whale trades.
    /// </summary>
    [HttpGet("whales/{symbol}")]
    [ProducesResponseType<IReadOnlyList<WhaleTradeDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetWhales(
        string symbol, [FromQuery] string exchange = "BINANCE", [FromQuery] int limit = 50, CancellationToken ct = default)
        => Ok(await mediator.Send(new GetRecentWhalesQuery(symbol.ToUpperInvariant(), exchange, limit), ct));
}
