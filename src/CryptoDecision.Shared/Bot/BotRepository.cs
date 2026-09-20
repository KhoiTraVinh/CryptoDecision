using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.Shared.Bot;

/// <summary>
/// What this account's own history says about a candidate, at the moment it is proposed.
///
/// Every field is something the entry gate is permitted to refuse against, and every one
/// of them can actually occur — which is the whole point, and was not true of the four
/// grounds this replaces. See <see cref="BotRepository.GetGateEvidenceAsync"/>.
/// </summary>
/// <param name="CellTrades">Closed trades in this exact strategy + side + entry path, most recent first.</param>
/// <param name="CellMeanR">Mean R over those. The narrowest honest base rate for this candidate.</param>
/// <param name="RuleTrades">Closed trades for the whole strategy, for when the cell is too thin to read.</param>
/// <param name="MinutesSinceSamePath">
/// Since the last entry through this same path, or -1 for none in 24 hours. Four
/// HIGH_VOLUME entries fired inside 90 minutes on 2026-09-18 and lost 1.499R between
/// them; the per-path position cap bounds CONCURRENT positions and did not see it.
/// </param>
/// <param name="Move4hPct">SOL's move over the last four hours, in percent. Signed.</param>
/// <param name="Move12hPct">The same over twelve, which is also the maximum hold.</param>
/// <param name="SessionTrades">
/// Closed trades for this strategy inside the SAME session of day as now. The session
/// split is <see cref="IsUsSession"/>.
///
/// Measured 2026-09-19 on all 68 recorded signals replayed with the deployed exit set:
/// entries between 12:00 and 20:00 UTC total **-10.745R over 32 trades** while everything
/// outside that window totals +9.923R over 36. Every threshold from 06:00 to 18:00 splits
/// the same way, so it is a plateau rather than one lucky cut — and unlike the positive
/// half, **the negative half does not move when the two largest winners are removed**
/// (-0.336 mean either way). The mechanism is the one the FATAL IN A TREND note already
/// names: 12:00-20:00 UTC is the European afternoon and the US session, where macro news
/// and institutional flow produce the sustained moves that both of these short-horizon
/// rules die in.
/// </param>
public sealed record GateEvidence(
    int     CellTrades,
    int     CellWins,
    decimal CellMeanR,
    int     SessionTrades,
    int     SessionWins,
    decimal SessionMeanR,
    int     RuleTrades,
    int     RuleWins,
    decimal RuleMeanR,
    double  MinutesSinceSamePath,
    decimal Move4hPct,
    decimal Move12hPct)
{
    /// <summary>
    /// Nothing known. Every threshold below reads this as "ground unavailable", so a
    /// failed evidence query makes the gate no stricter than it was — it must never
    /// turn a database hiccup into a refusal.
    /// </summary>
    public static readonly GateEvidence Unknown = new(0, 0, 0m, 0, 0, 0m, 0, 0, 0m, -1d, 0m, 0m);

    /// <summary>
    /// Whether a UTC instant falls in the session that measured negative.
    ///
    /// 12:00-20:00 came out of a threshold scan, which is worth saying plainly rather than
    /// presenting it as chosen from theory — but it is not a fitted boundary in the way a
    /// tuned z-score is. It has no estimation error, it is known before the trade rather
    /// than inferred from price, every neighbouring cut from 06:00 to 18:00 splits the same
    /// direction, and it coincides with the conventional crypto "US session" window.
    ///
    /// The honest caveat stays: 19 days cannot separate "the US session trends" from "these
    /// particular 19 days trended during the US session". That is what H19 is for.
    /// </summary>
    public static bool IsUsSession(DateTime utc) => utc.Hour is >= 12 and < 20;

    /// <summary>
    /// Below this the cell's mean R is an anecdote. Five is not a sample either; it is
    /// the point at which a run of losses stops being one bad afternoon.
    /// </summary>
    public const int MinCellTrades = 5;

    /// <summary>
    /// Percent move over four hours that counts as a trend rather than noise. SOL's
    /// median 15-minute true range is 1.07%, so 2% over four hours is a directional
    /// market rather than ordinary movement.
    /// </summary>
    public const decimal TrendPct = 2.0m;

    /// <summary>Minutes within which a second entry on the same path is one event twice.</summary>
    public const double ClusterMinutes = 120d;

    /// <summary>
    /// The cell's own record is losing, OR this session is. Either slice reaching
    /// <see cref="MinCellTrades"/> and coming out negative makes the ground available.
    ///
    /// Two slices rather than one because they fail at different times and the gate
    /// should see whichever has evidence: the cell is the narrowest read but takes
    /// longest to fill, and the session slice fills faster because it ignores side and
    /// entry path. Both are the account's own closed trades, so neither is a forecast.
    /// </summary>
    public bool CellIsLosing => (CellTrades    >= MinCellTrades && CellMeanR    < 0m)
                             || (SessionTrades >= MinCellTrades && SessionMeanR < 0m);

    // Two per-slice properties stood here for one commit — CellSliceIsLosing and
    // SessionSliceIsLosing, added "so the brief can say which slice is carrying the
    // ground" and then never called, because the brief prints all three slices with their
    // counts and lets the reader see it. Dead on arrival, in the same commit that added
    // them. If a caller ever does need one slice's verdict on its own, write it then.

    public bool ClusteredPath => MinutesSinceSamePath >= 0d && MinutesSinceSamePath < ClusterMinutes;

    /// <summary>True when the last four hours ran against the side being proposed.</summary>
    public bool TrendAgainst(string side) =>
        string.Equals(side, "SHORT", StringComparison.OrdinalIgnoreCase)
            ? Move4hPct >=  TrendPct
            : Move4hPct <= -TrendPct;
}

