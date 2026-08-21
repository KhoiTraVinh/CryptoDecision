using Npgsql;
using NpgsqlTypes;

namespace CryptoDecision.Shared.Bot;

/// <summary>
/// Reads/writes the singleton bot_config row that bridges API ↔ Bot Worker.
/// API writes commands (start/stop/config), Bot Worker polls and executes.
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
                   daily_loss_limit_pct, eval_interval_seconds, use_trailing_stop,
                   trailing_stop_pct,
                   COALESCE(use_breakeven_stop, TRUE) AS use_breakeven_stop,
                   COALESCE(breakeven_trigger_pct, 0.005) AS breakeven_trigger_pct,
                   COALESCE(use_dynamic_tp_sl, FALSE) AS use_dynamic_tp_sl,
                   COALESCE(use_ai_filter, FALSE) AS use_ai_filter,
                   COALESCE(min_ai_confidence, 0.500) AS min_ai_confidence,
                   COALESCE(use_ai_sizing, FALSE) AS use_ai_sizing,
                   COALESCE(use_ai_agent,  FALSE) AS use_ai_agent
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
            UseTrailingStop          = r.GetBoolean(r.GetOrdinal("use_trailing_stop")),
            TrailingStopPct          = r.GetDecimal(r.GetOrdinal("trailing_stop_pct")),
            UseBreakevenStop         = r.GetBoolean(r.GetOrdinal("use_breakeven_stop")),
            BreakevenTriggerPct      = r.GetDecimal(r.GetOrdinal("breakeven_trigger_pct")),
            UseDynamicTpSl           = r.GetBoolean(r.GetOrdinal("use_dynamic_tp_sl")),
            UseAiFilter              = r.GetBoolean(r.GetOrdinal("use_ai_filter")),
            MinAiConfidence          = r.GetDecimal(r.GetOrdinal("min_ai_confidence")),
            UseAiSizing              = r.GetBoolean(r.GetOrdinal("use_ai_sizing")),
            UseAiAgent               = r.GetBoolean(r.GetOrdinal("use_ai_agent")),
        };
    }

    /// <summary>Update heartbeat + runtime stats so API/Dashboard can read bot status from DB (used by Bot worker).</summary>
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

    /// <summary>Write start command + full config to DB for the Bot Worker to pick up (used by API).</summary>
    public async Task StartBotAsync(BotOptions opts, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bot_config
            SET enabled                      = TRUE,
                paper_mode                   = @paperMode,
                symbol                       = @symbol,
                exchange                     = @exchange,
                active_strategies            = @strategies,
                capital_usd                  = @capital,
                max_open_trades_per_strategy = @maxTrades,
                position_pct                 = @posPct,
                take_profit_pct              = @tp,
                stop_loss_pct                = @sl,
                cooldown_seconds             = @cooldown,
                max_hold_minutes             = @maxHold,
                daily_loss_limit_pct         = @dailyLoss,
                eval_interval_seconds        = @evalInterval,
                use_trailing_stop            = @trailing,
                trailing_stop_pct            = @trailingPct,
                use_breakeven_stop           = @breakeven,
                breakeven_trigger_pct        = @breakevenPct,
                use_dynamic_tp_sl            = @dynamicTpSl,
                use_ai_filter                = @aiFilter,
                min_ai_confidence            = @aiConf,
                use_ai_sizing                = @aiSizing,
                use_ai_agent                 = @aiAgent,
                updated_at                   = NOW()
            WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("paperMode",    opts.PaperMode);
        cmd.Parameters.AddWithValue("symbol",       opts.Symbol);
        cmd.Parameters.AddWithValue("exchange",     opts.Exchange);
        cmd.Parameters.AddWithValue("strategies",   opts.ActiveStrategies.ToArray());
        cmd.Parameters.AddWithValue("capital",      NpgsqlDbType.Numeric, opts.CapitalUsd);
        cmd.Parameters.AddWithValue("maxTrades",    opts.MaxOpenTradesPerStrategy);
        cmd.Parameters.AddWithValue("posPct",       NpgsqlDbType.Numeric, opts.PositionPctOfCapital);
        cmd.Parameters.AddWithValue("tp",           NpgsqlDbType.Numeric, opts.TakeProfitPct);
        cmd.Parameters.AddWithValue("sl",           NpgsqlDbType.Numeric, opts.StopLossPct);
        cmd.Parameters.AddWithValue("cooldown",     opts.CooldownSeconds);
        cmd.Parameters.AddWithValue("maxHold",      opts.MaxHoldMinutes);
        cmd.Parameters.AddWithValue("dailyLoss",    NpgsqlDbType.Numeric, opts.DailyLossLimitPct);
        cmd.Parameters.AddWithValue("evalInterval", opts.EvalIntervalSeconds);
        cmd.Parameters.AddWithValue("trailing",     opts.UseTrailingStop);
        cmd.Parameters.AddWithValue("trailingPct",  NpgsqlDbType.Numeric, opts.TrailingStopPct);
        cmd.Parameters.AddWithValue("breakeven",    opts.UseBreakevenStop);
        cmd.Parameters.AddWithValue("breakevenPct", NpgsqlDbType.Numeric, opts.BreakevenTriggerPct);
        cmd.Parameters.AddWithValue("dynamicTpSl",  opts.UseDynamicTpSl);
        cmd.Parameters.AddWithValue("aiFilter",     opts.UseAiFilter);
        cmd.Parameters.AddWithValue("aiConf",       NpgsqlDbType.Numeric, opts.MinAiConfidence);
        cmd.Parameters.AddWithValue("aiSizing",     opts.UseAiSizing);
        cmd.Parameters.AddWithValue("aiAgent",      opts.UseAiAgent);

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

    /// <summary>Read current bot status from DB (written by Bot Worker heartbeat) (used by API).</summary>
    public async Task<BotConfigStatus> GetStatusAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT enabled, paper_mode, symbol, capital_usd, 
                   last_heartbeat, last_eval_at, open_trade_count,
                   total_trades, total_pnl_usd, win_count, loss_count,
                   active_strategies, max_open_trades_per_strategy,
                   position_pct, cooldown_seconds, eval_interval_seconds
            FROM bot_config WHERE id = 1
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = new NpgsqlCommand(sql, conn);
        await using var r    = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return new BotConfigStatus();

        return new BotConfigStatus
        {
            Enabled              = r.GetBoolean(0),
            PaperMode            = r.GetBoolean(1),
            Symbol               = r.GetString(2),
            CapitalUsd           = r.GetDecimal(3),
            LastHeartbeat        = r.IsDBNull(4) ? null : r.GetDateTime(4),
            LastEvalAt           = r.IsDBNull(5) ? null : r.GetDateTime(5),
            OpenTradeCount       = r.GetInt32(6),
            TotalTrades          = r.GetInt32(7),
            TotalPnlUsd          = r.GetDecimal(8),
            WinCount             = r.GetInt32(9),
            LossCount            = r.GetInt32(10),
            ActiveStrategies     = ((string[])r.GetValue(11)).ToList(),
            MaxOpenTradesPerStrategy = r.GetInt32(12),
            PositionPctOfCapital = r.GetDecimal(13),
            CooldownSeconds      = r.GetInt32(14),
            EvalIntervalSeconds  = r.GetInt32(15),
        };
    }
}

