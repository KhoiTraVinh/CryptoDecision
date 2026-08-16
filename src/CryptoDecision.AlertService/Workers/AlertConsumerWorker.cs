using System.Text.Json;
using Confluent.Kafka;
using CryptoDecision.AlertService.Engine;
using CryptoDecision.AlertService.Kafka;
using CryptoDecision.AlertService.Models;
using CryptoDecision.AlertService.Serialization;
using CryptoDecision.AlertService.Telemetry;

namespace CryptoDecision.AlertService.Workers;

/// <summary>
/// Kafka consumer that evaluates trade prices against active price alerts.
///
/// Architecture:
///   1. Consumes from ALL trade topics (binance.trade.*, okx.trade.*, bybit.trade.*)
///   2. Uses a SEPARATE consumer group (alert-group) from ProcessorService (processor-group)
///      → Both services get their own copy of every message independently
///   3. For each trade batch: extract latest price → evaluate against cached alerts
///   4. Triggered alerts → publish to alerts.notifications topic
///
/// Interview talking points:
///   - Consumer group isolation: multiple services consume same topics independently
///   - Manual offset commit: at-least-once delivery (idempotent because alert marked triggered in DB)
///   - In-memory cache refresh: avoids DB query per trade
///   - Backpressure: exponential backoff on errors
/// </summary>
public sealed class AlertConsumerWorker(
    IConfiguration configuration,
    AlertEngine engine,
    AlertNotificationProducer producer,
    AlertMetrics metrics,
    ILogger<AlertConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to allow other hosted services to start (blocking Consume() call)
        await Task.Yield();

        // Initialize alert cache from DB
        await engine.InitializeAsync(stoppingToken);

        var topics = configuration.GetSection("Kafka:TradeTopics").Get<string[]>()
            ?? ["binance.trade.btcusdt", "binance.trade.ethusdt"];

        var config = new ConsumerConfig
        {
            BootstrapServers  = configuration["Kafka:BootstrapServers"] ?? "kafka:9092",
            GroupId           = configuration["Kafka:GroupId"] ?? "alert-group",
            AutoOffsetReset   = AutoOffsetReset.Latest,    // Only evaluate new trades (not historical)
            EnableAutoCommit  = false,
            MaxPollIntervalMs = 300_000,
            SessionTimeoutMs  = 45_000,
            FetchMinBytes     = 1,
            FetchWaitMaxMs    = 500
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                logger.LogError("Alert consumer error [{Code}]: {Reason}", e.Code, e.Reason))
            .SetPartitionsAssignedHandler((_, p) =>
                logger.LogInformation("Alert consumer partitions assigned: {Partitions}",
                    string.Join(",", p)))
            .SetPartitionsRevokedHandler((_, p) =>
                logger.LogInformation("Alert consumer partitions revoked: {Partitions}",
                    string.Join(",", p)))
            .Build();

        consumer.Subscribe(topics);
        logger.LogInformation("AlertConsumerWorker subscribed to: {Topics}", string.Join(", ", topics));

        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                // Periodically refresh alert cache from DB
                await engine.RefreshIfStaleAsync(stoppingToken);

                cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (cr is null) continue;

                var batch = JsonSerializer.Deserialize(cr.Message.Value,
                    AlertJsonContext.Default.TradeBatch);

                if (batch is null)
                {
                    consumer.Commit(cr);
                    continue;
                }

                // Extract the latest price from the batch
                var latestTrade = batch.Trades.MaxBy(t => t.TradeTime);
                if (latestTrade is null)
                {
                    consumer.Commit(cr);
                    continue;
                }

                metrics.TradesEvaluated.Add(batch.Trades.Count,
                    new KeyValuePair<string, object?>("symbol", batch.Symbol));

                // Evaluate price against all active alerts for this symbol
                var triggered = await engine.EvaluateAsync(
                    batch.Symbol, latestTrade.Price, stoppingToken);

                // Publish triggered notifications to Kafka
                foreach (var notification in triggered)
                    await producer.PublishAsync(notification, stoppingToken);

                consumer.Commit(cr);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Alert consume exception — retrying in {Backoff}s",
                    backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = Cap(backoff * 2);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert processing failed — NOT committing, retry in {Backoff}s",
                    backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = Cap(backoff * 2);
            }
        }

        consumer.Close();
        logger.LogInformation("AlertConsumerWorker closed");
    }

    private static TimeSpan Cap(TimeSpan t) =>
        t > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : t;
}
