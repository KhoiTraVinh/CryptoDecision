using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Kafka batch publisher for Binance trades → binance.trade.* topics.
/// Refactored to extend KafkaTradePublisherBase (shared batching + flush logic).
///
/// Flush triggers (whichever comes first):
///   • BatchSize trades accumulated (default: 200)
///   • FlushInterval elapsed         (default: 100ms)
/// </summary>
public sealed class KafkaBatchPublisherWorker(
    TradeChannel channel,
    KafkaProducerService producer,
    IngestionMetrics metrics,
    IOptions<KafkaProducerSettings> kafkaSettings,
    IOptions<BatchSettings> batchSettings,
    ILogger<KafkaBatchPublisherWorker> logger)
    : KafkaTradePublisherBase(channel.Reader, producer, metrics, batchSettings, logger)
{
    private readonly Topics _topics = kafkaSettings.Value.Topics;

    protected override string ExchangeName => "BINANCE";

    protected override string GetTopic(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "BTCUSDT" => _topics.TradeBtcUsdt,
            "ETHUSDT" => _topics.TradeEthUsdt,
            _         => $"binance.trade.{symbol.ToLowerInvariant()}"
        };
}
