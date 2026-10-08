using CryptoDecision.Shared.Signals;
using NpgsqlTypes;
using Npgsql;

namespace CryptoDecision.BotService.Infrastructure;

public interface IFlowBarRepository
{
    /// <summary>
    /// Per-venue 15-minute flow buckets, ascending, ending with the most recent
    /// bucket that has closed.
    /// </summary>
    Task<FlowBarSet> GetRecentAsync(string symbol, int bars, CancellationToken ct = default);

    /// <summary>Recent 1-minute candles, ascending, for volatility measurement.</summary>
    Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(
        string symbol, int minutes, CancellationToken ct = default);

    /// <summary>
    /// Buy and sell notional in the 15-minute bucket CURRENTLY IN PROGRESS, summed
    /// straight off raw trades up to this instant.
    ///
    /// Everything else in this interface deliberately refuses to look at the open
    /// bucket, because a partial bucket is a moving number and a RATIO computed from
    /// one is noise. This method exists because SIZE is not a ratio: $49M of prints is
    /// $49M whether the bucket has finished or not, and waiting to be told so costs
    /// the whole event.
    ///
    /// 2026-10-08 is why. The 15:15 bucket traded $49.51M — 2.5x the news-print
    /// threshold. CANDLE_REVERSAL opened a LONG at 15:15:51, 51 seconds into it, and
    /// was stopped out at 15:23:57 for -$0.6228, the largest loss in the live book.
    /// The high-volume suspension that should have blocked that entry could not fire:
    /// it waits for XVENUE_FLOW to hold a position, XVENUE_FLOW reads only closed and
    /// settled buckets, and that bucket did not close until 15:30 or settle until
    /// ~15:33. Eighteen minutes of structural latency against an eight-minute crash.
    /// The trade was opened and killed inside the very bucket whose volume was the
    /// reason not to open it.
    /// </summary>
    Task<LiveBucketVolume> GetLiveBucketVolumeAsync(
        string symbol, CancellationToken ct = default);
}

/// <summary>
/// Volume accumulated so far in the bucket currently in progress.
/// </summary>
/// <param name="BucketStart">Start of the bucket these totals belong to.</param>
/// <param name="ElapsedMinutes">How far into the bucket the reading was taken.</param>
public sealed record LiveBucketVolume(
    DateTime BucketStart,
    decimal  BuyUsd,
    decimal  SellUsd,
    double   ElapsedMinutes)
{
    public decimal TotalUsd => BuyUsd + SellUsd;

    /// <summary>
    /// Imbalance in [-1, +1], the same definition <see cref="FlowBar.Ofi"/> uses so a
    /// live reading and a settled one cannot mean different things.
    /// </summary>
    public double Ofi => TotalUsd > 0m
        ? (double)((BuyUsd - SellUsd) / TotalUsd)
        : 0.0;

    public static LiveBucketVolume Empty(DateTime bucketStart) =>
        new(bucketStart, 0m, 0m, 0.0);
}

/// <summary>
/// The flow bars available for one decision, plus how current they are.
/// </summary>
/// <param name="LatestBucket">
/// Start of the newest bucket found across all venues, or null when there are none.
/// </param>
public sealed record FlowBarSet(
    IReadOnlyDictionary<string, IReadOnlyList<FlowBar>> ByVenue,
    DateTime? LatestBucket)
{
    public static readonly FlowBarSet Empty =
        new(new Dictionary<string, IReadOnlyList<FlowBar>>(), null);

    public int VenueCount => ByVenue.Count;

    /// <summary>
    /// How far behind the current clock the newest bucket is.
    ///
    /// Exists because the previous incarnation of this data path had no staleness
    /// concept at all: the bot read "the latest prediction" with no upper bound on
    /// its age, so a prediction service that had been dead for days still carried
    /// full weight in every entry decision, and every health check stayed green.
    /// A signal has to be able to say "I do not know yet".
    /// </summary>
    public TimeSpan Age(DateTime nowUtc) =>
        LatestBucket is { } latest ? nowUtc - latest.AddMinutes(15) : TimeSpan.MaxValue;
}

