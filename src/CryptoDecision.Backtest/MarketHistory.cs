using CryptoDecision.Shared.Signals;
using Npgsql;

namespace CryptoDecision.Backtest;

/// <summary>Per-venue span and completeness of the flow bars, straight from the view.</summary>
public sealed record CoverageRow(
    string   Exchange,
    DateTime FirstBucket,
    DateTime LastBucket,
    long     Bars,
    long     BarsExpected,
    decimal  CompletenessPct);

/// <summary>
/// Everything the simulation reads, loaded once and held in memory.
///
/// Loaded eagerly rather than streamed because the whole point of the backtester is
/// to sweep many parameter sets over the same history, and re-querying per sweep
/// would make the sweep cost dominate. A month of SOL at 15-minute buckets across
/// three venues is a few thousand rows; a month of 1-minute candles is ~43k. Both
/// fit comfortably.
/// </summary>
public sealed class MarketHistory
{
    public required string Symbol { get; init; }

    /// <summary>Venue → that venue's buckets, ascending by time, no gaps filled.</summary>
    public required IReadOnlyDictionary<string, List<FlowBar>> BarsByVenue { get; init; }

    /// <summary>Every bucket timestamp any venue reported, ascending and distinct.</summary>
    public required List<DateTime> Timeline { get; init; }

    /// <summary>1-minute candles ascending, and an index from minute to position.</summary>
    public required List<Candle> Candles { get; init; }

    public required IReadOnlyList<CoverageRow> Coverage { get; init; }

    private Dictionary<DateTime, int>? _candleIndex;

