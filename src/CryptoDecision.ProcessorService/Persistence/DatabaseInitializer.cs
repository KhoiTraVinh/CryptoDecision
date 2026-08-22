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
            await EnsureBotConfigColumnsAsync(conn, ct);
            await EnsureBotTradesAsync(conn, ct);
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

    /// <summary>
    /// Add bot_config columns that newer bot builds read.
    ///
    /// BotConfigRepository selects these by name, so a database that predates them
    /// fails the whole config read and the bot cannot start at all. Applying them
    /// here means a preserved postgres_data volume self-heals on boot instead of
    /// requiring the operator to remember a manual migration.
    ///
    /// bot_config is created by the sql/ bootstrap scripts, which only run on an
    /// empty volume — hence the to_regclass guard for a fresh database where this
    /// runs before those scripts have created the table.
    /// </summary>
    private static async Task EnsureBotConfigColumnsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, """
            DO $$
            BEGIN
                IF to_regclass('public.bot_config') IS NOT NULL THEN
                    ALTER TABLE bot_config
                        ADD COLUMN IF NOT EXISTS use_ai_agent BOOLEAN NOT NULL DEFAULT FALSE,
                        -- Why the last entry was not placed, and how many the bot has
                        -- refused today. A refused entry is a normal outcome and was
                        -- logged as one, which meant a bot that had refused every
                        -- entry for hours was indistinguishable on screen from a bot
                        -- waiting patiently for a signal. Persisted rather than held
                        -- in memory because the API is a separate process and cannot
                        -- see the worker's state.
                        ADD COLUMN IF NOT EXISTS last_refusal_reason TEXT,
                        ADD COLUMN IF NOT EXISTS last_refusal_at     TIMESTAMPTZ,
                        ADD COLUMN IF NOT EXISTS refusal_count       INTEGER NOT NULL DEFAULT 0,
                        ADD COLUMN IF NOT EXISTS refusal_count_date  DATE,
                        -- What the last real sizing decision produced. The dashboard
                        -- can compute what sizing *would* ask for, but only the bot
                        -- knows what survived the exchange's lot grid.
                        ADD COLUMN IF NOT EXISTS last_sizing_note    TEXT;
                END IF;
            END
            $$;
            """, ct);
    }

    /// <summary>
    /// Create bot_trades and every column newer bot builds read.
    ///
    /// This table was only ever created by sql/008_bot_trades.sql, which — unlike
    /// 001-005 and 009-011 — is not mounted into docker-entrypoint-initdb.d, and
    /// nothing created it at runtime. So a clean postgres_data volume had no
    /// bot_trades at all: the bot's startup recovery reads it, that read is
    /// deliberately fatal, and the container crash-looped with no way out short of
    /// applying migrations by hand. The running database only worked because it had
    /// been migrated manually.
    ///
    /// Everything here is IF NOT EXISTS, so it is equally correct on a fresh volume
    /// and on one that already has the columns from sql/014-016.
    /// </summary>
    private static async Task EnsureBotTradesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await Exec(conn, """
            CREATE TABLE IF NOT EXISTS bot_trades (
                id           BIGSERIAL      PRIMARY KEY,
                symbol       TEXT           NOT NULL,
                side         TEXT           NOT NULL DEFAULT 'BUY',
                entry_price  NUMERIC(18, 8) NOT NULL,
                exit_price   NUMERIC(18, 8),
                quantity     NUMERIC(18, 8) NOT NULL,
                notional_usd NUMERIC(10, 4) NOT NULL,
                pnl_usd      NUMERIC(10, 4),
                pnl_pct      NUMERIC(8,  6),
                status       TEXT           NOT NULL DEFAULT 'OPEN',
                opened_at    TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
                closed_at    TIMESTAMPTZ,
                close_reason TEXT,
                strategy     TEXT           NOT NULL DEFAULT 'UNKNOWN',
                peak_price   NUMERIC(20, 8)
            );
            """, ct);

        await Exec(conn, """
            ALTER TABLE bot_trades
                ADD COLUMN IF NOT EXISTS peak_price     NUMERIC(20, 8),
                ADD COLUMN IF NOT EXISTS mode           TEXT NOT NULL DEFAULT 'PAPER',
                ADD COLUMN IF NOT EXISTS exchange       TEXT NOT NULL DEFAULT 'BINANCE',
                ADD COLUMN IF NOT EXISTS entry_order_id TEXT,
                ADD COLUMN IF NOT EXISTS exit_order_id  TEXT,
                ADD COLUMN IF NOT EXISTS fee_usd        NUMERIC(18, 8),
                ADD COLUMN IF NOT EXISTS exit_algo_id   TEXT,
                ADD COLUMN IF NOT EXISTS leverage       NUMERIC(6, 2),
                ADD COLUMN IF NOT EXISTS margin_mode    TEXT;
            """, ct);

        await Exec(conn, """
            CREATE INDEX IF NOT EXISTS idx_bot_trades_symbol ON bot_trades(symbol);
            """, ct);
        await Exec(conn, """
            CREATE INDEX IF NOT EXISTS idx_bot_trades_status ON bot_trades(status);
            """, ct);
        await Exec(conn, """
            CREATE INDEX IF NOT EXISTS idx_bot_trades_open ON bot_trades(opened_at) WHERE status = 'OPEN';
            """, ct);
        await Exec(conn, """
            CREATE INDEX IF NOT EXISTS idx_bot_trades_exit_algo_id
                ON bot_trades(exit_algo_id) WHERE exit_algo_id IS NOT NULL;
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
