using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using CryptoDecision.ApiService.Application;
using CryptoDecision.ApiService.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CryptoDecision.ApiService.Infrastructure.Services;

/// <summary>
/// Kafka consumer that reads triggered alert notifications from <c>alerts.notifications</c>
/// and broadcasts them to connected WebSocket clients via SignalR.
///
/// Interview talking points:
///   - Inter-service communication via Kafka (AlertService → ApiService)
///   - Separate consumer group (api-alert-group) for independent consumption
///   - Fan-out pattern: one Kafka message → broadcast to all connected clients
///   - Decoupled: AlertService doesn't need to know about SignalR/WebSocket
/// </summary>
public sealed class AlertBroadcastService(
    IHubContext<MarketHub, IMarketClient> hub,
    IConfiguration configuration,
    ILogger<AlertBroadcastService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var topic = configuration["Kafka:AlertNotificationTopic"] ?? "alerts.notifications";
        var bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "kafka:9092";

        var config = new ConsumerConfig
        {
            BootstrapServers  = bootstrapServers,
            GroupId           = "api-alert-group",
            AutoOffsetReset   = AutoOffsetReset.Latest,
            EnableAutoCommit  = false,
            MaxPollIntervalMs = 300_000,
            SessionTimeoutMs  = 45_000,
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                logger.LogError("Alert broadcast consumer error: {Code} {Reason}", e.Code, e.Reason))
            .Build();

        consumer.Subscribe(topic);
        logger.LogInformation("AlertBroadcastService subscribed to: {Topic}", topic);

        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (cr is null) continue;

                var notification = JsonSerializer.Deserialize(cr.Message.Value,
                    AlertBroadcastJsonContext.Default.KafkaAlertNotification);

                if (notification is null)
                {
                    consumer.Commit(cr);
                    continue;
                }

                var dto = new AlertTriggeredDto(
                    AlertId:     notification.AlertId,
                    UserId:      notification.UserId,
                    Symbol:      notification.Symbol,
                    Condition:   notification.Condition,
                    TargetPrice: notification.TargetPrice,
                    ActualPrice: notification.ActualPrice,
                    Note:        notification.Note,
                    TriggeredAt: DateTime.UtcNow
                );

                // Broadcast to ALL clients subscribed to this symbol's momentum group
                await hub.Clients
                    .Group(MarketHub.GroupName(notification.Symbol, "ALL"))
                    .ReceiveAlertTriggered(dto);

                // Also broadcast per-exchange groups
                foreach (var exchange in new[] { "BINANCE", "BYBIT", "OKX" })
                {
                    await hub.Clients
                        .Group(MarketHub.GroupName(notification.Symbol, exchange))
                        .ReceiveAlertTriggered(dto);
                }

                consumer.Commit(cr);
                backoff = TimeSpan.FromSeconds(1);

                logger.LogInformation(
                    "Broadcasted alert #{AlertId}: {Symbol} {Condition} {TargetPrice} (actual: {ActualPrice})",
                    notification.AlertId, notification.Symbol, notification.Condition,
                    notification.TargetPrice, notification.ActualPrice);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Alert broadcast consume error — retrying in {B}s", backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 60_000));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alert broadcast processing error");
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 60_000));
            }
        }

        consumer.Close();
        logger.LogInformation("AlertBroadcastService stopped");
    }
}

// Kafka message model (matches AlertService's AlertNotification record)
public sealed record KafkaAlertNotification(
    long AlertId,
    string? UserId,
    string Symbol,
    string Condition,
    decimal TargetPrice,
    decimal ActualPrice,
    string? Note,
    DateTimeOffset TriggeredAt
);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KafkaAlertNotification))]
internal partial class AlertBroadcastJsonContext : JsonSerializerContext;
