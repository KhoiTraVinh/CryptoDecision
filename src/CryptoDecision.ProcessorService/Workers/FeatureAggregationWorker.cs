using CryptoDecision.ProcessorService.Persistence;
using CryptoDecision.ProcessorService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CryptoDecision.ProcessorService.Workers;

/// <summary>
/// Daily-feature aggregation and trade-partition lifecycle.
///
///   1. Recompute today's daily_feature_table row, hourly. Yesterday's row is
///      refreshed once when the UTC day rolls over, not on every pass.
///   2. Create the next two days' trade partitions before they are needed.
///   3. Drop partitions past the raw-trade retention window.
///
/// The cadence is the point. Every feature run aggregates a whole day of `trades`,
/// and this deployment prints ~2.36M rows a day; the previous five-minute interval
/// over two dates was scanning ~57M rows an hour to refresh a number nothing reads
/// more than hourly. On a 2-core host that was the single largest CPU consumer in
/// the stack, larger than LLM inference.
/// </summary>
public sealed class FeatureAggregationWorker(
    FeatureRepository featureRepo,
    DatabaseInitializer dbInit,
    NpgsqlDataSource dataSource,
    ProcessorMetrics metrics,
    IOptions<FeatureSettings> settings,
    ILogger<FeatureAggregationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FeatureAggregationWorker starting");

        // Run once at startup (catch up if service was down)
        await RunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(settings.Value.AggregationInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunCycleAsync(stoppingToken);
    }

    private DateOnly _yesterdayRefreshedFor = DateOnly.MinValue;

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);
        var symbols  = settings.Value.Symbols;

        foreach (var symbol in symbols)
        {
            try
            {
                await featureRepo.UpsertDailyFeatureAsync(symbol, today, ct);
                metrics.FeaturesComputed.Add(1, new KeyValuePair<string, object?>("symbol", symbol));

                // Yesterday is refreshed once, on the first cycle of a new UTC day, not
                // on every cycle.
                //
                // Each call aggregates a whole day of `trades`, and this symbol prints
                // about 2.36 million rows a day across three venues. Doing both dates
                // every five minutes was scanning roughly 57 million rows an hour to
                // recompute a figure that changes only when a late trade lands — and on
                // a 2-core host that background load is larger than everything else in
                // the stack put together, including LLM inference.
                //
                // Late trades arrive within seconds of their bucket, not hours, so one
                // pass after the day rolls over catches them all.
                if (_yesterdayRefreshedFor != today)
                {
                    await featureRepo.UpsertDailyFeatureAsync(symbol, today.AddDays(-1), ct);
                    logger.LogInformation(
                        "Refreshed yesterday's features for {Symbol} after the UTC day rolled over.",
                        symbol);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Feature aggregation failed for {Symbol} {Date}", symbol, today);
            }
        }

        _yesterdayRefreshedFor = today;

        // ── Partition lifecycle ───────────────────────────────────────────────
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await dbInit.EnsureDailyPartitionAsync(conn, DateTime.UtcNow.Date.AddDays(1), ct);
            await dbInit.EnsureDailyPartitionAsync(conn, DateTime.UtcNow.Date.AddDays(2), ct);

            await DropExpiredPartitionsAsync(conn, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Partition maintenance failed");
        }
    }

    /// <summary>
    /// Drop trade partitions past the retention window.
    ///
    /// <c>drop_old_trade_partitions</c> has existed in sql/002_partition_helpers.sql
    /// since the beginning and was never called once, which is the only reason
    /// `trades` grew without bound. At the measured rate — 2.36 million rows a day,
    /// roughly 354 MB with indexes — that is about 10.6 GB a month, on a host whose
    /// whole disk budget is smaller than a quarter's worth.
    ///
    /// Raw trades are kept rather than discarded outright because they are still doing
    /// three jobs no aggregate can: they carry the dedup index that makes Kafka's
    /// at-least-once redelivery safe, they let a bucket be recomputed when its
    /// definition changes (which has already happened twice), and they are the only
    /// way to answer "why did we enter here" down to the print. A rolling window keeps
    /// all three and still bounds the table — flow_bars_15m, at 288 rows a day, is what
    /// gets kept forever.
    /// </summary>
    private async Task DropExpiredPartitionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var retainDays = settings.Value.RawTradeRetentionDays;

        if (retainDays <= 0)
        {
            logger.LogDebug("Raw trade retention is disabled (RawTradeRetentionDays={Days}).", retainDays);
            return;
        }

        await using var cmd = new NpgsqlCommand("SELECT drop_old_trade_partitions(@days)", conn);
        cmd.Parameters.AddWithValue("days", retainDays);

        var dropped = await cmd.ExecuteScalarAsync(ct);
        var count   = dropped is int i ? i : 0;

        if (count > 0)
            logger.LogInformation(
                "Dropped {Count} trade partition(s) older than {Days} days.", count, retainDays);
    }
}

public sealed class FeatureSettings
{
    public const string Section   = "Feature";

    /// <summary>
    /// Symbols to compute daily features for. Empty by default, and it has to stay
    /// that way.
    ///
    /// The .NET configuration binder appends to a collection that already holds items
    /// rather than replacing it, so the old default of ["BTCUSDT", "ETHUSDT"] plus
    /// appsettings naming ["SOLUSDT"] produced all three. That was live: daily rows
    /// were being written for BTCUSDT and ETHUSDT, symbols this deployment has
    /// ingested no trades or candles for since it narrowed to SOL, so every one of
    /// those rows is structurally zero.
    ///
    /// It was not merely wasteful. train.py's query deliberately does not filter by
    /// symbol, so XGBoost would have trained on those all-zero rows as though they
    /// were market observations, and readiness() reports the daily sample rate as
    /// COUNT(DISTINCT symbol) — so it claimed three samples a day when one symbol
    /// had data, and the "days until trainable" estimate was a third of the truth.
    /// </summary>
    public string[] Symbols       { get; set; } = [];

    /// <summary>
    /// How often to recompute today's daily features. Was 5 minutes.
    ///
    /// Each run aggregates a full day of trades. Nothing downstream needs this figure
    /// fresher than an hour: with XVENUE_FLOW active, daily_feature_table feeds one
    /// volatility read in RiskEngine and the (currently disabled) prediction service.
    /// The signal itself comes from flow_bars_15m, which has its own two-minute worker.
    /// </summary>
    public TimeSpan AggregationInterval { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Days of raw trades to keep before dropping the partition. Zero disables.
    ///
    /// Seven days at the measured 354 MB/day holds `trades` at about 2.5 GB steady
    /// state instead of growing 10.6 GB a month — while leaving a week to notice a bad
    /// bucket definition and recompute flow_bars_15m from source.
    /// </summary>
    public int RawTradeRetentionDays { get; set; } = 7;

    /// <summary>Throw rather than compute features for nothing, silently.</summary>
    public void Validate()
    {
        if (Symbols.Length == 0)
            throw new InvalidOperationException(
                $"No symbols configured for feature aggregation. Set {Section}:Symbols to at least " +
                "one symbol, e.g. [\"SOLUSDT\"].");
    }
}
