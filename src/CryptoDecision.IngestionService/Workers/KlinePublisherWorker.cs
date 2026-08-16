using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Reads klines from the channel and publishes each candle directly to Kafka.
/// Klines are low-volume (1/sec per symbol) so no batching needed.
/// Only publishes closed candles to avoid updating the same partition repeatedly.
/// </summary>
public sealed class KlinePublisherWorker(
    KlineChannel channel,
    KafkaProducerService producer,
    IOptions<KafkaProducerSettings> kafkaSettings,
    ILogger<KlinePublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KlinePublisherWorker starting");
        var topics = kafkaSettings.Value.Topics;

        await foreach (var kline in channel.Reader.ReadAllAsync(stoppingToken))
        {
            // Skip interim ticks; only publish when candle is finalized
            if (!kline.IsClosed) continue;

            var batch = new KlineBatch(
                Exchange:       "binance",
                Symbol:         kline.Symbol,
                Interval:       "1m",
                BatchTimestamp: DateTimeOffset.UtcNow,
                Kline:          kline);

            var topic = kline.Symbol.ToUpperInvariant() switch
            {
                "BTCUSDT" => topics.Kline1mBtcUsdt,
                "ETHUSDT" => topics.Kline1mEthUsdt,
                _         => $"binance.kline.1m.{kline.Symbol.ToLowerInvariant()}"
            };

            await producer.PublishAsync(topic, key: kline.Symbol, batch, stoppingToken);

            logger.LogInformation(
                "Published closed kline {Symbol} O={Open} H={High} L={Low} C={Close} V={Volume}",
                kline.Symbol, kline.Open, kline.High, kline.Low, kline.Close, kline.Volume);
        }

        logger.LogInformation("KlinePublisherWorker stopped");
    }
}
