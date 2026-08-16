using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Domain.Interfaces;
using CryptoDecision.ApiService.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CryptoDecision.ApiService.Infrastructure.Services;

/// <summary>
/// Background service that queries volume analysis data for each supported symbol
/// every 30 seconds and broadcasts the result to all connected WebSocket clients
/// subscribed to that symbol's volume group.
/// </summary>
public sealed class VolumeAnalysisBroadcastService(
    IHubContext<MarketHub, IMarketClient> hub,
    IVolumeRepository                     repo,
    ILogger<VolumeAnalysisBroadcastService> logger)
    : BackgroundService
{
    private static readonly string[] Symbols  = ["BTCUSDT", "ETHUSDT"];
    private static readonly string[] Exchanges = ["BINANCE", "BYBIT", "OKX", "ALL"];
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "VolumeAnalysisBroadcastService started — pushing every {Interval}s for {Symbols} x {Exchanges}",
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
                        var raw = await repo.GetWindowsAsync(symbol, exchange, ct);
                        var dto = VolumeAnalysisMapper.BuildDto(symbol, raw);

                        await hub.Clients
                            .Group(MarketHub.VolumeGroupName(symbol, exchange))
                            .ReceiveVolumeAnalysis(dto);

                        logger.LogDebug("Broadcast volume analysis {Exchange}_{Symbol}", exchange, symbol);
                    }
                    catch (OperationCanceledException) { /* shutting down */ }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to broadcast volume analysis for {Exchange}_{Symbol}", exchange, symbol);
                    }
                }
            }
        }

        logger.LogInformation("VolumeAnalysisBroadcastService stopped");
    }
}
