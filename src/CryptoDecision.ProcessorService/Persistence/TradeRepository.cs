using System.Diagnostics;
using CryptoDecision.ProcessorService.Models;
using CryptoDecision.ProcessorService.Telemetry;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.ProcessorService.Persistence;

/// <summary>
/// All write operations for the trades + klines tables.
///
/// BulkInsertTradesAsync uses PostgreSQL COPY binary protocol:
///   • No SQL parsing overhead per row
///   • Single network round-trip for entire batch
///   • PostgreSQL routes rows to the correct daily partition automatically
///   • ~10–20× faster than parameterised INSERT at scale
/// Deduplication: COPY to temp table → INSERT ON CONFLICT (exchange, trade_id, trade_time) DO NOTHING.
/// </summary>
public sealed class TradeRepository(
    NpgsqlDataSource dataSource,
    DatabaseInitializer dbInit,
    ProcessorMetrics metrics,
    ILogger<TradeRepository> logger)
{
    public async Task BulkInsertTradesAsync(
        string exchange,
        IReadOnlyList<Trade> trades,
        CancellationToken ct = default)
    {
        if (trades.Count == 0) return;

        var sw = Stopwatch.StartNew();

        // Ensure today's and tomorrow's partitions exist before writing
        await using var setupConn = await dataSource.OpenConnectionAsync(ct);
        await dbInit.EnsureDailyPartitionAsync(setupConn, DateTime.UtcNow, ct);
        await dbInit.EnsureDailyPartitionAsync(setupConn, DateTime.UtcNow.AddDays(1), ct);
        await setupConn.CloseAsync();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);

        // Step 1: COPY binary to a temp staging table (no constraints → full speed).
        // The temp table mirrors the exact columns we write; id/is_whale are omitted
        // because they are BIGSERIAL / GENERATED ALWAYS in the real table.
        await using (var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE tmp_trades (
                symbol         VARCHAR(20)    NOT NULL,
                trade_id       BIGINT         NOT NULL,
                price          NUMERIC(20, 8) NOT NULL,
                quantity       NUMERIC(20, 8) NOT NULL,
                quote_qty      NUMERIC(20, 8) NOT NULL,
                is_buyer_maker BOOLEAN        NOT NULL,
                trade_time     TIMESTAMPTZ    NOT NULL,
                ingested_at    TIMESTAMPTZ    NOT NULL,
                exchange       VARCHAR(16)    NOT NULL
            ) ON COMMIT DROP
            """, conn, tx))
        {
            await setup.ExecuteNonQueryAsync(ct);
        }

        // Wrap writer in an explicit block so it is disposed (COPY closed) before
        // running the next SQL command. Npgsql requires the connection to leave
        // Copy state before accepting a new query.
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY tmp_trades (symbol, trade_id, price, quantity, quote_qty, is_buyer_maker, trade_time, ingested_at, exchange) FROM STDIN (FORMAT BINARY)",
            ct))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var t in trades)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(t.Symbol,        NpgsqlDbType.Varchar,     ct);
                await writer.WriteAsync(t.TradeId,       NpgsqlDbType.Bigint,      ct);
                await writer.WriteAsync(t.Price,         NpgsqlDbType.Numeric,     ct);
                await writer.WriteAsync(t.Quantity,      NpgsqlDbType.Numeric,     ct);
                await writer.WriteAsync(t.QuoteQty,      NpgsqlDbType.Numeric,     ct);
                await writer.WriteAsync(t.IsBuyerMaker,  NpgsqlDbType.Boolean,     ct);
                await writer.WriteAsync(t.TradeTime,     NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(now,             NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(exchange,        NpgsqlDbType.Varchar,     ct);
            }
            await writer.CompleteAsync(ct);
        }

        // Step 2: Idempotent insert from staging table into the real partitioned table.
        // ON CONFLICT (exchange, trade_id, trade_time) DO NOTHING deduplicates Kafka
        // at-least-once redeliveries without extra in-memory state or Redis.
        long inserted;
        await using (var ins = new NpgsqlCommand("""
            WITH rows AS (
                INSERT INTO trades (symbol, trade_id, price, quantity, quote_qty,
                                    is_buyer_maker, trade_time, ingested_at, exchange)
                SELECT symbol, trade_id, price, quantity, quote_qty,
                       is_buyer_maker, trade_time, ingested_at, exchange
                FROM tmp_trades
                ON CONFLICT (exchange, trade_id, trade_time) DO NOTHING
                RETURNING 1
            )
            SELECT count(*) FROM rows
            """, conn, tx))
        {
            inserted = (long)(await ins.ExecuteScalarAsync(ct))!;
        }

        await tx.CommitAsync(ct);
        sw.Stop();

        var skipped = trades.Count - (int)inserted;
        var exchangeTag = new KeyValuePair<string, object?>("exchange", exchange);
        metrics.TradesPersisted.Add(inserted, exchangeTag);
        metrics.BulkInsertDurationMs.Record(sw.Elapsed.TotalMilliseconds, exchangeTag);
        if (skipped > 0)
        {
            metrics.DuplicatesSkipped.Add(skipped, exchangeTag);
            logger.LogInformation(
                "Dedup: {Inserted} inserted, {Skipped} duplicates skipped for {Exchange} in {Ms:F0}ms",
                inserted, skipped, exchange, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogInformation(
                "COPY inserted {Rows} {Exchange} trades in {Ms:F0}ms (first={First}, last={Last})",
                inserted, exchange, sw.Elapsed.TotalMilliseconds,
                trades[0].TradeTime, trades[^1].TradeTime);
        }
    }

    public async Task UpsertKlineAsync(Kline kline, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO klines_1m
                (symbol, open_time, close_time, open_price, high_price, low_price,
                 close_price, volume, quote_volume, num_trades)
            VALUES
                (@symbol, @open_time, @close_time, @open, @high, @low,
                 @close, @volume, @quote_vol, @num_trades)
            ON CONFLICT (symbol, open_time) DO UPDATE SET
                close_time   = EXCLUDED.close_time,
                high_price   = GREATEST(klines_1m.high_price, EXCLUDED.high_price),
                low_price    = LEAST(klines_1m.low_price, EXCLUDED.low_price),
                close_price  = EXCLUDED.close_price,
                volume       = EXCLUDED.volume,
                quote_volume = EXCLUDED.quote_volume,
                num_trades   = EXCLUDED.num_trades,
                ingested_at  = now()
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",     kline.Symbol);
        cmd.Parameters.AddWithValue("open_time",  kline.OpenTime);
        cmd.Parameters.AddWithValue("close_time", kline.CloseTime);
        cmd.Parameters.AddWithValue("open",       kline.Open);
        cmd.Parameters.AddWithValue("high",       kline.High);
        cmd.Parameters.AddWithValue("low",        kline.Low);
        cmd.Parameters.AddWithValue("close",      kline.Close);
        cmd.Parameters.AddWithValue("volume",     kline.Volume);
        cmd.Parameters.AddWithValue("quote_vol",  kline.QuoteVolume);
        cmd.Parameters.AddWithValue("num_trades", kline.NumberOfTrades);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
