using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.Shared.Bot;

/// <summary>Persists paper/real trades to the bot_trades table.</summary>
public sealed class BotRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Attach the strategy's reasoning to a trade that has just been opened.
    ///
    /// Written as a second statement rather than threaded through
    /// IOrderEngine.OpenPositionAsync, because the reasoning belongs to the strategy
    /// and the engine's job is to place orders — adding it to that signature would
    /// have touched three implementations to carry a value none of them use.
    ///
    /// Entries happen a few times an hour, so one extra UPDATE costs nothing, and
    /// failing it must never cost a position: the caller swallows the error.
    /// </summary>
    public async Task RecordEntryEvidenceAsync(
        long tradeId, decimal? composite, decimal confidence, string? rationale,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
            SET entry_composite  = @composite,
                entry_confidence = @confidence,
                entry_rationale  = @rationale
            WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", tradeId);
        cmd.Parameters.AddWithValue("composite",
            composite.HasValue ? composite.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("confidence", NpgsqlDbType.Numeric, confidence);
        cmd.Parameters.AddWithValue("rationale",
            string.IsNullOrWhiteSpace(rationale) ? DBNull.Value : rationale);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Insert a new trade ────────────────────────────────────────────────────

    public async Task<long> InsertTradeAsync(BotTrade t, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO bot_trades
              (symbol, side, strategy, entry_price, quantity, notional_usd, status, opened_at,
               mode, exchange, entry_order_id, fee_usd, exit_algo_id, leverage, margin_mode)
            VALUES
              (@symbol, @side, @strategy, @entryPrice, @qty, @notional, @status, @openedAt,
               @mode, @exchange, @entryOrderId, @feeUsd, @exitAlgoId, @leverage, @marginMode)
            RETURNING id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",     t.Symbol);
        cmd.Parameters.AddWithValue("side",       t.Side);
        cmd.Parameters.AddWithValue("strategy",   t.Strategy);
        cmd.Parameters.AddWithValue("entryPrice", NpgsqlDbType.Numeric, t.EntryPrice);
        cmd.Parameters.AddWithValue("qty",        NpgsqlDbType.Numeric, t.Quantity);
        cmd.Parameters.AddWithValue("notional",   NpgsqlDbType.Numeric, t.NotionalUsd);
        cmd.Parameters.AddWithValue("status",     t.Status);
        cmd.Parameters.AddWithValue("openedAt",   NpgsqlDbType.TimestampTz, t.OpenedAt);
        cmd.Parameters.AddWithValue("mode",       t.Mode);
        cmd.Parameters.AddWithValue("exchange",   t.Exchange);
        cmd.Parameters.AddWithValue("entryOrderId", t.EntryOrderId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("feeUsd",     NpgsqlDbType.Numeric,
            t.FeeUsd.HasValue ? t.FeeUsd.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("exitAlgoId", t.ExitAlgoId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("leverage",   NpgsqlDbType.Numeric,
            t.Leverage.HasValue ? t.Leverage.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("marginMode", t.MarginMode ?? (object)DBNull.Value);

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ── Close trade (update exit info) ────────────────────────────────────────

    public async Task CloseTradeAsync(BotTrade t, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
            SET exit_price   = @exitPrice,
                pnl_usd      = @pnlUsd,
                pnl_pct      = @pnlPct,
                status       = @status,
                closed_at    = @closedAt,
                close_reason = @reason,
                exit_order_id= @exitOrderId,
                fee_usd      = @feeUsd,
                -- Cleared on close: the protective order has either fired or been
                -- cancelled, and a stale algoId on a closed row would make the next
                -- reconciliation pass think there is still something to check.
                exit_algo_id = NULL
            WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("exitPrice", NpgsqlDbType.Numeric, t.ExitPrice!.Value);
        cmd.Parameters.AddWithValue("pnlUsd",    NpgsqlDbType.Numeric, t.PnlUsd!.Value);
        cmd.Parameters.AddWithValue("pnlPct",    NpgsqlDbType.Numeric, t.PnlPct!.Value);
        cmd.Parameters.AddWithValue("status",    t.Status);
        cmd.Parameters.AddWithValue("closedAt",  NpgsqlDbType.TimestampTz, t.ClosedAt!.Value);
        cmd.Parameters.AddWithValue("reason",    t.CloseReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("exitOrderId", t.ExitOrderId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("feeUsd",    NpgsqlDbType.Numeric,
            t.FeeUsd.HasValue ? t.FeeUsd.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("id",        t.Id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Protective order handle ───────────────────────────────────────────────

    /// <summary>
    /// Record the exchange-side OCO order guarding an open position.
    ///
    /// Written as its own statement immediately after the OCO is accepted, rather
    /// than as part of the insert, because the order cannot be placed until the
    /// entry has filled and the trade already has an id. The window between the
    /// two is the one place a protective order can exist without the database
    /// knowing about it, which is why the caller logs loudly if this fails.
    /// </summary>
    public async Task UpdateExitAlgoIdAsync(long tradeId, string? algoId, CancellationToken ct = default)
    {
        const string sql = "UPDATE bot_trades SET exit_algo_id = @algoId WHERE id = @id";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("algoId", algoId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("id",     tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Shrink a still-open trade to what it actually holds, after an exit filled
    /// only part of it, and carry the fee already paid.
    ///
    /// The row deliberately stays OPEN. Closing it on a partial fill would abandon
    /// the remainder — still a leveraged position, its protective order cancelled by
    /// the exit attempt, and nothing left referring to it.
    /// </summary>
    public async Task UpdateOpenQuantityAsync(
        long tradeId, decimal quantity, decimal feeUsd, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
            SET quantity = @qty, fee_usd = @feeUsd
            WHERE id = @id AND status = 'OPEN'
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("qty",    NpgsqlDbType.Numeric, quantity);
        cmd.Parameters.AddWithValue("feeUsd", NpgsqlDbType.Numeric, feeUsd);
        cmd.Parameters.AddWithValue("id",     tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Open positions ────────────────────────────────────────────────────────

    /// <summary>
    /// Every position still marked OPEN, oldest first.
    ///
    /// This is what lets a restarted worker take responsibility for positions it
    /// did not open. It deliberately does not reuse GetRecentTradesAsync with a
    /// limit: an open position can be arbitrarily older than the most recent N
    /// trades, and a live position missed by a limit clause is a real holding with
    /// nothing evaluating its stop loss.
    /// </summary>
    public async Task<IReadOnlyList<BotTrade>> GetOpenTradesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, symbol, side, strategy, entry_price, exit_price, quantity, notional_usd,
                   pnl_usd, pnl_pct, status, opened_at, closed_at, close_reason, peak_price,
                   mode, exchange, entry_order_id, exit_order_id, fee_usd, exit_algo_id,
                   leverage, margin_mode
            FROM bot_trades
            WHERE status = 'OPEN'
            ORDER BY opened_at ASC
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<BotTrade>();
        while (await reader.ReadAsync(ct))
            result.Add(MapRow(reader));
        return result;
    }

    // ── Update peak price (trailing stop tracking) ────────────────────────────

    public async Task UpdatePeakPriceAsync(long tradeId, decimal peakPrice, CancellationToken ct = default)
    {
        const string sql = "UPDATE bot_trades SET peak_price = @peak WHERE id = @id";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("peak", NpgsqlDbType.Numeric, peakPrice);
        cmd.Parameters.AddWithValue("id",   tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Query recent trades ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<BotTrade>> GetRecentTradesAsync(
        int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, symbol, side, strategy, entry_price, exit_price, quantity, notional_usd,
                   pnl_usd, pnl_pct, status, opened_at, closed_at, close_reason, peak_price,
                   mode, exchange, entry_order_id, exit_order_id, fee_usd, exit_algo_id,
                   leverage, margin_mode
            FROM bot_trades
            ORDER BY opened_at DESC
            LIMIT @limit
            """;

        await using var conn   = await dataSource.OpenConnectionAsync(ct);
        await using var cmd    = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<BotTrade>();
        while (await reader.ReadAsync(ct))
            result.Add(MapRow(reader));
        return result;
    }

    // ── Daily P&L for loss limit check ────────────────────────────────────────

    /// <summary>
    /// Today's realised P&amp;L, optionally narrowed to one instrument and one
    /// execution mode.
    ///
    /// The narrowing matters because this number is what the daily-loss circuit
    /// breaker acts on. Summed across modes, a run of simulated profits offsets real
    /// losses and the limit never trips; summed across symbols, results from an
    /// instrument the bot is no longer trading decide whether it may keep trading
    /// the one it is. Both arguments default to null so existing display callers,
    /// which do want the whole picture, are unaffected.
    /// </summary>
    public async Task<decimal> GetTodayPnlAsync(
        string? symbol = null, string? mode = null, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(SUM(pnl_usd), 0)
            FROM bot_trades
            WHERE DATE(closed_at AT TIME ZONE 'UTC') = CURRENT_DATE
              AND status IN ('CLOSED','STOPPED')
              AND (@symbol IS NULL OR symbol = @symbol)
              AND (@mode   IS NULL OR mode   = @mode)
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("mode",   mode   ?? (object)DBNull.Value);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ── Get latest price for a symbol (for unrealized PnL calculation) ─────────

    public async Task<decimal?> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
    {
        const string sql = """
            SELECT close_price FROM klines_1m
            WHERE symbol = @symbol
            ORDER BY open_time DESC
            LIMIT 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol", symbol);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is decimal d ? d : null;
    }

    // ── Mapper ────────────────────────────────────────────────────────────────

    private static BotTrade MapRow(NpgsqlDataReader r) => new()
    {
        Id          = r.GetInt64(0),
        Symbol      = r.GetString(1),
        Side        = r.GetString(2),
        Strategy    = r.GetString(3),
        EntryPrice  = r.GetDecimal(4),
        ExitPrice   = r.IsDBNull(5)  ? null : r.GetDecimal(5),
        Quantity    = r.GetDecimal(6),
        NotionalUsd = r.GetDecimal(7),
        PnlUsd      = r.IsDBNull(8)  ? null : r.GetDecimal(8),
        PnlPct      = r.IsDBNull(9)  ? null : r.GetDecimal(9),
        Status      = r.GetString(10),
        OpenedAt    = r.GetDateTime(11),
        ClosedAt    = r.IsDBNull(12) ? null : r.GetDateTime(12),
        CloseReason = r.IsDBNull(13) ? null : r.GetString(13),
        PeakPrice   = r.IsDBNull(14) ? null : r.GetDecimal(14),
        Mode         = r.GetString(15),
        Exchange     = r.GetString(16),
        EntryOrderId = r.IsDBNull(17) ? null : r.GetString(17),
        ExitOrderId  = r.IsDBNull(18) ? null : r.GetString(18),
        FeeUsd       = r.IsDBNull(19) ? null : r.GetDecimal(19),
        ExitAlgoId   = r.IsDBNull(20) ? null : r.GetString(20),
        Leverage     = r.IsDBNull(21) ? null : r.GetDecimal(21),
        MarginMode   = r.IsDBNull(22) ? null : r.GetString(22),
    };
}
