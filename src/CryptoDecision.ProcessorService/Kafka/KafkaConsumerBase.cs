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
///   • Exponential backoff on processing errors, with a bounded redelivery count
///   • W3C traceparent extraction for distributed trace continuation from IngestionService
///   • Pluggable IMessageDeserializer (OCP — no typeof dispatch)
///
/// Why a failure seeks instead of only declining to commit
/// ------------------------------------------------------
/// "At-least-once" was a claim this class did not honour. On a processing exception it
/// skipped the commit and continued the loop — but not committing does not rewind
/// anything: the consumer's fetch position has already advanced past that message, so
/// the next Consume returned the NEXT one, and the next successful commit buried the
/// failed offset for good. The comment said "will be redelivered after restart", and it
/// would not have been, because the commit that followed it moved the group past it.
///
/// One transient Postgres hiccup was therefore a silent, permanent hole in the trades
/// table — invisible downstream, because a bucket with fewer trades in it looks exactly
/// like a quiet bucket. That is the failure shape this repository keeps paying for.
///
/// Seeking back to the failed offset makes redelivery real. It also makes a genuinely
/// undeserialisable-but-parseable message an infinite loop, which is the cost, so
/// redelivery is bounded by <see cref="ConsumerSettings.MaxDeliveryAttempts"/> and
/// giving up is LOUD: the payload is logged in full at Critical before the offset is
/// skipped, so the rows can be replayed by hand rather than merely mourned.
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

        // The offset currently being retried, and how many times it has been handed to
        // ProcessAsync. Reset on every success and on every move to a new offset, so
        // this counts consecutive failures of one message rather than failures in general.
        TopicPartitionOffset? retrying = null;
        var attempts = 0;

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
                    // Skipped rather than retried: a payload that deserialises to null
                    // will do so again forever. Error, with the payload, because the
                    // rows in it are gone and a batch quietly missing from `trades`
                    // looks exactly like a quiet market downstream.
                    _logger.LogError(
                        "Null deserialization on {Topic}[{Partition}]@{Offset} — skipping. The trades " +
                        "in this batch will never be persisted. Payload: {Payload}",
                        cr.Topic, cr.Partition.Value, cr.Offset.Value, cr.Message.Value);

                    consumer.Commit(cr);  // skip poison pill
                    retrying = null;
                    attempts = 0;
                    continue;
                }

                await ProcessAsync(message, cr.Topic, stoppingToken);

                // ✅ Commit ONLY after successful processing
                consumer.Commit(cr);
                backoff  = TimeSpan.FromSeconds(1);   // reset on success
                retrying = null;
                attempts = 0;

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
                if (cr is null)
                {
                    // Nothing was consumed, so there is nothing to rewind to. Only the
                    // deserializer or the activity plumbing can reach here.
                    _logger.LogError(ex, "Consume loop failed before a message was read — retry in {B}s",
                        backoff.TotalSeconds);
                    await Task.Delay(backoff, stoppingToken);
                    backoff = Cap(backoff * 2);
                    continue;
                }

                // A new offset starts its own attempt count. Without this, a partition
                // that failed once and then failed again hours later on a different
                // message would carry the old count and give up early.
                if (retrying is null || !retrying.TopicPartition.Equals(cr.TopicPartition)
                                     || retrying.Offset != cr.Offset)
                {
                    retrying = cr.TopicPartitionOffset;
                    attempts = 0;
                }

                attempts++;

                if (attempts >= _settings.MaxDeliveryAttempts)
                {
                    // Giving up on a message is data loss, so it is recorded as data
                    // rather than as a sentence. The payload goes into the log in full:
                    // it is the only remaining copy once the offset is committed past.
                    _logger.LogCritical(ex,
                        "Giving up on {Topic}[{P}]@{O} after {Attempts} attempts — COMMITTING PAST IT. " +
                        "These rows will never be persisted. Payload follows so it can be replayed by " +
                        "hand: {Payload}",
                        cr.Topic, cr.Partition.Value, cr.Offset.Value, attempts, cr.Message.Value);

                    consumer.Commit(cr);
                    retrying = null;
                    attempts = 0;
                    backoff  = TimeSpan.FromSeconds(1);
                    continue;
                }

                _logger.LogError(ex,
                    "Processing failed for {Topic}[{P}]@{O} (attempt {Attempt}/{Max}) — seeking back " +
                    "to that offset and retrying in {B}s",
                    cr.Topic, cr.Partition.Value, cr.Offset.Value,
                    attempts, _settings.MaxDeliveryAttempts, backoff.TotalSeconds);

                // Rewind. Declining to commit is not enough on its own: the fetch
                // position has already moved past this message, so without the seek the
                // next Consume returns the one after it and the next successful commit
                // buries this offset permanently.
                try
                {
                    consumer.Seek(cr.TopicPartitionOffset);
                }
                catch (KafkaException seekEx)
                {
                    // The partition was revoked underneath us — a rebalance. Whoever
                    // picks it up resumes from the last committed offset, which is
                    // before this message, so redelivery still happens.
                    _logger.LogWarning(seekEx,
                        "Could not seek back to {Topic}[{P}]@{O}; the partition was most likely " +
                        "reassigned. The new owner resumes from the last commit, which is before " +
                        "this message.",
                        cr.Topic, cr.Partition.Value, cr.Offset.Value);
                }

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

    /// <summary>
    /// How many times one message may be re-delivered to ProcessAsync before the
    /// consumer commits past it and accepts the loss.
    ///
    /// The bound exists because the seek-on-failure that makes redelivery real also
    /// makes a permanently-bad message an infinite loop, and a consumer wedged on offset
    /// 4,812 forever is a worse outage than a hole in the table: everything BEHIND it
    /// stops arriving too.
    ///
    /// 10 at the loop's capped 60-second backoff is roughly eight minutes of retrying,
    /// which comfortably outlasts a Postgres restart or a partition-creation race — the
    /// transient failures actually seen here — while still ending. Giving up logs the
    /// payload at Critical; it is never silent.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 10;
}
