using System.Diagnostics.Metrics;

namespace CryptoDecision.IngestionService.Telemetry;

/// <summary>
/// Custom metrics for the IngestionService.
/// Exposes counters and histograms that Prometheus scrapes every 15 seconds.
/// All instruments use System.Diagnostics.Metrics — the OTel SDK picks them up
/// automatically via the "CryptoDecision.Ingestion" meter name.
/// </summary>
public sealed class IngestionMetrics : IDisposable
{
    public static readonly string MeterName = "CryptoDecision.Ingestion";

    private readonly Meter _meter;

    /// <summary>Total trades received from all exchanges.</summary>
    public readonly Counter<long> TradesIngested;

    /// <summary>Distribution of Kafka batch sizes per exchange.</summary>
    public readonly Histogram<double> BatchSize;

    public IngestionMetrics()
    {
        _meter         = new Meter(MeterName, "1.0.0");
        TradesIngested = _meter.CreateCounter<long>(
            "trades_ingested_total",
            unit:        "trades",
            description: "Total number of trades ingested per exchange and symbol");
        BatchSize      = _meter.CreateHistogram<double>(
            "trades_batch_size",
            unit:        "trades",
            description: "Number of trades per Kafka batch flush");
    }

    /// <summary>
    /// Register a channel depth gauge. Call once per channel in Program.cs after DI is built.
    /// ObservableGauge pulls the value on each Prometheus scrape — zero background overhead.
    /// </summary>
    public void RegisterChannelDepth(string channelName, Func<int> getDepth)
    {
        _meter.CreateObservableGauge<int>(
            $"channel_depth_{channelName}",
            observeValue:  getDepth,
            unit:          "trades",
            description:   $"Number of pending items in the {channelName} channel");
    }

    public void Dispose() => _meter.Dispose();
}
