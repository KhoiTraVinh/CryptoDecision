using Npgsql;

namespace CryptoDecision.ProcessorService.Persistence;

/// <summary>
/// Creates the daily partitions of <c>trades</c>. Nothing else.
///
/// WHY THIS NO LONGER DEFINES THE SCHEMA
/// -------------------------------------
/// It used to run 28 DDL statements at every boot: CREATE TABLE for trades,
/// klines_1m, daily_feature_table, bot_trades and strategy_verdicts, a block of
/// ALTER TABLE ... ADD COLUMN IF NOT EXISTS for bot_config and bot_trades, every
/// index, and the extensions. All of it duplicated `sql/`, which is applied by
/// db-migrate with a checksum per file and a row in schema_migrations.
///
/// Two authorities for one schema is one too many, and this one was already losing:
/// on 2026-10-06 it did not know about last_exit_review_note (047),
/// high_volume_max_hold_minutes (046) or suspend_on_high_volume (045). Every
/// statement was IF NOT EXISTS, so the drift was silent and harmless right up until
/// it would not have been — a fresh database brought up by this path would have come
/// back missing three columns, and the service that created it would have reported
/// success.
///
/// WHAT STAYS, AND WHY IT CANNOT MOVE INTO sql/
/// --------------------------------------------
/// Partitions are not a one-time schema fact. `trades` is RANGE-partitioned by
/// trade_time and needs a partition for every day, forever; a migration runs once at
/// deploy and cannot create tomorrow's. So partition creation belongs to the running
/// service, which is here. Dropping OLD partitions is the other half and does live in
/// sql/ as drop_old_trade_partitions.
///
/// Because the parent table is now somebody else's job, `processor` gained
/// `depends_on: db-migrate` in docker-compose. Without it, a fresh database could
/// start this service before the table it partitions exists.
///
/// Daily rather than monthly partitions because dropping a day is a DROP TABLE, the
/// planner prunes by date on the recent-window queries, and Binance alone produces
/// ~500k trades/day per symbol.
/// </summary>
public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            await EnsureDailyPartitionsAsync(conn, ct);
            await tx.CommitAsync(ct);
            logger.LogInformation(
                "Partitions ensured. Schema itself belongs to sql/ and db-migrate.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ── Partition management ──────────────────────────────────────────────────

    public async Task EnsureDailyPartitionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Yesterday through two days out. Yesterday matters because a late trade can
        // still arrive for it; two days out is the lead that keeps a missed cycle from
        // landing on a day with no partition.
        foreach (var offset in new[] { -1, 0, 1, 2 })
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
