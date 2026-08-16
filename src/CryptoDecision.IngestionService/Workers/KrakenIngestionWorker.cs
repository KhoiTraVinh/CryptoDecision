using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Kraken;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Owns the Kraken WebSocket connection lifecycle and routes trades via KrakenTradeChannel.
/// </summary>
public sealed class KrakenIngestionWorker(
    KrakenWebSocketClient wsClient,
    KrakenTradeChannel    tradeChannel,
    ILogger<KrakenIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KrakenIngestionWorker starting");
        await wsClient.RunAsync(stoppingToken);
        tradeChannel.Writer.TryComplete();
        logger.LogInformation("KrakenIngestionWorker stopped");
    }
}

/// <summary>Kafka batch publisher for Kraken trades → kraken.trade.* topics.</summary>
public sealed class KrakenKafkaBatchPublisherWorker(
    KrakenTradeChannel channel,
    KafkaProducerService producer,
    IngestionMetrics metrics,
    IOptions<BatchSettings> batchSettings,
    ILogger<KrakenKafkaBatchPublisherWorker> logger)
    : KafkaTradePublisherBase(channel.Reader, producer, metrics, batchSettings, logger)
{
    protected override string ExchangeName => "KRAKEN";

    protected override string GetTopic(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "BTCUSDT" => "kraken.trade.btcusdt",
            "ETHUSDT" => "kraken.trade.ethusdt",
            _         => $"kraken.trade.{symbol.ToLowerInvariant()}"
        };
}
