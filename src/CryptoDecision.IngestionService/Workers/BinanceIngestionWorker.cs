using CryptoDecision.IngestionService.Binance;
using CryptoDecision.IngestionService.Channels;
using Microsoft.Extensions.Logging;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Top-level worker that owns the Binance WebSocket connection lifecycle.
/// Runs the reconnect loop indefinitely until shutdown is signalled.
/// Publishes channel fill metrics every 30s for monitoring.
/// </summary>
public sealed class BinanceIngestionWorker(
    BinanceWebSocketClient wsClient,
    TradeChannel tradeChannel,
    KlineChannel klineChannel,
    ILogger<BinanceIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BinanceIngestionWorker starting");

        // Telemetry loop runs in background while WebSocket runs in foreground
        var metricsTask = ReportMetricsAsync(stoppingToken);
        var wsTask      = wsClient.RunAsync(stoppingToken);

        await Task.WhenAll(wsTask, metricsTask);

        // Signal channel completion so downstream workers drain and exit cleanly
        tradeChannel.Writer.TryComplete();
        klineChannel.Writer.TryComplete();

        logger.LogInformation("BinanceIngestionWorker stopped");
    }

    private async Task ReportMetricsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            logger.LogInformation(
                "Channel depth — trades: {TradePending}, klines: {KlinePending}",
                tradeChannel.PendingCount,
                klineChannel.PendingCount);
        }
    }
}
