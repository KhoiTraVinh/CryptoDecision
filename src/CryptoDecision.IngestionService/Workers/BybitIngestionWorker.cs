using CryptoDecision.IngestionService.Bybit;
using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Owns the Bybit WebSocket connection lifecycle and routes trades via BybitTradeChannel.
/// </summary>
public sealed class BybitIngestionWorker(
    BybitWebSocketClient wsClient,
    BybitTradeChannel    tradeChannel,
    ILogger<BybitIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BybitIngestionWorker starting");
        await wsClient.RunAsync(stoppingToken);
        tradeChannel.Writer.TryComplete();
        logger.LogInformation("BybitIngestionWorker stopped");
    }
}

/// <summary>Kafka batch publisher for Bybit trades → bybit.trade.* topics.</summary>
public sealed class BybitKafkaBatchPublisherWorker(
    BybitTradeChannel channel,
    KafkaProducerService producer,
    IngestionMetrics metrics,
    IOptions<BatchSettings> batchSettings,
    ILogger<BybitKafkaBatchPublisherWorker> logger)
    : KafkaTradePublisherBase(channel.Reader, producer, metrics, batchSettings, logger)
{
    protected override string ExchangeName => "BYBIT";

    protected override string GetTopic(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "BTCUSDT" => "bybit.trade.btcusdt",
            "ETHUSDT" => "bybit.trade.ethusdt",
            _         => $"bybit.trade.{symbol.ToLowerInvariant()}"
        };
}
