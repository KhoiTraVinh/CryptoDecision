using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Domain.Entities;
using CryptoDecision.ApiService.Domain.Interfaces;
using CryptoDecision.ApiService.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CryptoDecision.ApiService.Infrastructure.Services;

/// <summary>
/// Background service that queries recent whale trades every 3 seconds
/// and broadcasts alerts to all connected WebSocket clients subscribed
/// to the momentum updates of that symbol.
/// </summary>
public sealed class WhaleAlertBroadcastService(
    IHubContext<MarketHub, IMarketClient> hub,
    ITradeQueryRepository                repo,
    ILogger<WhaleAlertBroadcastService>  logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    
    // Track the last check time to only fetch new trades
    private DateTime _lastChecked = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "WhaleAlertBroadcastService started — polling every {Interval}s for new whale trades",
            Interval.TotalSeconds);

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTime.UtcNow;
            try
            {
                var recentWhales = await repo.GetRecentWhalesAsync(_lastChecked, ct);
                _lastChecked = now;

                if (recentWhales.Count > 0)
                {
                    // Group by Symbol and Exchange
                    var grouped = recentWhales.GroupBy(w => new { w.Symbol, w.Exchange });

                    foreach (var group in grouped)
                    {
                        var symbol = group.Key.Symbol;
                        var exchange = group.Key.Exchange;

                        // Map to DTO
                        var dtos = group.Select(w => new WhaleTradeDto(
                            w.Symbol,
                            w.Exchange,
                            w.Price,
                            w.QuoteQty,
                            w.IsBuyerMaker,
                            w.TradeTime
                        )).ToList();

                        var alert = new WhaleAlertDto(symbol, exchange, dtos, DateTime.UtcNow);

                        // Broadcast to the channel that already exists for momentum updates
                        await hub.Clients
                            .Group(MarketHub.GroupName(symbol, exchange))
                            .ReceiveWhaleAlert(alert);

                        // Also broadcast to the ALL exchange channel if it's a specific exchange
                        if (!string.Equals(exchange, "ALL", StringComparison.OrdinalIgnoreCase))
                        {
                            await hub.Clients
                                .Group(MarketHub.GroupName(symbol, "ALL"))
                                .ReceiveWhaleAlert(alert);
                        }

                        logger.LogInformation(
                            "Broadcasted {Count} whale alerts for {Exchange}_{Symbol}",
                            dtos.Count, exchange, symbol);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to poll and broadcast whale alerts");
            }
        }

        logger.LogInformation("WhaleAlertBroadcastService stopped");
    }
}
