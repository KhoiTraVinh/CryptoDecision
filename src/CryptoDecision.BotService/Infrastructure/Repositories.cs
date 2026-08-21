using CryptoDecision.BotService.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.BotService.Infrastructure;

// ── FeatureRepository (read-only, used by PaperOrderEngine for vol-sizing) ───

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
        if (!await reader.ReadAsync(ct)) return null;

        return new DailyFeature(
            Symbol:       reader.GetString(reader.GetOrdinal("symbol")),
            Date:         DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("date"))),
            Return24h:    reader.GetDecimal(reader.GetOrdinal("return_24h")),
            Volatility:   reader.GetDecimal(reader.GetOrdinal("volatility")),
            VolumeChange: reader.GetDecimal(reader.GetOrdinal("volume_change")),
            WhaleCount:   reader.GetInt32(reader.GetOrdinal("whale_count")),
            TotalVolume:  reader.GetDecimal(reader.GetOrdinal("total_volume")),
            Vwap:         reader.GetDecimal(reader.GetOrdinal("vwap")),
            ComputedAt:   reader.GetDateTime(reader.GetOrdinal("computed_at"))
        );
    }
}

// ── MomentumRepository (multi-timeframe order flow) ──────────────────────────

public sealed class MomentumRepository(NpgsqlDataSource dataSource) : IMomentumRepository
{
    /// <summary>
    /// Buy/sell pressure over the trailing 5m, 15m and 1h windows.
    ///
    /// Windows are <em>cumulative</em>: the 15m row includes the last 5 minutes,
    /// and the 1h row includes both. This used to bucket each trade into exactly
    /// one window with a CASE expression, which made "15m" mean "between 5 and 15
    /// minutes ago" — so the two longer timeframes MomentumStrategy weights most
    /// heavily excluded the most recent data, and a fresh reversal could not reach
    /// them at all. The labels, the strategy's own comments, and
    /// PredictionService's get_timeframe_flows all describe cumulative windows;
    /// this is now the only reading of them in the codebase.
    ///
    /// The consequence is deliberate and worth knowing: the three windows overlap,
    /// so their scores are correlated and the composite reacts more smoothly than
    /// it did with disjoint buckets.
    ///
    /// One materialised scan of the trailing hour, then a bare aggregate per
    /// window. An aggregate with no GROUP BY always yields exactly one row, so an
    /// empty window comes back as zeros rather than a missing row.
    ///
    /// Trades are counted across every exchange, not just the one orders are placed
    /// on. That is intentional: this measures where the market is going, and the
    /// venue with the deepest flow is the better read on that regardless of where
    /// the position is opened. The venue-specific number that does matter — price —
    /// comes from PriceFeedResolver instead.
    /// </summary>
    public async Task<MultiTimeframeMomentum> GetMultiTimeframeAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            WITH recent AS MATERIALIZED (
                SELECT is_buyer_maker, is_whale, quote_qty, trade_time
                FROM trades
                WHERE symbol = @symbol
                  AND trade_time >= NOW() - INTERVAL '1 hour'
            ),
            windowed AS (
                SELECT '5m' AS tf, * FROM recent WHERE trade_time >= NOW() - INTERVAL '5 minutes'
                UNION ALL
                SELECT '15m',       * FROM recent WHERE trade_time >= NOW() - INTERVAL '15 minutes'
                UNION ALL
                SELECT '1h',        * FROM recent
            )
            SELECT
                tf,
                COUNT(*) FILTER (WHERE NOT is_buyer_maker)                    AS buy_count,
                COUNT(*) FILTER (WHERE     is_buyer_maker)                    AS sell_count,
                COALESCE(SUM(quote_qty) FILTER (WHERE NOT is_buyer_maker), 0) AS buy_volume_usd,
                COALESCE(SUM(quote_qty) FILTER (WHERE     is_buyer_maker), 0) AS sell_volume_usd,
                COUNT(*) FILTER (WHERE is_whale AND NOT is_buyer_maker)       AS whale_buy_count,
                COUNT(*) FILTER (WHERE is_whale AND     is_buyer_maker)       AS whale_sell_count
            FROM windowed
            GROUP BY tf
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        await using var r = await cmd.ExecuteReaderAsync(ct);

        var frames = new Dictionary<string, TimeframeMomentum>();
        while (await r.ReadAsync(ct))
        {
            var tf = r.GetString(r.GetOrdinal("tf"));
            frames[tf] = new TimeframeMomentum(
                Timeframe:      tf,
                BuyCount:       (int)r.GetInt64(r.GetOrdinal("buy_count")),
                SellCount:      (int)r.GetInt64(r.GetOrdinal("sell_count")),
                BuyVolumeUsd:   r.GetDecimal(r.GetOrdinal("buy_volume_usd")),
                SellVolumeUsd:  r.GetDecimal(r.GetOrdinal("sell_volume_usd")),
                WhaleBuyCount:  (int)r.GetInt64(r.GetOrdinal("whale_buy_count")),
                WhaleSellCount: (int)r.GetInt64(r.GetOrdinal("whale_sell_count"))
            );
        }

        var empty = new TimeframeMomentum("", 0, 0, 0, 0, 0, 0);
        return new MultiTimeframeMomentum(
            M5:  frames.GetValueOrDefault("5m",  empty),
            M15: frames.GetValueOrDefault("15m", empty),
            M1h: frames.GetValueOrDefault("1h",  empty)
        );
    }
}

// ── PredictionRepository (reads AI prediction from prediction_table) ─────────

public sealed class PredictionRepository(NpgsqlDataSource dataSource) : IPredictionRepository
{
    public async Task<PredictionSnapshot?> GetLatestAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            SELECT symbol, direction, confidence, model_version, rationale, created_at
            FROM prediction_table
            WHERE symbol = @symbol
            ORDER BY created_at DESC
            LIMIT 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new PredictionSnapshot(
            Symbol:       reader.GetString(reader.GetOrdinal("symbol")),
            Direction:    reader.GetString(reader.GetOrdinal("direction")),
            Confidence:   reader.GetDecimal(reader.GetOrdinal("confidence")),
            ModelVersion: reader.GetString(reader.GetOrdinal("model_version")),
            Rationale:    reader.IsDBNull(reader.GetOrdinal("rationale")) ? null : reader.GetString(reader.GetOrdinal("rationale")),
            PredictedAt:  reader.GetDateTime(reader.GetOrdinal("created_at"))
        );
    }
}