/// <summary>
/// Reads flow_bars_15m and klines_1m for the live strategy.
///
/// Deliberately thin: it fetches rows and shapes them into the same types the
/// backtester loads, and does no scoring. All judgement lives in
/// <see cref="CrossVenueFlowScorer"/>, which is what lets the live path and the
/// offline path be the same arithmetic rather than two implementations that drift.
/// </summary>
public sealed class FlowBarRepository(NpgsqlDataSource dataSource) : IFlowBarRepository
{
    public async Task<FlowBarSet> GetRecentAsync(
        string symbol, int bars, CancellationToken ct = default)
    {
        // The newest N buckets *per venue*, not the buckets from the last N × 15
        // minutes.
        //
        // The time-bounded version was wrong about what a baseline is. It measures the
        // distribution a venue's imbalance normally has — how wide, where centred —
        // and that is a property of the venue, not of the last day. Bounding it by
        // recency meant a gap in the history could not be stepped over: with 114
        // usable buckets sitting in the table from two days earlier, the strategy
        // still refused for want of 100, and the only way past it was to wait 21 hours
        // for the window to refill with data no better than what was already there.
        //
        // What genuinely has to be current is the signal window — the last few buckets
        // the verdict is computed from — and that is guarded separately by MaxBarAge on
        // FlowBarSet.Age. Splicing across a gap costs a handful of bogus rolling
        // samples where one window straddles the seam, which is precisely what the
        // median and MAD in the scorer are chosen to absorb.
        //
        // Per venue via ROW_NUMBER rather than a plain LIMIT: a flat limit returns the
        // N newest rows overall, which on a symbol where Binance prints ten times as
        // often as Bybit is mostly Binance, and the cross-venue comparison silently
        // becomes a single-venue one.
        const string sql = """
            SELECT exchange, bucket_start, buy_volume_usd, sell_volume_usd,
                   buy_count, sell_count, max_buy_usd, max_sell_usd, vwap
            FROM (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY exchange ORDER BY bucket_start DESC) AS rn
                FROM flow_bars_15m
                WHERE symbol = @symbol
                  AND bucket_start < @openBucket
            ) ranked
            WHERE rn <= @bars
            ORDER BY exchange, bucket_start
            """;

        // The bucket in progress is excluded here, in SQL, rather than trimmed after
        // the fact. Doing it in the query is what makes the row limit mean "N usable
        // buckets" — trimming afterwards would have silently returned N-1.
        var openBucket = Buckets.FloorUtc(DateTime.UtcNow);

        var byVenue = new Dictionary<string, List<FlowBar>>(StringComparer.OrdinalIgnoreCase);
        DateTime? latest = null;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("bars", bars);
        cmd.Parameters.AddWithValue("openBucket", new DateTimeOffset(
            DateTime.SpecifyKind(openBucket, DateTimeKind.Utc)));

        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            var exchange = r.GetString(0);
            var bucket   = r.GetDateTime(1);

            if (!byVenue.TryGetValue(exchange, out var list))
                byVenue[exchange] = list = new List<FlowBar>();

            list.Add(new FlowBar(
                Exchange:      exchange,
                BucketStart:   bucket,
                BuyVolumeUsd:  r.GetDecimal(2),
                SellVolumeUsd: r.GetDecimal(3),
                BuyCount:      r.GetInt32(4),
                SellCount:     r.GetInt32(5),
                MaxBuyUsd:     r.GetDecimal(6),
                MaxSellUsd:    r.GetDecimal(7),
                Vwap:          r.GetDecimal(8)));

            if (latest is null || bucket > latest) latest = bucket;
        }

        if (byVenue.Count == 0) return FlowBarSet.Empty;

        // No post-fetch trimming: the query already excluded the bucket in progress.
        // A second copy of that rule here would be a second place for it to drift, and
        // the two disagreeing is how the row limit would quietly start meaning N-1.
        var result = byVenue.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<FlowBar>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

        return new FlowBarSet(result, latest);
    }

    public async Task<LiveBucketVolume> GetLiveBucketVolumeAsync(
        string symbol, CancellationToken ct = default)
    {
        // The buy/sell split is NOT re-derived here. `NOT is_buyer_maker` is the taker
        // buying, and that is the convention sql/017 aggregates flow_bars_15m with and
        // every IngestionService normaliser maps onto. Writing the condition a second
        // time is how a live reading and a settled one come to disagree about which
        // side a print was on, so it is copied rather than reasoned out.
        //
        // Across all venues, matching how the strategy sums settled buckets. Partitions
        // are pruned by trade_time, so this touches at most today's and yesterday's.
        // ROLLING 15 minutes, not the aligned bucket.
        //
        // Aligned was the first version and it has a hole: a spike at 15:23 is
        // forgotten at 15:30 when the bucket rolls. On 2026-10-08 the $49.51M landed in
        // the 15:15 bucket and CANDLE_REVERSAL opened trade #184 at 15:31:01 — 61
        // seconds into the next bucket, with the accumulator back at zero and the
        // cascade one minute behind it. A rolling window still sees it.
        //
        // Fifteen minutes because that is the width the $20M threshold was calibrated
        // on. Changing the window without changing the threshold would silently change
        // what the rule means.
        const string sql = """
            SELECT COALESCE(SUM(quote_qty) FILTER (WHERE NOT is_buyer_maker), 0),
                   COALESCE(SUM(quote_qty) FILTER (WHERE     is_buyer_maker), 0)
            FROM trades
            WHERE symbol = @symbol
              AND trade_time >= @since
            """;

        var now   = DateTime.UtcNow;
        var since = now.AddMinutes(-15);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("since",  NpgsqlDbType.TimestampTz, since);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return LiveBucketVolume.Empty(since);

        return new LiveBucketVolume(
            BucketStart:    since,
            BuyUsd:         r.GetDecimal(0),
            SellUsd:        r.GetDecimal(1),
            ElapsedMinutes: 15.0);
    }

    public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(
        string symbol, int minutes, CancellationToken ct = default)
    {
        const string sql = """
            SELECT open_time, open_price, high_price, low_price, close_price
            FROM klines_1m
            WHERE symbol = @symbol
              AND open_time >= @from
            ORDER BY open_time
            """;

        var from = DateTime.UtcNow.AddMinutes(-minutes);
        var candles = new List<Candle>();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("from", new DateTimeOffset(
            DateTime.SpecifyKind(from, DateTimeKind.Utc)));

        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
            candles.Add(new Candle(
                r.GetDateTime(0), r.GetDecimal(1), r.GetDecimal(2),
                r.GetDecimal(3), r.GetDecimal(4)));

        return candles;
    }

}
