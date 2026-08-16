using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.ProcessorService.Kafka;

/// <summary>
/// Generic Kafka consumer base with:
///   • Manual offset commit (EnableAutoCommit=false)
///   • Offsets committed ONLY after successful processing (at-least-once)
///   • Configurable poll/batch timeout
///   • Graceful shutdown on cancellation
///   • Exponential backoff on processing errors (no poison-pill infinite loop)
///   • W3C traceparent extraction for distributed trace continuation from IngestionService
///   • Pluggable IMessageDeserializer (OCP — no typeof dispatch)
/// </summary>
public abstract class KafkaConsumerBase<TMessage> : BackgroundService
    where TMessage : class
{
    private readonly ILogger _logger;
    private readonly string[] _topics;
    private readonly ConsumerSettings _settings;
    private readonly ActivitySource _activitySource;
    private readonly IMessageDeserializer<TMessage> _deserializer;

    protected KafkaConsumerBase(
        IOptions<ConsumerSettings> settings,
        ActivitySource activitySource,
        IMessageDeserializer<TMessage> deserializer,
        string[] topics,
        ILogger logger)
    {
        _settings       = settings.Value;
        _activitySource = activitySource;
        _deserializer   = deserializer;
        _topics         = topics;
        _logger         = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Confluent.Kafka consumer.Consume() is a blocking call. Without yielding
        // first, StartAsync() would block on this worker and never start the others
        // (HealthCheckHttpServer, KlineProcessorWorker, FeatureAggregationWorker).
        await Task.Yield();

        var config = new ConsumerConfig
        {
            BootstrapServers       = _settings.BootstrapServers,
            GroupId                = _settings.GroupId,
            AutoOffsetReset        = AutoOffsetReset.Earliest,
            EnableAutoCommit       = false,          // we commit manually after DB write
            MaxPollIntervalMs      = 300_000,        // 5 minutes max between polls
            SessionTimeoutMs       = 45_000,
            FetchMinBytes          = 1,
            FetchWaitMaxMs         = 500
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Kafka consumer error [{Code}]: {Reason}", e.Code, e.Reason))
            .SetPartitionsAssignedHandler((_, p) =>
                _logger.LogInformation("Partitions assigned: {Partitions}", string.Join(",", p)))
            .SetPartitionsRevokedHandler((_, p) =>
                _logger.LogInformation("Partitions revoked: {Partitions}", string.Join(",", p)))
            .Build();

        consumer.Subscribe(_topics);
        _logger.LogInformation("{Consumer} subscribed to: {Topics}", GetType().Name, string.Join(", ", _topics));

        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (cr is null) continue;

                ActivityContext parentCtx = default;
                if (cr.Message.Headers?.TryGetLastBytes("traceparent", out var tpBytes) == true)
                {
                    var tpValue = Encoding.UTF8.GetString(tpBytes);
                    ActivityContext.TryParse(tpValue, null, out parentCtx);
                }

                using var activity = _activitySource.StartActivity(
                    $"kafka.consume {cr.Topic}", ActivityKind.Consumer, parentCtx);
                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.kafka.topic", cr.Topic);
                activity?.SetTag("messaging.kafka.partition", cr.Partition.Value);
                activity?.SetTag("messaging.kafka.offset", cr.Offset.Value);

                var message = _deserializer.Deserialize(cr.Message.Value);
                
                if (message is null)
                {
                    _logger.LogWarning("Null deserialization on {Topic}[{Partition}]@{Offset}",
                        cr.Topic, cr.Partition.Value, cr.Offset.Value);
                    consumer.Commit(cr);  // skip poison pill
                    continue;
                }

                await ProcessAsync(message, cr.Topic, stoppingToken);

                // ✅ Commit ONLY after successful processing
                consumer.Commit(cr);
                backoff = TimeSpan.FromSeconds(1);   // reset on success

                _logger.LogDebug("Committed {Topic}[{Partition}]@{Offset}",
                    cr.Topic, cr.Partition.Value, cr.Offset.Value);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume exception — retrying in {Backoff}s", backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = Cap(backoff * 2);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Processing failed for {Topic}[{P}]@{O} — NOT committing, retry in {B}s",
                    cr?.Topic, cr?.Partition.Value, cr?.Offset.Value, backoff.TotalSeconds);
                // Do NOT commit — message will be redelivered after restart
                await Task.Delay(backoff, stoppingToken);
                backoff = Cap(backoff * 2);
            }
        }

        consumer.Close();
        _logger.LogInformation("{Consumer} closed", GetType().Name);
    }

    protected abstract Task ProcessAsync(TMessage message, string topic, CancellationToken ct);

    private static TimeSpan Cap(TimeSpan t) =>
        t > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : t;
}

public sealed class ConsumerSettings
{
    public const string Section = "Kafka";
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string GroupId          { get; set; } = "processor-group";
    // Defaults are empty — values always come from appsettings.json / config.
    // Do NOT set C# defaults here: .NET config binding merges array defaults with
    // config values, resulting in duplicates that librdkafka rejects.
    public string[] TradeTopics    { get; set; } = [];
    public string[] KlineTopics    { get; set; } = [];
}
