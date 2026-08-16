using System.Threading.Channels;
using CryptoDecision.IngestionService.Kafka;
using CryptoDecision.IngestionService.Models;
using CryptoDecision.IngestionService.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoDecision.IngestionService.Workers;

/// <summary>
/// Abstract base for exchange-specific Kafka trade batch publishers.
///
/// Reads trades from a ChannelReader, micro-batches by symbol, and flushes to Kafka.
/// Flush triggers: BatchSize accumulation OR FlushInterval elapsed (whichever first).
///
/// Thread safety: _buffers and _flushDue are guarded by _lock because
/// RunTimerFlushAsync and the ReadAllAsync loop run as concurrent Tasks.
/// </summary>
public abstract class KafkaTradePublisherBase(
    ChannelReader<Trade> reader,
    KafkaProducerService producer,
    IngestionMetrics metrics,
    IOptions<BatchSettings> batchSettings,
    ILogger logger) : BackgroundService
{
    private readonly BatchSettings _batch = batchSettings.Value;
    private readonly Dictionary<string, List<Trade>> _buffers
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _flushDue
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    protected abstract string ExchangeName { get; }
    protected abstract string GetTopic(string symbol);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Worker} starting (exchange={Exchange})", GetType().Name, ExchangeName);

        var timer     = new PeriodicTimer(_batch.FlushInterval);
        var timerTask = RunTimerFlushAsync(timer, stoppingToken);

        await foreach (var trade in reader.ReadAllAsync(stoppingToken))
        {
            bool shouldFlush;
            lock (_lock)
            {
                if (!_buffers.TryGetValue(trade.Symbol, out var buf))
                {
                    buf = new List<Trade>(_batch.BatchSize);
                    _buffers[trade.Symbol] = buf;
                    _flushDue[trade.Symbol] = DateTimeOffset.UtcNow.Add(_batch.FlushInterval);
                }
                buf.Add(trade);
                shouldFlush = buf.Count >= _batch.BatchSize ||
                              DateTimeOffset.UtcNow >= _flushDue[trade.Symbol];
            }

            if (shouldFlush)
                await FlushSymbolAsync(trade.Symbol, stoppingToken);
        }

        // Drain remaining buffered trades on shutdown
        string[] remaining;
        lock (_lock) { remaining = _buffers.Keys.ToArray(); }
        foreach (var symbol in remaining)
            await FlushSymbolAsync(symbol, CancellationToken.None);

        timer.Dispose();
        await timerTask;

        logger.LogInformation("{Worker} stopped", GetType().Name);
    }

    private async Task RunTimerFlushAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            string[] symbols;
            lock (_lock) { symbols = _buffers.Keys.ToArray(); }
            foreach (var symbol in symbols)
                await FlushSymbolAsync(symbol, ct);
        }
    }

    private async Task FlushSymbolAsync(string symbol, CancellationToken ct)
    {
        List<Trade> snapshot;
        lock (_lock)
        {
            if (!_buffers.TryGetValue(symbol, out var buf) || buf.Count == 0) return;
            snapshot = buf.ToList();
            buf.Clear();
            _flushDue[symbol] = DateTimeOffset.UtcNow.Add(_batch.FlushInterval);
        }

        var batch = new TradeBatch(
            Exchange:       ExchangeName,
            Symbol:         symbol,
            BatchTimestamp: DateTimeOffset.UtcNow,
            Trades:         snapshot);

        var topic = GetTopic(symbol);
        await producer.PublishAsync(topic, key: symbol, batch, ct);

        // Record metrics — atomic increment, zero lock contention
        metrics.TradesIngested.Add(snapshot.Count,
            new("exchange", ExchangeName),
            new("symbol",   symbol));
        metrics.BatchSize.Record(snapshot.Count,
            new("exchange", ExchangeName),
            new("symbol",   symbol));

        logger.LogDebug(
            "Flushed {Count} {Exchange}/{Symbol} trades to {Topic}",
            snapshot.Count, ExchangeName, symbol, topic);
    }
}
