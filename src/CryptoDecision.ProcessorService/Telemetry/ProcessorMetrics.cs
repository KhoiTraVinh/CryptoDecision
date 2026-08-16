using System.Diagnostics.Metrics;

namespace CryptoDecision.ProcessorService.Telemetry;

/// <summary>
/// Custom metrics for the ProcessorService.
/// All instruments use System.Diagnostics.Metrics — the OTel SDK collects them
/// and exposes them on port 9090 for Prometheus to scrape.
/// </summary>
public sealed class ProcessorMetrics : IDisposable
{
    public static readonly string MeterName = "CryptoDecision.Processor";

    private readonly Meter _meter;

    /// <summary>Rows successfully inserted into the trades table.</summary>
    public readonly Counter<long> TradesPersisted;

    /// <summary>Rows skipped because (exchange, trade_id, trade_time) already existed.</summary>
    public readonly Counter<long> DuplicatesSkipped;

    /// <summary>Duration of each BulkInsert operation (COPY + ON CONFLICT INSERT).</summary>
    public readonly Histogram<double> BulkInsertDurationMs;

    /// <summary>Daily feature rows upserted per symbol.</summary>
    public readonly Counter<long> FeaturesComputed;

    public ProcessorMetrics()
    {
        _meter               = new Meter(MeterName, "1.0.0");
        TradesPersisted      = _meter.CreateCounter<long>(
            "trades_persisted_total",
            unit:        "trades",
            description: "Total trades inserted into PostgreSQL per exchange");
        DuplicatesSkipped    = _meter.CreateCounter<long>(
            "trades_duplicates_skipped_total",
            unit:        "trades",
            description: "Trades deduplicated (ON CONFLICT DO NOTHING) per exchange");
        BulkInsertDurationMs = _meter.CreateHistogram<double>(
            "bulk_insert_duration_ms",
            unit:        "ms",
            description: "End-to-end duration of COPY + idempotent INSERT per batch");
        FeaturesComputed     = _meter.CreateCounter<long>(
            "features_computed_total",
            unit:        "rows",
            description: "Daily feature rows upserted per symbol");
    }

    public void Dispose() => _meter.Dispose();
}
