using CryptoDecision.ApiService.Domain.Entities;
using CryptoDecision.ApiService.Domain.Interfaces;
using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.ApiService.Infrastructure.Persistence;

public sealed class FeatureRepository(NpgsqlDataSource dataSource) : IFeatureRepository
{
    public async Task<DailyFeature?> GetTodayAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol, date, return_24h, volatility, volume_change,
                   whale_count, total_volume, vwap, computed_at
            FROM daily_feature_table
            WHERE symbol = @symbol AND date = CURRENT_DATE
            LIMIT 1
            """;

        await using var conn   = await dataSource.OpenConnectionAsync(ct);
        await using var cmd    = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapFeature(reader) : null;
    }

    public async Task<IReadOnlyList<DailyFeature>> GetHistoryAsync(
        string symbol, int days = 30, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol, date, return_24h, volatility, volume_change,
                   whale_count, total_volume, vwap, computed_at
            FROM daily_feature_table
            WHERE symbol = @symbol
              AND date >= CURRENT_DATE - @days::int
            ORDER BY date DESC
            """;

        await using var conn   = await dataSource.OpenConnectionAsync(ct);
        await using var cmd    = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("days",   NpgsqlDbType.Integer, days);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<DailyFeature>();
        while (await reader.ReadAsync(ct))
            result.Add(MapFeature(reader));
        return result;
    }

    private static DailyFeature MapFeature(NpgsqlDataReader r)
    {
        var ordSymbol     = r.GetOrdinal("symbol");
        var ordDate       = r.GetOrdinal("date");
        var ordReturn     = r.GetOrdinal("return_24h");
        var ordVolatility = r.GetOrdinal("volatility");
        var ordVolChange  = r.GetOrdinal("volume_change");
        var ordWhaleCount = r.GetOrdinal("whale_count");
        var ordTotalVol   = r.GetOrdinal("total_volume");
        var ordVwap       = r.GetOrdinal("vwap");
        var ordComputedAt = r.GetOrdinal("computed_at");

        return new DailyFeature(
            Symbol:       r.GetString(ordSymbol),
            Date:         DateOnly.FromDateTime(r.GetDateTime(ordDate)),
            Return24h:    r.GetDecimal(ordReturn),
            Volatility:   r.GetDecimal(ordVolatility),
            VolumeChange: r.GetDecimal(ordVolChange),
            WhaleCount:   r.GetInt32(ordWhaleCount),
            TotalVolume:  r.GetDecimal(ordTotalVol),
            Vwap:         r.GetDecimal(ordVwap),
            ComputedAt:   r.GetDateTime(ordComputedAt)
        );
    }
}

public sealed class MomentumRepository(NpgsqlDataSource dataSource) : IMomentumRepository
{
    // COUNT(*) aggregates always return exactly 1 row, even when no trades match.
    public async Task<MomentumData> GetAsync(string symbol, string exchange = "BINANCE", CancellationToken ct = default)
    {
        const string sql = """
            SELECT
              COUNT(*)                                                  AS total_trades,
              COUNT(*) FILTER (WHERE NOT is_buyer_maker)                AS buy_count,
              COUNT(*) FILTER (WHERE     is_buyer_maker)                AS sell_count,
              COUNT(*) FILTER (WHERE is_whale AND NOT is_buyer_maker)   AS whale_buy_count,
              COUNT(*) FILTER (WHERE is_whale AND     is_buyer_maker)   AS whale_sell_count,
              COALESCE(SUM(quote_qty), 0)                               AS volume_usd
            FROM trades
            WHERE symbol = @symbol
              AND (exchange = @exchange OR @exchange = 'ALL')
              AND trade_time >= NOW() - INTERVAL '5 minutes'
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("exchange", exchange);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);

        return new MomentumData(
            Symbol:         symbol,
            TotalTrades:    (int)r.GetInt64(r.GetOrdinal("total_trades")),
            BuyCount:       (int)r.GetInt64(r.GetOrdinal("buy_count")),
            SellCount:      (int)r.GetInt64(r.GetOrdinal("sell_count")),
            WhaleBuyCount:  (int)r.GetInt64(r.GetOrdinal("whale_buy_count")),
            WhaleSellCount: (int)r.GetInt64(r.GetOrdinal("whale_sell_count")),
            VolumeUsd:      r.GetDecimal(r.GetOrdinal("volume_usd"))
        );
    }
}

public sealed class KlineRepository(NpgsqlDataSource dataSource) : IKlineRepository
{
    public async Task<IReadOnlyList<KlineData>> GetRecentAsync(
        string symbol, int limit, string exchange = "BINANCE", CancellationToken ct = default)
    {
        const string sql = """
            SELECT open_time, open_price, high_price, low_price, close_price, volume, num_trades
            FROM klines_1m
            WHERE symbol = @symbol
            ORDER BY open_time DESC
            LIMIT @limit
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("limit",  NpgsqlDbType.Integer, limit);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        var ordOpen  = r.GetOrdinal("open_time");
        var ordO     = r.GetOrdinal("open_price");
        var ordH     = r.GetOrdinal("high_price");
        var ordL     = r.GetOrdinal("low_price");
        var ordC     = r.GetOrdinal("close_price");
        var ordV     = r.GetOrdinal("volume");
        var ordN     = r.GetOrdinal("num_trades");

        var result = new List<KlineData>();
        while (await r.ReadAsync(ct))
            result.Add(new KlineData(
                OpenTime:  r.GetDateTime(ordOpen),
                Open:      r.GetDecimal(ordO),
                High:      r.GetDecimal(ordH),
                Low:       r.GetDecimal(ordL),
                Close:     r.GetDecimal(ordC),
                Volume:    r.GetDecimal(ordV),
                NumTrades: r.GetInt32(ordN)
            ));

        // Reverse to ascending order so charts render left → right
        result.Reverse();
        return result;
    }
}

