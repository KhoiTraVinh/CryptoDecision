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

// ── MomentumRepository (5-min buy/sell ratio + multi-timeframe) ───────────────

public sealed class MomentumRepository(NpgsqlDataSource dataSource) : IMomentumRepository
{
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

    public async Task<MultiTimeframeMomentum> GetMultiTimeframeAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            WITH classified AS (
                SELECT
                    CASE
                        WHEN trade_time >= NOW() - INTERVAL '5 minutes'  THEN '5m'
                        WHEN trade_time >= NOW() - INTERVAL '15 minutes' THEN '15m'
                        ELSE '1h'
                    END AS tf,
                    is_buyer_maker, is_whale, quote_qty
                FROM trades
                WHERE symbol = @symbol
                  AND trade_time >= NOW() - INTERVAL '1 hour'
            )
            SELECT
                tf,
                COUNT(*) FILTER (WHERE NOT is_buyer_maker)                    AS buy_count,
                COUNT(*) FILTER (WHERE     is_buyer_maker)                    AS sell_count,
                COALESCE(SUM(quote_qty) FILTER (WHERE NOT is_buyer_maker), 0) AS buy_volume_usd,
                COALESCE(SUM(quote_qty) FILTER (WHERE     is_buyer_maker), 0) AS sell_volume_usd,
                COUNT(*) FILTER (WHERE is_whale AND NOT is_buyer_maker)       AS whale_buy_count,
                COUNT(*) FILTER (WHERE is_whale AND     is_buyer_maker)       AS whale_sell_count
            FROM classified
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

// ── VolumeRepository (1h, 24h windows) ───────────────────────────────────────

public sealed class VolumeRepository(NpgsqlDataSource dataSource) : IVolumeRepository
{
    public async Task<IReadOnlyList<VolumeWindowData>> GetWindowsAsync(
        string symbol, string exchange = "BINANCE", CancellationToken ct = default)
    {
        const string sql = """
            WITH classified AS (
                SELECT
                    CASE
                        WHEN trade_time >= NOW() - INTERVAL '1 hour'  THEN '1h'
                        WHEN trade_time >= NOW() - INTERVAL '1 day'   THEN '24h'
                        WHEN trade_time >= NOW() - INTERVAL '7 days'  THEN '7d'
                        WHEN trade_time >= NOW() - INTERVAL '30 days' THEN '30d'
                        WHEN trade_time >= NOW() - INTERVAL '3 months' THEN '3m'
                        WHEN trade_time >= NOW() - INTERVAL '6 months' THEN '6m'
                        ELSE '1y'
                    END AS win,
                    is_buyer_maker, is_whale, quote_qty
                FROM trades
                WHERE symbol = @symbol
                  AND (exchange = @exchange OR @exchange = 'ALL')
                  AND trade_time >= NOW() - INTERVAL '1 year'
            )
            SELECT
                win,
                COUNT(*)                                                      AS total_trades,
                COUNT(*) FILTER (WHERE NOT is_buyer_maker)                    AS buy_count,
                COUNT(*) FILTER (WHERE     is_buyer_maker)                    AS sell_count,
                COALESCE(SUM(quote_qty) FILTER (WHERE NOT is_buyer_maker), 0) AS buy_volume_usd,
                COALESCE(SUM(quote_qty) FILTER (WHERE     is_buyer_maker), 0) AS sell_volume_usd,
                COUNT(*) FILTER (WHERE is_whale AND NOT is_buyer_maker)        AS whale_buy_count,
                COUNT(*) FILTER (WHERE is_whale AND     is_buyer_maker)        AS whale_sell_count,
                COALESCE(SUM(quote_qty) FILTER (WHERE is_whale), 0)           AS whale_volume_usd
            FROM classified
            GROUP BY win
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
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
