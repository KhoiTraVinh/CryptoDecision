using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.Shared.Bot;

/// <summary>
/// Reads/writes the singleton bot_config row.
///
/// This used to bridge an API and the worker: the API wrote start/stop and the full
/// configuration, the worker polled and executed. The API and its dashboard are
/// gone, so the row is now written by hand and read by the worker — which is why
/// StartBotAsync was removed rather than left as a method with no caller. Starting
/// the bot is:
///
///     UPDATE bot_config SET enabled = true WHERE id = 1;
///
/// and the worker picks it up within one poll. Everything the worker still writes
/// here — the heartbeat, the refusal trail, the last verdict, the sizing note — is
/// written so it survives a container restart and can be read with SQL, which was
/// always the reason and is not affected by there being no web page.
/// </summary>
public sealed class BotConfigRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Read the current bot config from the database (used by Bot worker).</summary>
    public async Task<BotOptions?> GetConfigAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT enabled, paper_mode, symbol, exchange, active_strategies,
                   capital_usd, max_open_trades_per_strategy, position_pct,
                   take_profit_pct, stop_loss_pct, cooldown_seconds, max_hold_minutes,
                   daily_loss_limit_pct, eval_interval_seconds,
                   COALESCE(use_breakeven_stop, TRUE) AS use_breakeven_stop,
                   COALESCE(breakeven_trigger_pct, 0.005) AS breakeven_trigger_pct,
                   COALESCE(use_dynamic_tp_sl, FALSE) AS use_dynamic_tp_sl,
                   COALESCE(use_ai_sizing, FALSE) AS use_ai_sizing,
                   COALESCE(require_ai_gate, TRUE) AS require_ai_gate,
                   COALESCE(allow_entry_without_gate, FALSE) AS allow_entry_without_gate,
                   COALESCE(max_entries_per_day, 6) AS max_entries_per_day,
                   COALESCE(risk_pct_per_trade, 0.01) AS risk_pct_per_trade
            FROM bot_config WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        await using var r    = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        // Mapped by column name, not by ordinal.
        //
        // Ordinals used to pin this method: every column had to stay in the SELECT
        // in its original position, because deleting one renumbered every
        // GetDecimal(n) after it, and an off-by-one here feeds the wrong number
        // into a live trading parameter — a stop loss reading a cooldown, say.
        // That fear is what kept three dead columns in the query and three dead
        // properties on BotOptions. Names cost one dictionary lookup per field and
        // make the whole class of mistake impossible, so the dead weight could go.
        return new BotOptions
        {
            Enabled                  = r.GetBoolean(r.GetOrdinal("enabled")),
            PaperMode                = r.GetBoolean(r.GetOrdinal("paper_mode")),
            Symbol                   = r.GetString(r.GetOrdinal("symbol")),
            Exchange                 = r.GetString(r.GetOrdinal("exchange")),
            ActiveStrategies         = ((string[])r.GetValue(r.GetOrdinal("active_strategies"))).ToList(),
            CapitalUsd               = r.GetDecimal(r.GetOrdinal("capital_usd")),
            MaxOpenTradesPerStrategy = r.GetInt32(r.GetOrdinal("max_open_trades_per_strategy")),
            PositionPctOfCapital     = r.GetDecimal(r.GetOrdinal("position_pct")),
            TakeProfitPct            = r.GetDecimal(r.GetOrdinal("take_profit_pct")),
            StopLossPct              = r.GetDecimal(r.GetOrdinal("stop_loss_pct")),
            CooldownSeconds          = r.GetInt32(r.GetOrdinal("cooldown_seconds")),
            MaxHoldMinutes           = r.GetInt32(r.GetOrdinal("max_hold_minutes")),
            DailyLossLimitPct        = r.GetDecimal(r.GetOrdinal("daily_loss_limit_pct")),
            EvalIntervalSeconds      = r.GetInt32(r.GetOrdinal("eval_interval_seconds")),
            UseBreakevenStop         = r.GetBoolean(r.GetOrdinal("use_breakeven_stop")),
            BreakevenTriggerPct      = r.GetDecimal(r.GetOrdinal("breakeven_trigger_pct")),
            UseDynamicTpSl           = r.GetBoolean(r.GetOrdinal("use_dynamic_tp_sl")),
            UseAiSizing              = r.GetBoolean(r.GetOrdinal("use_ai_sizing")),
            RequireAiGate            = r.GetBoolean(r.GetOrdinal("require_ai_gate")),
            AllowEntryWithoutGate    = r.GetBoolean(r.GetOrdinal("allow_entry_without_gate")),
            MaxEntriesPerDay         = r.GetInt32(r.GetOrdinal("max_entries_per_day")),
            RiskPctPerTrade          = r.GetDecimal(r.GetOrdinal("risk_pct_per_trade")),
        };
    }

    /// <summary>Update heartbeat + runtime stats so the bot's state survives a restart and is readable with SQL.</summary>
    public async Task UpdateHeartbeatAsync(
        DateTime lastEvalAt, int openTradeCount, int totalTrades,
        decimal totalPnlUsd, int winCount, int lossCount,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_config
            SET last_heartbeat   = NOW(),
                last_eval_at     = @lastEval,
                open_trade_count = @openCount,
                total_trades     = @totalTrades,
                total_pnl_usd   = @pnl,
                win_count        = @wins,
                loss_count       = @losses,
                updated_at       = NOW()
            WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("lastEval",    NpgsqlDbType.TimestampTz, lastEvalAt);
        cmd.Parameters.AddWithValue("openCount",   openTradeCount);
        cmd.Parameters.AddWithValue("totalTrades", totalTrades);
        cmd.Parameters.AddWithValue("pnl",         NpgsqlDbType.Numeric, totalPnlUsd);
        cmd.Parameters.AddWithValue("wins",        winCount);
        cmd.Parameters.AddWithValue("losses",      lossCount);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Record that an entry was not placed, and why (used by Bot worker).
    ///
    /// A refused entry is a normal outcome — a short signal on a spot account, an
    /// order under the exchange minimum — so it was logged at Error and left there.
    /// That is exactly the failure shape worth surfacing: the bot reported RUNNING,
    /// every health check passed, and it had refused every entry for hours with the
    /// only evidence buried in `docker compose logs`. The operator's first sign of
    /// trouble was noticing on the exchange that nothing had traded.
    ///
    /// Written to bot_config rather than kept in memory, so it survives the restart
    /// that would otherwise erase it. The counter resets on date change rather than being
    /// cleared by anyone, so a quiet morning cannot hide behind yesterday's total.
    /// </summary>
    public async Task RecordEntryRefusalAsync(
        string reason, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_config
            SET last_refusal_reason = @reason,
                last_refusal_at     = NOW(),
                refusal_count       = CASE
                                          WHEN refusal_count_date = CURRENT_DATE
                                          THEN refusal_count + 1
                                          ELSE 1
                                      END,
                refusal_count_date  = CURRENT_DATE,
                updated_at          = NOW()
            WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        // Truncated: this is meant to be read at a glance, and an exception message with a
        // stack-trace tail would bury the reason itself.
        cmd.Parameters.AddWithValue("reason",
            reason.Length > 300 ? reason[..300] : reason);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The strategy's current verdict, overwritten every cycle.
    ///
    /// Separate from <see cref="RecordEntryRefusalAsync"/> on purpose. That one
    /// counts refusals that mattered — the daily cap, the gate declining a finished
    /// proposal — and an abstention happens on nearly every cycle, so routing them
    /// through the same counter would take refusal_count to roughly 2,880 a day and
    /// make an existing column meaningless.
    ///
    /// This exists because the abstention log is throttled to once per code change
    /// and then once per 120 cycles. That is correct for a log, and it left the
    /// operator blind for 33 minutes while SOL fell 2.7%: the newest line said
    /// z=+0.50 and the actual state had to be reconstructed by hand from
    /// flow_bars_15m. One row, always current, so looking is a query.
    /// </summary>
    public async Task RecordVerdictAsync(
        string code, string detail, double aggregateZ,
        int agreeingVenues, int participatingVenues, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_config
            SET last_verdict_code   = @code,
                last_verdict_detail = @detail,
                last_verdict_z      = @z,
                last_verdict_agree  = @agree,
                last_verdict_venues = @venues,
                last_verdict_at     = NOW()
            WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("code",
            code.Length > 48 ? code[..48] : code);
        // Same reasoning as the refusal reason: it is meant to be read at a glance.
        cmd.Parameters.AddWithValue("detail",
            detail.Length > 400 ? detail[..400] : detail);
        cmd.Parameters.AddWithValue("z",      (decimal)Math.Round(aggregateZ, 4));
        cmd.Parameters.AddWithValue("agree",  (short)agreeingVenues);
        cmd.Parameters.AddWithValue("venues", (short)participatingVenues);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Record what the last sizing decision actually produced (used by Bot worker).
    ///
    /// <see cref="PositionSizer"/> can be re-run to see what sizing would ask for, but not
    /// what survived the venue's lot grid — so the number that was really sent has
    /// to come from the process that sent it.
    /// </summary>
    public async Task RecordSizingNoteAsync(string note, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_config
            SET last_sizing_note = @note, updated_at = NOW()
            WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("note", note.Length > 300 ? note[..300] : note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Write stop command to DB (used by API).</summary>
    public async Task StopBotAsync(CancellationToken ct = default)
    {
        const string sql = "UPDATE bot_config SET enabled = FALSE, updated_at = NOW() WHERE id = 1";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