    /// <summary>
    /// Position of the candle opening at exactly <paramref name="minute"/>, or the
    /// first one after it. Returns -1 when the series ends before that point.
    ///
    /// Built lazily and once. A linear scan per lookup turned a full sweep into
    /// minutes of quadratic work.
    /// </summary>
    public int CandleIndexAtOrAfter(DateTime minute)
    {
        _candleIndex ??= BuildCandleIndex();

        if (_candleIndex.TryGetValue(minute, out var exact)) return exact;

        // Missing minute — the feed dropped it. Binary search for the next one
        // rather than treating the gap as a reason to skip the trade: a missing
        // candle is a data artefact, and a backtest that silently declines every
        // trade near one reports a coverage figure that the live bot would not
        // reproduce.
        var lo = 0;
        var hi = Candles.Count - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Candles[mid].OpenTime >= minute) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        return found;
    }

    private Dictionary<DateTime, int> BuildCandleIndex()
    {
        var index = new Dictionary<DateTime, int>(Candles.Count);
        for (var i = 0; i < Candles.Count; i++) index[Candles[i].OpenTime] = i;
        return index;
    }

    /// <summary>
    /// The buckets visible to a decision made at <paramref name="asOf"/>, per venue.
    ///
    /// Inclusive of the bucket starting at <paramref name="asOf"/> and nothing after
    /// it. This method is the only place the backtester can leak the future, so the
    /// bound lives here alone rather than being re-imposed at each call site.
    /// </summary>
    public Dictionary<string, IReadOnlyList<FlowBar>> VisibleAt(DateTime asOf, int maxBars)
    {
        var result = new Dictionary<string, IReadOnlyList<FlowBar>>(BarsByVenue.Count);

        foreach (var (venue, bars) in BarsByVenue)
        {
            // Bars are ascending, so the visible prefix ends at the first bucket
            // past asOf. Binary search rather than a filter: this runs once per
            // venue per bucket per sweep configuration.
            var lo = 0;
            var hi = bars.Count - 1;
            var last = -1;

            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (bars[mid].BucketStart <= asOf) { last = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            if (last < 0) continue;

            var count = last + 1;
            var skip  = Math.Max(0, count - maxBars);
            result[venue] = bars.GetRange(skip, count - skip);
        }

        return result;
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    public static async Task<MarketHistory> LoadAsync(
        string connectionString, string symbol, DateTime? from, DateTime? to, CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        var coverage = await LoadCoverageAsync(dataSource, symbol, ct);
        var barsByVenue = await LoadFlowBarsAsync(dataSource, symbol, from, to, ct);
        var candles = await LoadCandlesAsync(dataSource, symbol, from, to, ct);

        var timeline = barsByVenue.Values
            .SelectMany(b => b.Select(x => x.BucketStart))
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        return new MarketHistory
        {
            Symbol      = symbol,
            BarsByVenue = barsByVenue,
            Timeline    = timeline,
            Candles     = candles,
            Coverage    = coverage,
        };
    }

    private static async Task<List<CoverageRow>> LoadCoverageAsync(
        NpgsqlDataSource ds, string symbol, CancellationToken ct)
    {
        const string sql = """
            SELECT exchange, first_bucket, last_bucket, bars, bars_expected, completeness_pct
            FROM v_flow_bar_coverage
            WHERE symbol = @symbol
            ORDER BY exchange
            """;

        var rows = new List<CoverageRow>();

        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("symbol", symbol);
        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
            rows.Add(new CoverageRow(
                r.GetString(0),
                r.GetDateTime(1),
                r.GetDateTime(2),
                r.GetInt64(3),
                r.GetInt64(4),
                r.IsDBNull(5) ? 0m : r.GetDecimal(5)));

        return rows;
    }

    private static async Task<Dictionary<string, List<FlowBar>>> LoadFlowBarsAsync(
        NpgsqlDataSource ds, string symbol, DateTime? from, DateTime? to, CancellationToken ct)
    {
        const string sql = """
            SELECT exchange, bucket_start, buy_volume_usd, sell_volume_usd,
                   buy_count, sell_count, max_buy_usd, max_sell_usd, vwap
            FROM flow_bars_15m
            WHERE symbol = @symbol
              AND (@from::timestamptz IS NULL OR bucket_start >= @from)
              AND (@to::timestamptz   IS NULL OR bucket_start <  @to)
            ORDER BY exchange, bucket_start
            """;

        var result = new Dictionary<string, List<FlowBar>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("symbol", symbol);
        AddNullableTimestamp(cmd, "from", from);
        AddNullableTimestamp(cmd, "to",   to);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            var exchange = r.GetString(0);

            if (!result.TryGetValue(exchange, out var list))
                result[exchange] = list = new List<FlowBar>();

            list.Add(new FlowBar(
                Exchange:       exchange,
                BucketStart:    r.GetDateTime(1),
                BuyVolumeUsd:   r.GetDecimal(2),
                SellVolumeUsd:  r.GetDecimal(3),
                BuyCount:       r.GetInt32(4),
                SellCount:      r.GetInt32(5),
                MaxBuyUsd:      r.GetDecimal(6),
                MaxSellUsd:     r.GetDecimal(7),
                Vwap:           r.GetDecimal(8)));
        }

        return result;
    }

    private static async Task<List<Candle>> LoadCandlesAsync(
        NpgsqlDataSource ds, string symbol, DateTime? from, DateTime? to, CancellationToken ct)
    {
        // Widened past the flow-bar range on both sides: entries are simulated at the
        // bucket *after* a signal and can be held for hours past the last bucket, so
        // clipping candles to the signal range would truncate the final trades and
        // score them as timeouts at whatever price the series happened to end on.
        const string sql = """
            SELECT open_time, open_price, high_price, low_price, close_price
            FROM klines_1m
            WHERE symbol = @symbol
              AND (@from::timestamptz IS NULL OR open_time >= @from - INTERVAL '2 days')
              AND (@to::timestamptz   IS NULL OR open_time <  @to   + INTERVAL '2 days')
            ORDER BY open_time
            """;

        var candles = new List<Candle>();

        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("symbol", symbol);
        AddNullableTimestamp(cmd, "from", from);
        AddNullableTimestamp(cmd, "to",   to);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
            candles.Add(new Candle(
                r.GetDateTime(0), r.GetDecimal(1), r.GetDecimal(2),
                r.GetDecimal(3), r.GetDecimal(4)));

        return candles;
    }

    /// <summary>
    /// Bind an optional timestamp, stating the type explicitly.
    ///
    /// AddWithValue(DBNull.Value) leaves Npgsql with no type to infer from and the
    /// command fails at execution rather than at binding — a "from/to are optional"
    /// path that only breaks when the caller actually omits them, which is the
    /// default. Naming NpgsqlDbType removes the inference entirely.
    /// </summary>
    private static void AddNullableTimestamp(NpgsqlCommand cmd, string name, DateTime? value)
    {
        var p = new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = value.HasValue
                // A timestamptz parameter must carry an offset-aware value. The CLI
                // parses with AssumeUniversal, so anything arriving here is already
                // UTC; this makes that explicit rather than relying on Kind surviving.
                ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
                : DBNull.Value,
        };

        cmd.Parameters.Add(p);
    }
}
