using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Telemetry;
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
    IOptions<BatchSettings> batchSettings,
    ILogger<KafkaBatchPublisherWorker> logger)
    : KafkaTradePublisherBase(channel.Reader, producer, metrics, batchSettings, logger)
{
    protected override string ExchangeName => "BINANCE";

    /// <summary>
    /// One rule, no table. This had named overrides for BTCUSDT and ETHUSDT read from a
    /// `Topics` settings block, and both arms were unreachable: the service subscribes to
    /// what MarketSubscription:Pairs names, which is SOL-USDT, so every message took the
    /// fallback. The overrides also produced exactly the string the fallback produces —
    /// `binance.trade.btcusdt` — so they were a configurable way to get the default.
    ///
    /// The two other exchange workers already derived their topic this way, so this also
    /// removes the last disagreement about how a topic name is formed.
    /// </summary>
    protected override string GetTopic(string symbol) =>
        $"binance.trade.{symbol.ToLowerInvariant()}";
}
