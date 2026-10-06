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

    /// <summary>
    /// Attach the exit geometry to a freshly opened trade.
    ///
    /// A second statement rather than parameters on IOrderEngine.OpenPositionAsync,
    /// for the reason <see cref="RecordEntryEvidenceAsync"/> already gives: these are
    /// facts the strategy owns, and threading them through the engine would touch
    /// three implementations to carry values none of them use.
    ///
    /// It no longer writes a gate verdict — the gate went with H30, and the sentence
    /// saying it did outlived the statement by a week.
    ///
    /// Unlike the evidence write, this one is <em>not</em> safe to swallow. The stop
    /// price is what the exit evaluation reads; a position whose geometry failed to
    /// persist falls back to the configured percentages, which for a volatility-scaled
    /// stop is a different — and possibly much tighter — level than the one the entry
    /// was sized against. The caller has to know.
    /// </summary>
    public async Task RecordEntryGeometryAsync(
        long tradeId,
        decimal stopPrice,
        decimal targetPrice,
        decimal atrPct,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
            SET stop_price       = @stop,
                target_price     = @target,
                atr_pct_at_entry = @atr
            WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id",      tradeId);
        cmd.Parameters.AddWithValue("stop",    NpgsqlDbType.Numeric, stopPrice);
        cmd.Parameters.AddWithValue("target",  NpgsqlDbType.Numeric, targetPrice);
        cmd.Parameters.AddWithValue("atr",     NpgsqlDbType.Numeric, atrPct);

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        if (rows != 1)
            throw new InvalidOperationException(
                $"Expected to set exit geometry on exactly one row for trade {tradeId}, " +
                $"but {rows} were updated. The position is open without the stop level the " +
                "entry was sized against.");
    }

    // ── Insert a new trade ────────────────────────────────────────────────────

    public async Task<long> InsertTradeAsync(BotTrade t, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO bot_trades
              (symbol, side, strategy, entry_price, quantity, notional_usd, status, opened_at,
               mode, exchange, entry_order_id, fee_usd, exit_algo_id, leverage, margin_mode,
               entry_path)
            VALUES
              (@symbol, @side, @strategy, @entryPrice, @qty, @notional, @status, @openedAt,
               @mode, @exchange, @entryOrderId, @feeUsd, @exitAlgoId, @leverage, @marginMode,
               @entryPath)
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

        // Written on the INSERT rather than by a follow-up UPDATE. A second statement
        // would leave a window where the row exists with entry_path NULL, and NULL is
        // read as "not high-volume" by the concurrency limit — so a crash in that window
        // would quietly free the slot the limit exists to hold.
        cmd.Parameters.AddWithValue("entryPath", t.EntryPath ?? (object)DBNull.Value);

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
    /// <summary>
    /// The one column list every <see cref="BotTrade"/> query selects.
    ///
    /// It used to be written out twice, in two separate string literals, under a
    /// comment claiming they "stay in step or neither compiles". Nothing checked
    /// that — they were two literals kept identical by hand, feeding a mapper that
    /// read by POSITION. Inserting a column anywhere but the end of one of them
    /// would have silently shifted every read after it, and the shift lands on
    /// whatever happens to have a compatible type. Now there is genuinely one list,
    /// and <see cref="MapRow"/> reads by name, so neither hazard exists.
    ///
    /// gate_verdict and gate_reason are gone from here. H30 deleted the entry gate
    /// on 2026-09-29; the follow-up removed FindSimilarAsync and SimilarCase but
    /// missed these two, which were still fetched on every open-trade read and
    /// mapped onto properties nothing ever looked at. The COLUMNS stay in the table
    /// — 92 historical rows carry verdicts that recorded decisions refer to, and
    /// dropping them would destroy that evidence to save two fetched strings.
    /// </summary>
    private const string TradeColumns = """
        id, symbol, side, strategy, entry_price, exit_price, quantity, notional_usd,
        pnl_usd, pnl_pct, status, opened_at, closed_at, close_reason, peak_price,
        mode, exchange, entry_order_id, exit_order_id, fee_usd, exit_algo_id,
        leverage, margin_mode, stop_price, target_price, atr_pct_at_entry,
        entry_path, last_exit_review_at
        """;

    public async Task<IReadOnlyList<BotTrade>> GetOpenTradesAsync(CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT {TradeColumns}
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

    // ── Update peak price (the breakeven stop reads it) ───────────────────────

    public async Task UpdatePeakPriceAsync(long tradeId, decimal peakPrice, CancellationToken ct = default)
    {
        const string sql = "UPDATE bot_trades SET peak_price = @peak WHERE id = @id";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("peak", NpgsqlDbType.Numeric, peakPrice);
        cmd.Parameters.AddWithValue("id",   tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Record where the dynamic widening currently has the barriers.
    ///
    /// Write-only by design. Nothing reads these back — the widening is recomputed each
    /// cycle from <c>stop_price</c> / <c>target_price</c>, and feeding a scaled value into
    /// the column that arithmetic reads would compound it every 30 seconds. See sql/035.
    ///
    /// Null clears them, which is the correct state when the feature is off or the trade
    /// has no favourable excursion: "not applicable" rather than "unchanged".
    /// </summary>
    /// <summary>
    /// Stamp when the LLM was last asked whether to cut this position.
    ///
    /// Written even when the answer was HOLD, and even when the model could not answer at
    /// all — the stamp paces the NEXT question, so a failed review must still consume its
    /// slot. Without that, an Ollama outage would make the bot retry every 30 seconds and
    /// spend the whole cycle budget on a service that is down.
    /// </summary>
    /// <summary>
    /// Stamp the review time, and keep what the reviewer said.
    ///
    /// The note rides on the UPDATE that was already happening, so persisting the
    /// reasoning costs no extra round trip. It used to live only in `docker logs`,
    /// which a deploy destroys because the container is recreated rather than
    /// restarted — see sql/047.
    /// </summary>
    public async Task StampExitReviewAsync(
        long tradeId, DateTime reviewedAt, string? note = null, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
               SET last_exit_review_at   = @at,
                   -- COALESCE so a reviewer that answered with nothing cannot erase the
                   -- previous answer. A review that could not answer at all does not
                   -- reach here; it keeps its slot but leaves the note alone.
                   last_exit_review_note = COALESCE(@note, last_exit_review_note)
             WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("at", reviewedAt);
        cmd.Parameters.AddWithValue("note",
            string.IsNullOrWhiteSpace(note) ? DBNull.Value : note);
        cmd.Parameters.AddWithValue("id", tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateDynamicLevelsAsync(
        long tradeId, decimal? stopPrice, decimal? targetPrice, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
               SET dynamic_stop_price = @stop, dynamic_target_price = @target
             WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("stop",   NpgsqlDbType.Numeric, (object?)stopPrice   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)targetPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id",     tradeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Query recent trades ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<BotTrade>> GetRecentTradesAsync(
        int limit = 50, CancellationToken ct = default)
    {
        const string sql = $"""
            SELECT {TradeColumns}
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



    // ── Mapper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read a row by COLUMN NAME, not by position.
    ///
    /// The positional version broke silently by construction: a column inserted
    /// anywhere but the end of <see cref="TradeColumns"/> shifts every read after
    /// it, and the shift only throws if the types happen to disagree. A decimal
    /// landing in a decimal is a wrong number with no error — exactly the failure
    /// shape this bot keeps producing.
    ///
    /// GetOrdinal is resolved per field per row, which at a few hundred rows a day
    /// is not worth caching to get back.
    /// </summary>
    private static BotTrade MapRow(NpgsqlDataReader r)
    {
        decimal? Dec(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetDecimal(r.GetOrdinal(c));
        string?  Str(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
        DateTime? Dt(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetDateTime(r.GetOrdinal(c));

        return new BotTrade
        {
            Id          = r.GetInt64(r.GetOrdinal("id")),
            Symbol      = r.GetString(r.GetOrdinal("symbol")),
            Side        = r.GetString(r.GetOrdinal("side")),
            Strategy    = r.GetString(r.GetOrdinal("strategy")),
            EntryPrice  = r.GetDecimal(r.GetOrdinal("entry_price")),
            ExitPrice   = Dec("exit_price"),
            Quantity    = r.GetDecimal(r.GetOrdinal("quantity")),
            NotionalUsd = r.GetDecimal(r.GetOrdinal("notional_usd")),
            PnlUsd      = Dec("pnl_usd"),
            PnlPct      = Dec("pnl_pct"),
            Status      = r.GetString(r.GetOrdinal("status")),
            OpenedAt    = r.GetDateTime(r.GetOrdinal("opened_at")),
            ClosedAt    = Dt("closed_at"),
            CloseReason = Str("close_reason"),
            PeakPrice   = Dec("peak_price"),

            Mode         = r.GetString(r.GetOrdinal("mode")),
            Exchange     = r.GetString(r.GetOrdinal("exchange")),
            EntryOrderId = Str("entry_order_id"),
            ExitOrderId  = Str("exit_order_id"),
            FeeUsd       = Dec("fee_usd"),
            ExitAlgoId   = Str("exit_algo_id"),
            Leverage     = Dec("leverage"),
            MarginMode   = Str("margin_mode"),

            StopPrice        = Dec("stop_price"),
            TargetPrice      = Dec("target_price"),
            AtrPctAtEntry    = Dec("atr_pct_at_entry"),
            EntryPath        = Str("entry_path"),
            LastExitReviewAt = Dt("last_exit_review_at"),
        };
    }
}
