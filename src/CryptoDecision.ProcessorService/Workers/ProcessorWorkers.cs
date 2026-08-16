using System.Diagnostics;
using CryptoDecision.ProcessorService.Kafka;
using CryptoDecision.ProcessorService.Models;
using CryptoDecision.ProcessorService.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.ProcessorService.Workers;

/// <summary>
/// Consumes trade batches from Kafka and bulk-inserts via PostgreSQL COPY.
/// Offset committed ONLY after successful COPY — at-least-once guarantee.
/// </summary>
public sealed class TradeProcessorWorker(
    TradeRepository repo,
    IOptions<ConsumerSettings> settings,
    ActivitySource activitySource,
    IMessageDeserializer<TradeBatch> deserializer,
    ILogger<TradeProcessorWorker> logger)
    : KafkaConsumerBase<TradeBatch>(settings, activitySource, deserializer, settings.Value.TradeTopics, logger)
{
    protected override async Task ProcessAsync(
        TradeBatch batch, string topic, CancellationToken ct)
    {
        logger.LogDebug(
            "Processing {Symbol} trade batch: {Count} trades, timestamp={Ts}",
            batch.Symbol, batch.Trades.Count, batch.BatchTimestamp);

        if (batch.Trades.Count == 0) return;

        var whales = batch.Trades.Count(t => t.IsWhaleTrade);
        if (whales > 0)
            logger.LogInformation("🐋 {Count} whale trade(s) detected in {Symbol} batch", whales, batch.Symbol);

        // COPY to PostgreSQL — if this throws, offset is NOT committed
        await repo.BulkInsertTradesAsync(batch.Exchange.ToUpperInvariant(), batch.Trades, ct);
    }
}

/// <summary>
/// Consumes closed 1m klines from Kafka and upserts to klines_1m.
/// Uses INSERT ... ON CONFLICT to handle potential duplicate closed candles.
/// </summary>
public sealed class KlineProcessorWorker(
    TradeRepository repo,
    IOptions<ConsumerSettings> settings,
    ActivitySource activitySource,
    IMessageDeserializer<KlineBatch> deserializer,
    ILogger<KlineProcessorWorker> logger)
    : KafkaConsumerBase<KlineBatch>(settings, activitySource, deserializer, settings.Value.KlineTopics, logger)
{
    protected override async Task ProcessAsync(
        KlineBatch batch, string topic, CancellationToken ct)
    {
        if (!batch.Kline.IsClosed) return;  // skip interim updates

        logger.LogDebug(
            "Upserting closed kline {Symbol} [{OpenTime}] O={O} H={H} L={L} C={C}",
            batch.Symbol, batch.Kline.OpenTime,
            batch.Kline.Open, batch.Kline.High, batch.Kline.Low, batch.Kline.Close);

        await repo.UpsertKlineAsync(batch.Kline, ct);
    }
}
