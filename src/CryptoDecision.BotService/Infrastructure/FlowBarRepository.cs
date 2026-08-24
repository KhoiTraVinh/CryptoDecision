using CryptoDecision.Shared.Signals;
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
        var openBucket = FloorTo15Minutes(DateTime.UtcNow);

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

    internal static DateTime FloorTo15Minutes(DateTime utc)
    {
        var quarter = TimeSpan.FromMinutes(15).Ticks;
        return new DateTime(utc.Ticks - utc.Ticks % quarter, DateTimeKind.Utc);
    }
}
