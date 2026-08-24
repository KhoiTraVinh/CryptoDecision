using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CryptoDecision.ProcessorService.Workers;

/// <summary>
/// Keeps flow_bars_15m current from the raw `trades` stream.
///
/// Why a worker rather than a live query
/// -------------------------------------
/// Every order-flow read in this stack used to be an ad-hoc scan of `trades`
/// bounded to the trailing hour, which capped the lookback at an hour and left
/// nothing on disk to backtest against. Materialising clock-aligned 15-minute
/// buckets makes the trailing baseline a cheap range scan and makes the signal
/// reproducible offline — the same rows the live strategy reads are the rows the
/// backtester replays.
///
/// Why it recomputes a trailing window every cycle
/// -----------------------------------------------
/// Trades arrive late. A batch can be flushed seconds after its bucket closed, a
/// Kafka consumer can lag, and a reconnect can backfill. Recomputing the last few
/// buckets from `trades` on every pass folds all of that in, and because
/// upsert_flow_bars_15m replaces a bucket rather than adding to it, doing so
/// repeatedly is safe. Appending would have made every re-run double-count.
/// </summary>
public sealed class FlowBarAggregationWorker(
    NpgsqlDataSource dataSource,
    IOptions<FlowBarSettings> settings,
    ILogger<FlowBarAggregationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = settings.Value;

        logger.LogInformation(
            "FlowBarAggregationWorker starting — symbols [{Symbols}], every {Interval}, " +
            "recomputing the trailing {Lookback}",
            string.Join(", ", cfg.Symbols), cfg.Interval, cfg.RecomputeWindow);

        // Backfill before the first live pass, so a fresh deployment (or one that was
        // down for a while) has a usable baseline immediately rather than after
        // 24 hours of buckets accumulate. Bounded and idempotent; skipped when the
        // table already covers the range.
        await BackfillAsync(cfg, stoppingToken);

        using var timer = new PeriodicTimer(cfg.Interval);

        do
        {
            await RunCycleAsync(cfg, stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(FlowBarSettings cfg, CancellationToken ct)
    {
        // Aligned to the bucket grid rather than to "now minus an interval": a range
        // that starts mid-bucket would recompute that bucket from a partial slice of
        // its own trades and write the truncated result over the complete one.
        var to   = FloorTo15Minutes(DateTime.UtcNow).AddMinutes(15);
        var from = to - cfg.RecomputeWindow;

        foreach (var symbol in cfg.Symbols)
        {
            try
            {
                var rows = await UpsertAsync(symbol, from, to, ct);
                logger.LogDebug(
                    "flow_bars_15m: {Rows} bucket(s) written for {Symbol} over {From:HH:mm}-{To:HH:mm}",
                    rows, symbol, from, to);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal by design, but never silent. The strategy downstream
                // abstains when its bars are too few or too stale, so a failure here
                // degrades into inaction rather than into a bad trade — and the log
                // is the only thing that distinguishes "no signal" from "no data".
                logger.LogError(ex,
                    "flow_bars_15m aggregation failed for {Symbol} over {From}-{To}. The strategy " +
                    "will abstain rather than read a stale baseline.", symbol, from, to);
            }
        }
    }

    private async Task BackfillAsync(FlowBarSettings cfg, CancellationToken ct)
    {
        if (cfg.BackfillDays <= 0) return;

        var to   = FloorTo15Minutes(DateTime.UtcNow).AddMinutes(15);
        var from = to.AddDays(-cfg.BackfillDays);

        foreach (var symbol in cfg.Symbols)
        {
            try
            {
                // Walk day by day rather than issuing one query across the whole
                // range. The trades table is partitioned daily and holds hundreds of
                // millions of rows; a single multi-day aggregate holds one long
                // transaction open and spikes memory on a host that has already had
                // Docker killed by memory pressure once.
                var written = 0L;

                for (var day = from.Date; day < to; day = day.AddDays(1))
                {
                    var dayEnd = day.AddDays(1);
                    if (dayEnd > to) dayEnd = to;

                    written += await UpsertAsync(symbol, day, dayEnd, ct);
                }

                logger.LogInformation(
                    "flow_bars_15m backfill complete for {Symbol}: {Rows} bucket(s) over the last " +
                    "{Days} day(s).", symbol, written, cfg.BackfillDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "flow_bars_15m backfill failed for {Symbol}. Live aggregation will still run, " +
                    "but the trailing baseline will be short until enough buckets accumulate — the " +
                    "strategy abstains while that is true.", symbol);
            }
        }
    }

    private async Task<long> UpsertAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        const string sql = "SELECT upsert_flow_bars_15m(@symbol, @from, @to)";

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("from", new DateTimeOffset(
            DateTime.SpecifyKind(from, DateTimeKind.Utc)));
        cmd.Parameters.AddWithValue("to", new DateTimeOffset(
            DateTime.SpecifyKind(to, DateTimeKind.Utc)));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i ? i : 0L;
    }

    /// <summary>
    /// Floor a timestamp onto the 15-minute grid the buckets are keyed by.
    ///
    /// The grid is part of the signal, not a storage detail: order-flow imbalance
    /// measured at quarter-hour boundaries carries documented predictive content over
    /// the following hours, and the same imbalance measured on an arbitrary offset
    /// does not. Ticks arithmetic rather than minute subtraction so seconds and
    /// sub-second precision are dropped too.
    /// </summary>
    internal static DateTime FloorTo15Minutes(DateTime utc)
    {
        var quarter = TimeSpan.FromMinutes(15).Ticks;
        return new DateTime(utc.Ticks - utc.Ticks % quarter, DateTimeKind.Utc);
    }
}

public sealed class FlowBarSettings
{
    public const string Section = "FlowBars";

    /// <summary>
    /// Symbols to aggregate. Empty by default, and it has to stay that way.
    ///
    /// The .NET configuration binder <em>appends</em> to a collection property that
    /// already holds items rather than replacing it, so a non-empty default plus a
    /// configured value yields both. With a default of ["SOLUSDT"] and appsettings
    /// also naming SOLUSDT, this ran the whole backfill twice on the first boot —
    /// harmless only because the upsert is idempotent. MarketSubscriptionSettings
    /// carries a comment about this exact trap, from the time it subscribed to
    /// SOL-USDT twice and double-counted every trade in the momentum flow, and this
    /// class walked straight into it anyway.
    /// </summary>
    public string[] Symbols { get; set; } = [];

    /// <summary>
    /// Throw when nothing is configured rather than idling silently.
    ///
    /// An empty list means no buckets are ever written, which downstream looks
    /// identical to a market with no trades: the strategy abstains on
    /// FLOW_BARS_STALE, the bot reports RUNNING, every health check stays green, and
    /// nothing anywhere says the aggregation was never asked to run.
    /// </summary>
    public void Validate()
    {
        if (Symbols.Length == 0)
            throw new InvalidOperationException(
                $"No symbols configured for flow-bar aggregation. Set {Section}:Symbols to at " +
                "least one symbol, e.g. [\"SOLUSDT\"]. Without it flow_bars_15m is never written " +
                "and the strategy abstains forever with nothing reporting why.");
    }

    /// <summary>
    /// How often to fold new and late-arriving trades into the buckets. Well under
    /// the 15-minute bucket width so the current bucket is never more than this stale
    /// when the strategy reads it.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How far back each pass recomputes. Wide enough to absorb consumer lag and a
    /// short outage; narrow enough that a pass stays a partition-pruned scan.
    /// </summary>
    public TimeSpan RecomputeWindow { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Days of history to build on startup. Zero disables it.
    ///
    /// Needs to comfortably exceed the strategy's baseline window — 96 buckets is
    /// 24 hours — or the strategy abstains on "baseline too short" for its first day
    /// after every deployment, which is indistinguishable from a broken signal unless
    /// someone reads the abstain code.
    /// </summary>
    public int BackfillDays { get; set; } = 30;
}