public sealed class VolumeRepository(NpgsqlDataSource dataSource) : IVolumeRepository
{
    public async Task<IReadOnlyList<VolumeWindowData>> GetWindowsAsync(
        string symbol, string exchange = "BINANCE", CancellationToken ct = default)
    {
        // Cumulative windows: "24h" is the last 24 hours and contains the last hour,
        // "7d" contains both. That is what the labels say, and it was not what the
        // query did — a CASE expression put each trade in exactly one bucket, so
        // "24h" meant "between 1 and 24 hours ago". The two readings diverge most
        // when they matter most: right after a symbol change, when all the data is
        // inside the hour and every wider column reads zero.
        //
        // One aggregate per window with a literal interval, rather than one pass
        // with a CASE. Literals let the planner prune partitions at plan time, so
        // the 1h window touches today's partition instead of a year of them. A bare
        // aggregate with no GROUP BY always returns exactly one row, so an empty
        // window comes back as zeros rather than a missing row — the same shape
        // PredictionService uses for its flow windows.
        //
        // Only the windows the dashboard renders. The previous query also computed
        // 30d, 3m, 6m and 1y — scanning a full year of trades every 30 seconds for
        // numbers nothing displayed.
        const string aggregates = """
                COUNT(*)                                                      AS total_trades,
                COUNT(*) FILTER (WHERE NOT is_buyer_maker)                    AS buy_count,
                COUNT(*) FILTER (WHERE     is_buyer_maker)                    AS sell_count,
                COALESCE(SUM(quote_qty) FILTER (WHERE NOT is_buyer_maker), 0) AS buy_volume_usd,
                COALESCE(SUM(quote_qty) FILTER (WHERE     is_buyer_maker), 0) AS sell_volume_usd,
                COUNT(*) FILTER (WHERE is_whale AND NOT is_buyer_maker)       AS whale_buy_count,
                COUNT(*) FILTER (WHERE is_whale AND     is_buyer_maker)       AS whale_sell_count,
                COALESCE(SUM(quote_qty) FILTER (WHERE is_whale), 0)           AS whale_volume_usd
            """;

        const string filter = """
                WHERE symbol = @symbol
                  AND (exchange = @exchange OR @exchange = 'ALL')
            """;

        var sql = $"""
            SELECT '1h' AS win,
            {aggregates}
            FROM trades
            {filter}
              AND trade_time >= NOW() - INTERVAL '1 hour'
            UNION ALL
            SELECT '24h',
            {aggregates}
            FROM trades
            {filter}
              AND trade_time >= NOW() - INTERVAL '1 day'
            UNION ALL
            SELECT '7d',
            {aggregates}
            FROM trades
            {filter}
              AND trade_time >= NOW() - INTERVAL '7 days'
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 20;
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("exchange", exchange);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        var result = new List<VolumeWindowData>();
        while (await r.ReadAsync(ct))
        {
            result.Add(new VolumeWindowData(
                Window:         r.GetString(r.GetOrdinal("win")),
                TotalTrades:    (int)r.GetInt64(r.GetOrdinal("total_trades")),
                BuyCount:       (int)r.GetInt64(r.GetOrdinal("buy_count")),
                SellCount:      (int)r.GetInt64(r.GetOrdinal("sell_count")),
                BuyVolumeUsd:   r.GetDecimal(r.GetOrdinal("buy_volume_usd")),
                SellVolumeUsd:  r.GetDecimal(r.GetOrdinal("sell_volume_usd")),
                WhaleBuyCount:  (int)r.GetInt64(r.GetOrdinal("whale_buy_count")),
                WhaleSellCount: (int)r.GetInt64(r.GetOrdinal("whale_sell_count")),
                WhaleVolumeUsd: r.GetDecimal(r.GetOrdinal("whale_volume_usd"))
            ));
        }
        return result;
    }
}

public sealed class TradeQueryRepository(NpgsqlDataSource dataSource) : ITradeQueryRepository
{
    public async Task<IReadOnlyList<WhaleTradeData>> GetRecentWhalesAsync(
        DateTime since, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol, exchange, price, quote_qty, is_buyer_maker, trade_time
            FROM trades
            WHERE is_whale = true
              AND trade_time > @since
              AND trade_time >= CURRENT_DATE - INTERVAL '1 day'
            ORDER BY trade_time ASC
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("since", since);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<WhaleTradeData>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WhaleTradeData(
                Symbol:       reader.GetString(0),
                Exchange:     reader.GetString(1),
                Price:        reader.GetDecimal(2),
                QuoteQty:     reader.GetDecimal(3),
                IsBuyerMaker: reader.GetBoolean(4),
                TradeTime:    reader.GetDateTime(5)
            ));
        }
        return result;
    }

    public async Task<IReadOnlyList<WhaleTradeData>> GetLatestWhalesAsync(
        string symbol, string exchange, int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol, exchange, price, quote_qty, is_buyer_maker, trade_time
            FROM trades
            WHERE symbol = @symbol
              AND (exchange = @exchange OR @exchange = 'ALL')
              AND is_whale = true
            ORDER BY trade_time DESC
            LIMIT @limit
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("exchange", exchange);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<WhaleTradeData>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WhaleTradeData(
                Symbol:       reader.GetString(0),
                Exchange:     reader.GetString(1),
                Price:        reader.GetDecimal(2),
                QuoteQty:     reader.GetDecimal(3),
                IsBuyerMaker: reader.GetBoolean(4),
                TradeTime:    reader.GetDateTime(5)
            ));
        }
        return result;
    }
}
