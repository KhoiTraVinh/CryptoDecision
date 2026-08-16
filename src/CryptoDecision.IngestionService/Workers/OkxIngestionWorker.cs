using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.OKX;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Owns the OKX WebSocket connection lifecycle and routes trades via OkxTradeChannel.
/// Mirrors BinanceIngestionWorker — one worker per exchange for clean isolation.
/// </summary>
public sealed class OkxIngestionWorker(
    OkxWebSocketClient wsClient,
    OkxTradeChannel    tradeChannel,
    ILogger<OkxIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OkxIngestionWorker starting");
        await wsClient.RunAsync(stoppingToken);
        tradeChannel.Writer.TryComplete();
        logger.LogInformation("OkxIngestionWorker stopped");
    }
}

/// <summary>Kafka batch publisher for OKX trades → okx.trade.* topics.</summary>
public sealed class OkxKafkaBatchPublisherWorker(
    OkxTradeChannel channel,
    KafkaProducerService producer,
    IngestionMetrics metrics,
    IOptions<BatchSettings> batchSettings,
    ILogger<OkxKafkaBatchPublisherWorker> logger)
    : KafkaTradePublisherBase(channel.Reader, producer, metrics, batchSettings, logger)
{
    protected override string ExchangeName => "OKX";

    protected override string GetTopic(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "BTCUSDT" => "okx.trade.btcusdt",
            "ETHUSDT" => "okx.trade.ethusdt",
            _         => $"okx.trade.{symbol.ToLowerInvariant()}"
        };
}
