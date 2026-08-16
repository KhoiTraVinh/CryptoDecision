using Microsoft.Extensions.Logging;
using Npgsql;

namespace CryptoDecision.ProcessorService.Persistence;

/// <summary>
/// Manages all DDL for the ProcessorService schema.
///
/// Tables:
///   trades              — RANGE-partitioned by trade_time (daily partitions)
///   klines_1m           — 1-minute OHLCV candles (not partitioned; low volume)
///   daily_feature_table — aggregated per-symbol, per-day features
///   prediction_table    — populated by a downstream ML service (read by ApiService)
///
/// Partition strategy: daily (not monthly) for trades because:
///   • Easier to drop old days (just DROP TABLE partition)
///   • Query planner prunes by date for dashboard queries (last 24h)
///   • Binance generates ~500k trades/day per symbol → daily is right-sized
/// </summary>
public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Initializing ProcessorService schema...");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            await CreateExtensionsAsync(conn, ct);
            await CreateTradesTableAsync(conn, ct);
            await CreateKlinesTableAsync(conn, ct);
            await CreateDailyFeatureTableAsync(conn, ct);
            await CreatePredictionTableAsync(conn, ct);
            await EnsureDailyPartitionsAsync(conn, ct);
            await EnsureDedupIndexAsync(conn, ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Schema initialization complete");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task CreateExtensionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, "CREATE EXTENSION IF NOT EXISTS pgcrypto;", ct);
        await Exec(conn, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;", ct);
    }

    private static async Task CreateTradesTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, """
            CREATE TABLE IF NOT EXISTS trades (
                id              BIGSERIAL,
                symbol          VARCHAR(20)      NOT NULL,
                trade_id        BIGINT           NOT NULL,
                price           NUMERIC(20, 8)   NOT NULL,
                quantity        NUMERIC(20, 8)   NOT NULL,
                quote_qty       NUMERIC(20, 8)   NOT NULL,
                is_buyer_maker  BOOLEAN          NOT NULL,
                is_whale        BOOLEAN          NOT NULL GENERATED ALWAYS AS (quote_qty > 100000) STORED,
                trade_time      TIMESTAMPTZ      NOT NULL,
                ingested_at     TIMESTAMPTZ      NOT NULL DEFAULT now(),
                PRIMARY KEY (id, trade_time)     -- partition key must be in PK
            ) PARTITION BY RANGE (trade_time);

            -- Global indexes propagate to future partitions
            CREATE INDEX IF NOT EXISTS ix_trades_symbol
                ON trades (symbol, trade_time DESC);
            CREATE INDEX IF NOT EXISTS ix_trades_whale
                ON trades (is_whale, trade_time DESC)
                WHERE is_whale = true;
            """, ct);
    }

    private static async Task CreateKlinesTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, """
            CREATE TABLE IF NOT EXISTS klines_1m (
                id              BIGSERIAL PRIMARY KEY,
                symbol          VARCHAR(20)    NOT NULL,
                open_time       TIMESTAMPTZ    NOT NULL,
                close_time      TIMESTAMPTZ    NOT NULL,
                open_price      NUMERIC(20, 8) NOT NULL,
                high_price      NUMERIC(20, 8) NOT NULL,
                low_price       NUMERIC(20, 8) NOT NULL,
                close_price     NUMERIC(20, 8) NOT NULL,
                volume          NUMERIC(30, 8) NOT NULL,
                quote_volume    NUMERIC(30, 8) NOT NULL,
                num_trades      INT            NOT NULL,
                ingested_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
                UNIQUE (symbol, open_time)
            );
            CREATE INDEX IF NOT EXISTS ix_klines_symbol_time
                ON klines_1m (symbol, open_time DESC);
            """, ct);
    }

    private static async Task CreateDailyFeatureTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, """
            CREATE TABLE IF NOT EXISTS daily_feature_table (
                id              BIGSERIAL      PRIMARY KEY,
                symbol          VARCHAR(20)    NOT NULL,
                date            DATE           NOT NULL,
                return_24h      NUMERIC(10, 6) NOT NULL,   -- % return
                volatility      NUMERIC(10, 6) NOT NULL,   -- (H-L)/O * 100
                volume_change   NUMERIC(10, 6) NOT NULL,   -- % vs prior day
                whale_count     INT            NOT NULL,   -- trades >100k USDT
                total_volume    NUMERIC(30, 8) NOT NULL,
                vwap            NUMERIC(20, 8) NOT NULL,
                computed_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
                UNIQUE (symbol, date)
            );
            CREATE INDEX IF NOT EXISTS ix_daily_feature_symbol_date
                ON daily_feature_table (symbol, date DESC);
            """, ct);
    }

    private static async Task CreatePredictionTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, """
            CREATE TABLE IF NOT EXISTS prediction_table (
                id              BIGSERIAL      PRIMARY KEY,
                symbol          VARCHAR(20)    NOT NULL,
                date            DATE           NOT NULL,
                direction       VARCHAR(10)    NOT NULL,   -- 'UP' | 'DOWN' | 'NEUTRAL'
                confidence      NUMERIC(5, 4)  NOT NULL,   -- 0.0000 – 1.0000
                model_version   VARCHAR(50)    NOT NULL,
                rationale       TEXT,
                signals         JSONB,                      -- per-model ensemble breakdown
                created_at      TIMESTAMPTZ    NOT NULL DEFAULT now(),
                UNIQUE (symbol, date, model_version)
            );
            ALTER TABLE prediction_table ADD COLUMN IF NOT EXISTS rationale TEXT;
            ALTER TABLE prediction_table ADD COLUMN IF NOT EXISTS signals   JSONB;
            CREATE INDEX IF NOT EXISTS ix_prediction_symbol_date
                ON prediction_table (symbol, date DESC);
            """, ct);
    }

    // ── Deduplication index ───────────────────────────────────────────────────

    private static async Task EnsureDedupIndexAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // trade_time must be included because the table is RANGE-partitioned by it.
        // This index enforces idempotency across Kafka at-least-once redeliveries.
        await Exec(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS uq_trades_exchange_trade_id
                ON trades (exchange, trade_id, trade_time);
            """, ct);
    }

    // ── Partition management ──────────────────────────────────────────────────

    public async Task EnsureDailyPartitionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Maintain: yesterday, today, tomorrow (rolling 3-day window)
        var days = new[] { -1, 0, 1, 2 };
        foreach (var offset in days)
            await EnsureDailyPartitionAsync(conn, DateTime.UtcNow.Date.AddDays(offset), ct);
    }

    public async Task EnsureDailyPartitionAsync(
        NpgsqlConnection conn, DateTime day, CancellationToken ct)
    {
        var from = day.Date;
        var to   = from.AddDays(1);
        var name = $"trades_{from:yyyy_MM_dd}";

        var sql = $"""
            CREATE TABLE IF NOT EXISTS {name}
                PARTITION OF trades
                FOR VALUES FROM ('{from:O}') TO ('{to:O}');

            CREATE INDEX IF NOT EXISTS ix_{name}_symbol_time
                ON {name} (symbol, trade_time DESC);
            """;

        await Exec(conn, sql, ct);
        logger.LogDebug("Ensured partition {Name}", name);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
