using System.Text.Json;
using Confluent.Kafka;
using CryptoDecision.AlertService.Models;
using CryptoDecision.AlertService.Serialization;

namespace CryptoDecision.AlertService.Kafka;

/// <summary>
/// Publishes triggered alert notifications to <c>alerts.notifications</c> Kafka topic.
///
/// Interview talking points:
///   - Decouples AlertService from ApiService (doesn't call HTTP directly)
///   - ApiService has its own consumer for this topic → pushes via SignalR
///   - Idempotent producer (exactly-once delivery to broker)
///   - LZ4 compression consistent with other services
/// </summary>
public sealed class AlertNotificationProducer : IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<AlertNotificationProducer> _logger;
    private readonly string _topic;

    public AlertNotificationProducer(
        IConfiguration configuration,
        ILogger<AlertNotificationProducer> logger)
    {
        _logger = logger;
        _topic = configuration["Kafka:NotificationTopic"] ?? "alerts.notifications";

        var config = new ProducerConfig
        {
            BootstrapServers      = configuration["Kafka:BootstrapServers"] ?? "kafka:9092",
            EnableIdempotence     = true,
            Acks                  = Acks.All,
            CompressionType       = CompressionType.Lz4,
            LingerMs              = 5,
            MessageSendMaxRetries = 5,
            RetryBackoffMs        = 200,
            EnableDeliveryReports = true,
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogError("Alert producer error: {Code} {Reason}", e.Code, e.Reason))
            .Build();
    }

    public async Task PublishAsync(AlertNotification notification, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(notification, AlertJsonContext.Default.AlertNotification);
        var msg = new Message<string, string>
        {
            Key = notification.Symbol,
            Value = json
        };

        try
        {
            var result = await _producer.ProduceAsync(_topic, msg, ct);
            _logger.LogInformation(
                "Alert notification published to {Topic}[{Partition}]@{Offset} for alert #{AlertId}",
                _topic, result.Partition.Value, result.Offset.Value, notification.AlertId);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish alert notification for alert #{AlertId}", notification.AlertId);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