/// <summary>Read-only status from bot_config (populated by Bot Worker heartbeat).</summary>
public sealed class BotConfigStatus
{
    public bool         Enabled              { get; init; }
    public bool         PaperMode            { get; init; } = true;
    public string       Symbol               { get; init; } = "BTCUSDT";
    public decimal      CapitalUsd           { get; init; } = 100m;
    public DateTime?    LastHeartbeat        { get; init; }
    public DateTime?    LastEvalAt           { get; init; }
    public int          OpenTradeCount       { get; init; }
    public int          TotalTrades          { get; init; }
    public decimal      TotalPnlUsd          { get; init; }
    public int          WinCount             { get; init; }
    public int          LossCount            { get; init; }
    public List<string> ActiveStrategies     { get; init; } = ["MOMENTUM"];
    public int          MaxOpenTradesPerStrategy { get; init; } = 5;
    public decimal      PositionPctOfCapital { get; init; } = 0.10m;
    public int          CooldownSeconds      { get; init; } = 120;
    public int          EvalIntervalSeconds  { get; init; } = 30;

    /// <summary>
    /// Whether the bot worker is still alive, judged from its last heartbeat.
    ///
    /// The window has to cover a whole evaluation cycle, not a fixed minute. With the
    /// AI agent enabled a single cycle is the eval interval plus three sequential LLM
    /// tool calls, which on CPU runs 60-90s — so a flat 60s threshold reported a
    /// perfectly healthy bot as STOPPED between heartbeats, and the dashboard flipped
    /// back and forth.
    ///
    /// Four intervals with a 180s floor covers an agent cycle comfortably while still
    /// noticing a genuinely dead worker within a few minutes.
    /// </summary>
    public bool IsWorkerAlive
    {
        get
        {
            if (!LastHeartbeat.HasValue) return false;

            var window = Math.Max(180, EvalIntervalSeconds * 4);
            return (DateTime.UtcNow - LastHeartbeat.Value).TotalSeconds < window;
        }
    }
}
