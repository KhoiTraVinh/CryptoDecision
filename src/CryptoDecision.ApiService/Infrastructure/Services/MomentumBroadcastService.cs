using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Domain.Interfaces;
using CryptoDecision.ApiService.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CryptoDecision.ApiService.Infrastructure.Services;

/// <summary>
/// Background service that queries momentum data for each supported symbol
/// every 5 seconds and broadcasts the result to all connected WebSocket clients
/// subscribed to that symbol's group.
/// </summary>
public sealed class MomentumBroadcastService(
    IHubContext<MarketHub, IMarketClient> hub,
    IMomentumRepository                  repo,
    ILogger<MomentumBroadcastService>    logger)
    : BackgroundService
{
    private static readonly string[] Symbols   = ["BTCUSDT", "ETHUSDT"];
    private static readonly string[] Exchanges = ["BINANCE", "BYBIT", "OKX", "ALL"];
    private static readonly TimeSpan  Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "MomentumBroadcastService started — pushing every {Interval}s for {Symbols} x {Exchanges}",
            Interval.TotalSeconds, string.Join(", ", Symbols), string.Join(", ", Exchanges));

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var symbol in Symbols)
            {
                foreach (var exchange in Exchanges)
                {
                    try
                    {
                        var data = await repo.GetAsync(symbol, exchange, ct);
                        var dto  = MomentumScorer.BuildDto(symbol, data);

                        await hub.Clients
                            .Group(MarketHub.GroupName(symbol, exchange))
                            .ReceiveMomentum(dto);

                        logger.LogDebug(
                            "Broadcast momentum {Exchange}_{Symbol}: signal={Signal} trades={Trades}",
                            exchange, symbol, dto.Signal, dto.TotalTrades);
                    }
                    catch (OperationCanceledException) { /* shutting down */ }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to broadcast momentum for {Exchange}_{Symbol}", exchange, symbol);
                    }
                }
            }
        }

        logger.LogInformation("MomentumBroadcastService stopped");
    }
}
