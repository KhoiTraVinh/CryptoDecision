using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.OKX;
using CryptoDecision.IngestionService.Telemetry;
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

    // Two named arms used to sit above this and each produced the identical string the
    // fallback produces. The service only ever subscribes to what MarketSubscription:Pairs
    // names, so neither was reachable either.
    protected override string GetTopic(string symbol) =>
        $"okx.trade.{symbol.ToLowerInvariant()}";
}
