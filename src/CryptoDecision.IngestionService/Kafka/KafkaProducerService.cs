using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Kafka;

/// <summary>
/// Thread-safe Kafka producer wrapper.
///
/// Settings rationale:
///   EnableIdempotence=true  → exactly-once delivery per producer session
///   Acks=All                → strongest durability (all ISR must ack)
///   CompressionType=Lz4    → best speed/ratio for JSON payloads
///   LingerMs=10             → small batching window, reduces round-trips
///   MaxInFlight=5           → max concurrent requests (safe with idempotence)
///   EnableDeliveryReports   → we await ack per-batch, not per-message
/// </summary>
public sealed class KafkaProducerService : IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ActivitySource _activitySource;
    private readonly ILogger<KafkaProducerService> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public KafkaProducerService(
        IOptions<KafkaProducerSettings> settings,
        ActivitySource activitySource,
        ILogger<KafkaProducerService> logger)
    {
        _activitySource = activitySource;
        _logger = logger;
        var cfg = settings.Value;

        var config = new ProducerConfig
        {
            BootstrapServers      = cfg.BootstrapServers,
            EnableIdempotence     = true,               // exactly-once writes
            Acks                  = Acks.All,           // all ISR must ack
            CompressionType       = CompressionType.Lz4,
            LingerMs              = 10,                 // 10ms batch window
            MaxInFlight           = 5,                  // safe with idempotence
            MessageSendMaxRetries = 10,                 // outer Kafka retries
            RetryBackoffMs        = 100,                // base backoff
            EnableDeliveryReports = true,
            BatchSize             = 131_072,            // 128KB batch size
            // Exponential backoff handled inside ProduceAsync retry loop
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Kafka producer error: {Code} {Reason}", e.Code, e.Reason))
            .Build();
    }

    /// <summary>
    /// Publishes a message with retry + exponential backoff on transient errors.
    /// Uses the symbol as the Kafka message key for partition affinity
    /// (all BTCUSDT messages land on the same partition, preserving order).
    /// Injects W3C traceparent header for distributed trace propagation to ProcessorService.
    /// </summary>
    public async Task PublishAsync<T>(
        string topic,
        string key,
        T payload,
        CancellationToken ct) where T : class
    {
        string json;
        if (payload is CryptoDecision.IngestionService.Models.TradeBatch tb)
        {
            json = JsonSerializer.Serialize(tb, CryptoDecision.IngestionService.Serialization.KafkaProducerJsonContext.Default.TradeBatch);
        }
        else if (payload is CryptoDecision.IngestionService.Models.KlineBatch kb)
        {
            json = JsonSerializer.Serialize(kb, CryptoDecision.IngestionService.Serialization.KafkaProducerJsonContext.Default.KlineBatch);
        }
        else
        {
            json = JsonSerializer.Serialize(payload, JsonOpts);
        }

        // Start a producer span — sampled if OTel SDK has a sampler configured
        using var activity = _activitySource.StartActivity(
            $"kafka.publish {topic}", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination", topic);
        activity?.SetTag("messaging.kafka.message_key", key);

        // Propagate trace context via W3C traceparent header
        var headers = new Headers();
        if (activity != null)
        {
            var traceparent = $"00-{activity.TraceId}-{activity.SpanId}-01";
            headers.Add("traceparent", Encoding.UTF8.GetBytes(traceparent));
        }

        var msg = new Message<string, string> { Key = key, Value = json, Headers = headers };

        const int maxAttempts = 5;
        var backoff = TimeSpan.FromMilliseconds(100);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await _producer.ProduceAsync(topic, msg, ct);
                activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
                activity?.SetTag("messaging.kafka.offset", result.Offset.Value);
                _logger.LogDebug(
                    "Published to {Topic} [{Partition}|{Offset}] key={Key} size={Size}b",
                    topic, result.Partition.Value, result.Offset.Value, key, json.Length);
                return;
            }
            catch (ProduceException<string, string> ex)
                when (!ex.Error.IsFatal && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "Kafka produce attempt {Attempt}/{Max} failed for {Topic}: {Error}. Retrying in {Backoff}ms",
                    attempt, maxAttempts, topic, ex.Error.Reason, backoff.TotalMilliseconds);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * 2);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(15));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class KafkaProducerSettings
{
    public const string Section = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";

    public Topics Topics { get; set; } = new();
}

public sealed class Topics
{
    public string TradeBtcUsdt    { get; set; } = "binance.trade.btcusdt";
    public string TradeEthUsdt    { get; set; } = "binance.trade.ethusdt";
    public string Kline1mBtcUsdt  { get; set; } = "binance.kline.1m.btcusdt";
    public string Kline1mEthUsdt  { get; set; } = "binance.kline.1m.ethusdt";
}

public sealed class BatchSettings
{
    public const string Section = "Batch";

    /// <summary>Flush to Kafka when this many trades have accumulated (per symbol).</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Flush to Kafka at least this often, regardless of trade count.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);
}
