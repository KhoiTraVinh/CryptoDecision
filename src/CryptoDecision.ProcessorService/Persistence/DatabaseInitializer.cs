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
///
/// A prediction_table was created here too, "populated by a downstream ML service
/// (read by ApiService)". Both of those were deleted; the DDL was not, so every
/// boot re-created an empty table with two ALTERs and an index that no line of code
/// has read or written since. The table is left alone on existing databases rather
/// than dropped — an unused table costs nothing, and a DROP in a boot path is not
/// a thing to run against a live account.
///
/// Partition strategy: daily (not monthly) for trades because:
///   • Easier to drop old days (just DROP TABLE partition)
///   • Query planner prunes by date for recent-window queries (last 24h)
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

                -- Created here, not only by sql/004_add_exchange.sql.
                --
                -- This column was added by that migration while EnsureDedupIndexAsync
                -- below indexes on it — so the base schema depended on a migration, and
                -- the migrations in turn depend on tables only this class creates
                -- (006_bot_trailing_stop.sql alters bot_trades). That is a cycle, and it
                -- meant a fresh database could not be built by either order: SQL-first
                -- failed at 006, C#-first failed here on "column exchange does not
                -- exist". The only working database was one patched incrementally over
                -- months on a single machine.
                --
                -- Declaring it here breaks the cycle and makes this class the sole owner
                -- of the base schema. sql/004 stays harmless — it is ADD COLUMN IF NOT
                -- EXISTS — and is kept so an existing database's migration ledger still
                -- accounts for it.
                exchange        VARCHAR(16)      NOT NULL DEFAULT 'BINANCE',
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
                        -- use_ai_agent was added here. Nothing reads it any more —
                        -- the agent it switched on was deleted — so a fresh database
                        -- no longer grows the column. Existing ones keep theirs.
                        --
                        -- Why the last entry was not placed, and how many the bot has
                        -- refused today. A refused entry is a normal outcome and was
                        -- logged as one, which meant a bot that had refused every
                        -- entry for hours was indistinguishable from one waiting
                        -- patiently for a signal. Persisted rather than held in
                        -- memory so it survives a restart and can be read with SQL.
                        ADD COLUMN IF NOT EXISTS last_refusal_reason TEXT,
                        ADD COLUMN IF NOT EXISTS last_refusal_at     TIMESTAMPTZ,
                        ADD COLUMN IF NOT EXISTS refusal_count       INTEGER NOT NULL DEFAULT 0,
                        ADD COLUMN IF NOT EXISTS refusal_count_date  DATE,
                        -- What the last real sizing decision produced. The dashboard
                        -- can compute what sizing *would* ask for, but only the bot
                        -- knows what survived the exchange's lot grid.
                        ADD COLUMN IF NOT EXISTS last_sizing_note    TEXT,
                        -- Concurrent positions per side. 0 keeps the old behaviour;
                        -- 1 with max_open_trades_per_strategy = 2 means one LONG and
                        -- one SHORT may be held but never two of either. Mirrored
                        -- from sql/030 so a preserved volume self-heals on boot
                        -- rather than failing the whole config read on a missing
                        -- column.
                        ADD COLUMN IF NOT EXISTS max_open_per_side   INTEGER NOT NULL DEFAULT 0,
                        -- Was hardcoded at 5. The right value depends on the win
                        -- rate the geometry implies, and that is now a setting.
                        ADD COLUMN IF NOT EXISTS max_consecutive_losses INTEGER NOT NULL DEFAULT 5,
                        -- Concurrent positions from the FlowRatio high-volume waiver.
                        -- Mirrored from sql/032 for the reason in this method's
                        -- summary, and it is not a formality here: BotConfigRepository
                        -- wraps this column in COALESCE, which defends against a NULL
                        -- value and not at all against a missing column. Without this
                        -- line, a preserved volume that has not had sql/032 applied
                        -- fails the entire config read, and the bot polls every five
                        -- seconds forever without ever starting.
                        ADD COLUMN IF NOT EXISTS max_open_high_volume INTEGER NOT NULL DEFAULT 1,
                        -- Ceiling across ALL strategies. Mirrored from sql/033 for the
                        -- reason in this method's summary: BotConfigRepository selects it
                        -- by name, so a database without it fails the whole config read
                        -- and the bot never starts.
                        ADD COLUMN IF NOT EXISTS max_open_total INTEGER NOT NULL DEFAULT 5;
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
                ADD COLUMN IF NOT EXISTS margin_mode    TEXT,
                -- Why the trade was entered, kept with the trade.
                --
                -- Every exit reason was recorded and no entry reason was, so a
                -- losing run could be described but not explained: the score and
                -- the component breakdown lived only in the container log, and
                -- that log dies with the container. On 2026-08-22 four losses in
                -- a row could not be compared against the four winners earlier
                -- the same morning, because the winners' logs were gone.
                --
                -- entry_composite is numeric so "were these taken near the
                -- threshold?" is a SQL question rather than a text search.
                ADD COLUMN IF NOT EXISTS entry_composite  NUMERIC(8, 3),
                ADD COLUMN IF NOT EXISTS entry_confidence NUMERIC(6, 4),
                ADD COLUMN IF NOT EXISTS entry_rationale  TEXT,
                -- Which entry rule opened the position: RATIO or HIGH_VOLUME. A
                -- column and not a substring of entry_rationale, because the code
                -- branches on it — the per-rule position limit counts it — and this
                -- repository has already paid once for recovering a branch condition
                -- from prose. Mirrored from sql/032; unlike the bot_config columns
                -- above, a missing one here does not fail the config read, it fails
                -- every trade INSERT instead.
                ADD COLUMN IF NOT EXISTS entry_path       TEXT;
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
