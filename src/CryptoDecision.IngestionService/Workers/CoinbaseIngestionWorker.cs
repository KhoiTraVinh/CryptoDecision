using CryptoDecision.IngestionService.Channels;
using CryptoDecision.IngestionService.Coinbase;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Owns the Coinbase WebSocket connection lifecycle and routes trades via CoinbaseTradeChannel.
/// </summary>
public sealed class CoinbaseIngestionWorker(
    CoinbaseWebSocketClient wsClient,
    CoinbaseTradeChannel    tradeChannel,
    ILogger<CoinbaseIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CoinbaseIngestionWorker starting");
        await wsClient.RunAsync(stoppingToken);
        tradeChannel.Writer.TryComplete();
        logger.LogInformation("CoinbaseIngestionWorker stopped");
    }
}

/// <summary>Kafka batch publisher for Coinbase trades → coinbase.trade.* topics.</summary>
public sealed class CoinbaseKafkaBatchPublisherWorker(
    CoinbaseTradeChannel channel,
    KafkaProducerService producer,
    IngestionMetrics metrics,
    IOptions<BatchSettings> batchSettings,
    ILogger<CoinbaseKafkaBatchPublisherWorker> logger)
    : KafkaTradePublisherBase(channel.Reader, producer, metrics, batchSettings, logger)
{
    protected override string ExchangeName => "COINBASE";

    protected override string GetTopic(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "BTCUSDT" => "coinbase.trade.btcusdt",
            "ETHUSDT" => "coinbase.trade.ethusdt",
            _         => $"coinbase.trade.{symbol.ToLowerInvariant()}"
        };
}
