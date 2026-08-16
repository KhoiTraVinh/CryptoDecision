using Microsoft.Extensions.Logging;
using Npgsql;

namespace CryptoDecision.ProcessorService.Persistence;

/// <summary>
/// Computes daily features from raw trades + klines data using PostgreSQL
/// window functions and aggregations. The computation runs inside the DB for
/// maximum performance — no data is pulled into application memory.
///
/// Called by FeatureAggregationWorker on a schedule (e.g. every 5 minutes).
/// </summary>
public sealed class FeatureRepository(
    NpgsqlDataSource dataSource,
    ILogger<FeatureRepository> logger)
{
    /// <summary>
    /// Computes and upserts the daily_feature_table row for the given symbol and date.
    /// Whale count: trades where quote_qty > 100,000 USDT.
    /// Volatility: (max_high - min_low) / first_open * 100 over the day.
    /// Return 24h: (last_close - first_open) / first_open * 100.
    /// VolumeChange: vs the prior day's total volume.
    /// VWAP: SUM(price * quantity) / SUM(quantity).
    /// </summary>
    public async Task UpsertDailyFeatureAsync(
        string symbol, DateOnly date, CancellationToken ct = default)
    {
        const string sql = """
            WITH day_trades AS (
                SELECT *
                FROM trades
                WHERE symbol = @symbol
                  AND trade_time >= @day_start
                  AND trade_time <  @day_end
            ),
            prev_day_trades AS (
                SELECT SUM(quantity) AS prev_volume
                FROM trades
                WHERE symbol = @symbol
                  AND trade_time >= @prev_day_start
                  AND trade_time <  @day_start
            ),
            day_klines AS (
                SELECT
                    FIRST_VALUE(open_price)  OVER (ORDER BY open_time ASC)  AS day_open,
                    LAST_VALUE(close_price)  OVER (ORDER BY open_time ASC
                        ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS day_close,
                    MAX(high_price)  OVER () AS day_high,
                    MIN(low_price)   OVER () AS day_low
                FROM klines_1m
                WHERE symbol = @symbol
                  AND open_time >= @day_start
                  AND open_time <  @day_end
                LIMIT 1
            ),
            agg AS (
                SELECT
                    COUNT(*) FILTER (WHERE is_whale)                   AS whale_count,
                    COALESCE(SUM(quantity), 0)                         AS total_volume,
                    CASE WHEN SUM(quantity) > 0
                         THEN SUM(price * quantity) / SUM(quantity)
                         ELSE 0 END                                     AS vwap
                FROM day_trades
            ),
            kline_agg AS (
                SELECT
                    COALESCE(MAX(day_open),  0) AS day_open,
                    COALESCE(MAX(day_close), 0) AS day_close,
                    COALESCE(MAX(day_high),  0) AS day_high,
                    COALESCE(MAX(day_low),   0) AS day_low
                FROM day_klines
            )
            INSERT INTO daily_feature_table
                (symbol, date, return_24h, volatility, volume_change, whale_count,
                 total_volume, vwap, computed_at)
            SELECT
                @symbol,
                @date,
                CASE WHEN k.day_open > 0
                     THEN ROUND(((k.day_close - k.day_open) / k.day_open * 100)::numeric, 6)
                     ELSE 0 END,
                CASE WHEN k.day_open > 0
                     THEN ROUND(((k.day_high - k.day_low) / k.day_open * 100)::numeric, 6)
                     ELSE 0 END,
                CASE WHEN COALESCE(p.prev_volume, 0) > 0
                     THEN ROUND(((a.total_volume - p.prev_volume) / p.prev_volume * 100)::numeric, 6)
                     ELSE 0 END,
                a.whale_count,
                a.total_volume,
                a.vwap,
                now()
            FROM agg a, kline_agg k
            CROSS JOIN (SELECT COALESCE(prev_volume, 0) AS prev_volume FROM prev_day_trades) p
            ON CONFLICT (symbol, date) DO UPDATE SET
                return_24h    = EXCLUDED.return_24h,
                volatility    = EXCLUDED.volatility,
                volume_change = EXCLUDED.volume_change,
                whale_count   = EXCLUDED.whale_count,
                total_volume  = EXCLUDED.total_volume,
                vwap          = EXCLUDED.vwap,
                computed_at   = EXCLUDED.computed_at
            """;

        var dayStart    = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dayEnd      = dayStart.AddDays(1);
        var prevDayStart = dayStart.AddDays(-1);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",       symbol);
        cmd.Parameters.AddWithValue("date",         date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("day_start",    dayStart);
        cmd.Parameters.AddWithValue("day_end",      dayEnd);
        cmd.Parameters.AddWithValue("prev_day_start", prevDayStart);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation(
            "daily_feature_table upserted for {Symbol} {Date} — {Rows} row(s)",
            symbol, date, affected);
    }
}