/// <summary>
/// One closed trade, for the short ledger the gate is shown beside the averages.
/// See <see cref="BotRepository.GetRecentTradesForRuleAsync"/>.
/// </summary>
public sealed record RecentTrade(DateTime ClosedAt, string Side, string CloseReason, decimal R);

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
    /// Attach the exit geometry and the gate's verdict to a freshly opened trade.
    ///
    /// A second statement rather than parameters on IOrderEngine.OpenPositionAsync,
    /// for the reason <see cref="RecordEntryEvidenceAsync"/> already gives: these are
    /// facts the strategy and the gate own, and threading them through the engine
    /// would touch three implementations to carry values none of them use.
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
        string gateVerdict,
        string? gateReason,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_trades
            SET stop_price       = @stop,
                target_price     = @target,
                atr_pct_at_entry = @atr,
                gate_verdict     = @verdict,
                gate_reason      = @reason
            WHERE id = @id
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id",      tradeId);
        cmd.Parameters.AddWithValue("stop",    NpgsqlDbType.Numeric, stopPrice);
        cmd.Parameters.AddWithValue("target",  NpgsqlDbType.Numeric, targetPrice);
        cmd.Parameters.AddWithValue("atr",     NpgsqlDbType.Numeric, atrPct);
        cmd.Parameters.AddWithValue("verdict", gateVerdict);
        cmd.Parameters.AddWithValue("reason",
            string.IsNullOrWhiteSpace(gateReason) ? DBNull.Value : gateReason);

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
    public async Task<IReadOnlyList<BotTrade>> GetOpenTradesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, symbol, side, strategy, entry_price, exit_price, quantity, notional_usd,
                   pnl_usd, pnl_pct, status, opened_at, closed_at, close_reason, peak_price,
                   mode, exchange, entry_order_id, exit_order_id, fee_usd, exit_algo_id,
                   leverage, margin_mode, stop_price, target_price, atr_pct_at_entry,
                   gate_verdict, gate_reason, entry_path
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
        const string sql = """
            SELECT id, symbol, side, strategy, entry_price, exit_price, quantity, notional_usd,
                   pnl_usd, pnl_pct, status, opened_at, closed_at, close_reason, peak_price,
                   mode, exchange, entry_order_id, exit_order_id, fee_usd, exit_algo_id,
                   leverage, margin_mode, stop_price, target_price, atr_pct_at_entry,
                   gate_verdict, gate_reason, entry_path
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

    /// <summary>
    /// The account's own recent history, as the four numbers the entry gate is allowed
    /// to refuse against.
    ///
    /// Why this exists
    /// ---------------
    /// The gate approved 45 of 45 candidates and refused none, and the cause was not the
    /// model: every one of the four grounds the prompt offered it was unreachable by
    /// construction. Dispersion needed a ceiling, and MaxDispersionBps is 0. Thin
    /// evidence needed an excluded venue, and neither surviving rule scores venues at
    /// all. A losing day needed half the daily limit — $2.25 — against a worst observed
    /// day of $0.65. Concentration needed two open positions, and the per-side limit is
    /// checked before the gate is ever called. The model said so in its own words on
    /// every single call: "is not subject to any grounds for skipping".
    ///
    /// So the fix is not a better prompt or a bigger model. It is grounds that a real
    /// candidate can actually meet. These four can:
    ///
    ///   • this exact cell — strategy, side and entry path — is losing on its own record
    ///   • the market is trending against the direction being proposed
    ///   • the same entry path already fired recently, so this is one event twice
    ///   • something is already open on this side, across every strategy
    ///
    /// Each is a number this account produced. None of them was available to the gate
    /// before.
    ///
    /// Why the model and not an `if`
    /// -----------------------------
    /// Any one of these reduces to a threshold, and a threshold belongs in RiskEngine
    /// where it is deterministic and free. What is handed to the model is deliberately
    /// weaker: the brief states which grounds are AVAILABLE, not which ones bind. Three
    /// marginal readings together may be worth refusing on and one alone may not, and
    /// that combination is the only thing here a language model can do that an `if`
    /// cannot. If a single one of these ever turns out to be decisive on its own, move
    /// it into the deterministic layer and take it out of the brief.
    ///
    /// One query, four scalar sub-selects. The gate costs 34 seconds of inference
    /// against a 75-second timeout, so the budget for gathering its evidence is a single
    /// round trip.
    /// </summary>
    /// <param name="entryPath">
    /// Null is a value, not a wildcard: CANDLE_REVERSAL writes no path and its rows
    /// carry NULL, so the cell for it is "rows whose path is also NULL". Matched with
    /// IS NOT DISTINCT FROM for exactly that reason.
    /// </param>
    /// <param name="lookback">
    /// Closed trades in the cell to average over. 20 rather than all of history because
    /// the question is "is this cell losing NOW" — a rule that was fixed a week ago
    /// should not keep being refused for what it did before.
    /// </param>
    /// <summary>
    /// The last few closed trades from one rule, newest first, as a short ledger.
    ///
    /// The three slices in <see cref="GateEvidence"/> are averages, and an average hides
    /// the thing the operator most wants the gate to notice: that the previous trade from
    /// this same rule just lost. On 2026-09-20 the gate approved trade 105 while trade 104
    /// — same rule, same side, opened 98 minutes earlier — had stopped out at -1.12R
    /// SEVENTEEN SECONDS before the evidence query ran. The brief marked "one event twice"
    /// as an available ground and never said how that event ended.
    ///
    /// Deliberately raw rows rather than another statistic. Five lines cost about sixty
    /// tokens, which is roughly two seconds of generation against a 120s cycle budget, and
    /// a sequence is something a reader can judge where a mean is something they have to
    /// trust.
    /// </summary>
    public async Task<IReadOnlyList<RecentTrade>> GetRecentTradesForRuleAsync(
        string symbol, string mode, string strategy, int limit = 5,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT closed_at, side, COALESCE(close_reason, '?'),
                   pnl_pct / NULLIF(ABS(entry_price - stop_price) / entry_price, 0) AS r
            FROM bot_trades
            WHERE symbol = @symbol AND mode = @mode AND strategy = @strategy
              AND status IN ('CLOSED','STOPPED')
              AND stop_price IS NOT NULL AND entry_price > 0
            ORDER BY closed_at DESC
            LIMIT @limit
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",   symbol);
        cmd.Parameters.AddWithValue("mode",     mode);
        cmd.Parameters.AddWithValue("strategy", strategy);
        cmd.Parameters.AddWithValue("limit",    limit);

        var rows = new List<RecentTrade>(limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new RecentTrade(
                ClosedAt:    r.GetDateTime(0),
                Side:        r.GetString(1),
                CloseReason: r.GetString(2),
                R:           r.IsDBNull(3) ? 0m : r.GetDecimal(3)));

        return rows;
    }

    public async Task<GateEvidence> GetGateEvidenceAsync(
        string symbol, string mode, string strategy, string side,
        string? entryPath, int lookback = 20, CancellationToken ct = default)
    {
        // R is recomputed here rather than stored, the same way every analysis of this
        // table does it: pnl_pct over the stop distance the trade was actually sized
        // against. Rows with no stop are excluded rather than counted as zero.
        const string sql = """
            WITH scoped AS (
                SELECT strategy, side, entry_path, opened_at, closed_at, pnl_usd,
                       pnl_pct / NULLIF(ABS(entry_price - stop_price) / entry_price, 0) AS r
                FROM bot_trades
                WHERE symbol = @symbol AND mode = @mode
                  AND status IN ('CLOSED','STOPPED')
                  AND stop_price IS NOT NULL AND entry_price > 0
            ),
            cell AS (
                SELECT COUNT(*) n,
                       COUNT(*) FILTER (WHERE r > 0) wins,
                       COALESCE(AVG(r), 0) mean_r
                FROM (SELECT r FROM scoped
                       WHERE strategy = @strategy AND side = @side
                         AND COALESCE(entry_path, '') = COALESCE(@path::text, '')
                       ORDER BY closed_at DESC LIMIT @lookback) c
            ),
            sess AS (
                -- Same strategy, same session of day. Deliberately NOT scoped by side or
                -- entry path: this slice exists because it fills faster than the cell,
                -- and narrowing it further would defeat the point of having two.
                SELECT COUNT(*) n,
                       COUNT(*) FILTER (WHERE r > 0) wins,
                       COALESCE(AVG(r), 0) mean_r
                FROM (SELECT r FROM scoped
                       WHERE strategy = @strategy
                         AND (EXTRACT(hour FROM opened_at AT TIME ZONE 'UTC') >= 12
                          AND EXTRACT(hour FROM opened_at AT TIME ZONE 'UTC') <  20) = @inUs
                       ORDER BY closed_at DESC LIMIT @lookback) s
            ),
            rule AS (
                SELECT COUNT(*) n,
                       COUNT(*) FILTER (WHERE r > 0) wins,
                       COALESCE(AVG(r), 0) mean_r
                FROM (SELECT r FROM scoped
                       WHERE strategy = @strategy
                       ORDER BY closed_at DESC LIMIT @lookback) s
            ),
            samepath AS (
                SELECT COALESCE(
                    MIN(EXTRACT(epoch FROM (now() - opened_at)) / 60.0), -1) mins
                FROM bot_trades
                WHERE symbol = @symbol AND mode = @mode AND strategy = @strategy
                  AND COALESCE(entry_path, '') = COALESCE(@path::text, '')
                  AND opened_at > now() - interval '24 hours'
            ),
            px AS (
                SELECT
                  (SELECT close_price FROM klines_1m
                    WHERE symbol = @symbol ORDER BY open_time DESC LIMIT 1) AS now_px,
                  (SELECT close_price FROM klines_1m
                    WHERE symbol = @symbol AND open_time <= now() - interval '4 hours'
                    ORDER BY open_time DESC LIMIT 1) AS px4,
                  (SELECT close_price FROM klines_1m
                    WHERE symbol = @symbol AND open_time <= now() - interval '12 hours'
                    ORDER BY open_time DESC LIMIT 1) AS px12
            )
            SELECT cell.n, cell.wins, cell.mean_r,
                   sess.n, sess.wins, sess.mean_r,
                   rule.n, rule.wins, rule.mean_r,
                   samepath.mins,
                   CASE WHEN px.px4  > 0 THEN (px.now_px - px.px4)  / px.px4  * 100 ELSE 0 END,
                   CASE WHEN px.px12 > 0 THEN (px.now_px - px.px12) / px.px12 * 100 ELSE 0 END
            FROM cell, sess, rule, samepath, px
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("symbol",   symbol);
        cmd.Parameters.AddWithValue("mode",     mode);
        cmd.Parameters.AddWithValue("strategy", strategy);
        cmd.Parameters.AddWithValue("side",     side);
        cmd.Parameters.AddWithValue("path",     (object?)entryPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lookback", lookback);
        cmd.Parameters.AddWithValue("inUs",     GateEvidence.IsUsSession(DateTime.UtcNow));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return GateEvidence.Unknown;

        return new GateEvidence(
            CellTrades:       (int)r.GetInt64(0),
            CellWins:         (int)r.GetInt64(1),
            CellMeanR:        r.IsDBNull(2) ? 0m : r.GetDecimal(2),
            SessionTrades:    (int)r.GetInt64(3),
            SessionWins:      (int)r.GetInt64(4),
            SessionMeanR:     r.IsDBNull(5) ? 0m : r.GetDecimal(5),
            RuleTrades:       (int)r.GetInt64(6),
            RuleWins:         (int)r.GetInt64(7),
            RuleMeanR:        r.IsDBNull(8) ? 0m : r.GetDecimal(8),
            MinutesSinceSamePath: r.IsDBNull(9) ? -1d : (double)r.GetDecimal(9),
            Move4hPct:        r.IsDBNull(10) ? 0m : r.GetDecimal(10),
            Move12hPct:       r.IsDBNull(11) ? 0m : r.GetDecimal(11));
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

        // Ordinals continue the SELECT list above. Positional rather than by name
        // because every query in this class shares one column list, so the two stay
        // in step or neither compiles.
        StopPrice     = r.IsDBNull(23) ? null : r.GetDecimal(23),
        TargetPrice   = r.IsDBNull(24) ? null : r.GetDecimal(24),
        AtrPctAtEntry = r.IsDBNull(25) ? null : r.GetDecimal(25),
        GateVerdict   = r.IsDBNull(26) ? null : r.GetString(26),
        GateReason    = r.IsDBNull(27) ? null : r.GetString(27),
        EntryPath     = r.IsDBNull(28) ? null : r.GetString(28),
    };
}
