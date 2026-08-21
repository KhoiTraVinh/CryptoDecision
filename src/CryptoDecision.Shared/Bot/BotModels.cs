namespace CryptoDecision.Shared.Bot;

// ── Trade record ──────────────────────────────────────────────────────────────

public sealed record BotTrade
{
    public long      Id          { get; init; }
    public string    Symbol      { get; init; } = "";
    public string    Side        { get; init; } = "BUY";
    public string    Strategy    { get; set; }  = "UNKNOWN";
    public decimal   EntryPrice  { get; set;  }
    public decimal?  ExitPrice   { get; set;  }
    public decimal   Quantity    { get; set;  }
    public decimal   NotionalUsd { get; set;  }
    public decimal?  PnlUsd      { get; set;  }
    public decimal?  PnlPct      { get; set;  }
    public string    Status      { get; set;  } = "OPEN";   // OPEN | CLOSED | STOPPED
    public DateTime  OpenedAt    { get; init; }
    public DateTime? ClosedAt    { get; set;  }
    public string?   CloseReason { get; set;  }             // TP | SL | TRAILING_STOP | TIMEOUT | MANUAL

    /// <summary>
    /// High-water mark since trade opened. Updated every eval cycle.
    /// LONG: tracks highest price seen (trailing stop fires when price drops TrailingStopPct from peak).
    /// SHORT: tracks lowest price seen (trailing stop fires when price rises TrailingStopPct from peak).
    /// </summary>
    public decimal?  PeakPrice   { get; set;  }

    // ── Execution provenance ──
    //
    // A paper row and a live row are not the same kind of fact, and once real
    // orders are possible the difference has to travel with the trade rather than
    // being inferred from whatever bot_config happens to say now. PaperMode can be
    // flipped while positions are still open; an exit has to go back to the venue
    // its entry actually filled on, which is what Mode and Exchange are read for.

    /// <summary>PAPER = simulated fill. LIVE = a real order was placed and real funds moved.</summary>
    public string    Mode         { get; init; } = "PAPER";

    /// <summary>Venue the order was placed on, or the price source for a paper fill.</summary>
    public string    Exchange     { get; init; } = "BINANCE";

    /// <summary>Exchange order id of the entry order. Null for paper trades.</summary>
    public string?   EntryOrderId { get; init; }

    /// <summary>Exchange order id of the exit order. Null for paper and still-open trades.</summary>
    public string?   ExitOrderId  { get; set;  }

    /// <summary>
    /// OKX algoId of the OCO take-profit/stop-loss order guarding this position.
    ///
    /// Persisted rather than held in memory because it has to survive the restart
    /// it exists to protect against. A bot-driven exit must cancel this first: a
    /// manual sell leaves the OCO live, and an orphaned OCO waits to sell coins
    /// that are no longer there.
    /// </summary>
    public string?   ExitAlgoId   { get; set;  }

    /// <summary>
    /// Fees the exchange actually charged, in USD — the entry fee while open, entry
    /// plus exit once closed. Null for paper trades, whose fee is modelled inside
    /// the P&amp;L rather than charged.
    /// </summary>
    public decimal?  FeeUsd       { get; set;  }

    /// <summary>
    /// Leverage in force when the position was opened, and the margin mode behind
    /// it. Null for paper and spot trades.
    ///
    /// Recorded because they are not recoverable from anything else on the row, and
    /// without them the P&amp;L series is not comparable across trades: the same
    /// entry, exit and size at different leverage risked different amounts of
    /// capital and sat different distances from liquidation.
    /// </summary>
    public decimal?  Leverage    { get; init; }
    public string?   MarginMode  { get; init; }

    /// <summary>True when this trade committed real funds.</summary>
    public bool IsLive => Mode == "LIVE";
}

// ── Bot configuration ─────────────────────────────────────────────────────────

public sealed class BotOptions
{
    public bool         Enabled                  { get; set; } = false;
    public bool         PaperMode                { get; set; } = true;
    public string       Symbol                   { get; set; } = "BTCUSDT";
    public string       Exchange                 { get; set; } = "BINANCE";
    public List<string> ActiveStrategies         { get; set; } = ["MOMENTUM"];
    public decimal      CapitalUsd               { get; set; } = 100m;
    
    /// <summary>Number of concurrent positions per strategy.</summary>
    public int          MaxOpenTradesPerStrategy { get; set; } = 5;
    
    public decimal PositionPctOfCapital{ get; set; } = 0.10m;   // 10% per trade (1/10th)

    // ── Take profit / stop loss ──
    //
    // These defaults were previously TP 0.3% against SL 5%. Net of the 0.2%
    // round-trip fee that is a 0.1% win against a 5.2% loss: a 98% win rate is
    // needed just to break even, and a single loss undoes 52 wins. The pair below
    // is fee-aware and the right way round — see RiskEngine.Expectancy.
    //
    // TP 2.0% / SL 1.5% nets to +1.8% / -1.7%, a 1.06:1 reward:risk needing a
    // 48.6% win rate to break even. RiskEngine.Validate() re-derives this from
    // whatever is actually configured and refuses to start on impossible setups.

    /// <summary>Take profit. Must clear the round-trip fee by a wide enough margin to be worth taking.</summary>
    public decimal TakeProfitPct  { get; set; } = 0.02m;   // +2.0%
    /// <summary>Stop loss. Kept below take profit so the reward:risk ratio is not inverted.</summary>
    public decimal StopLossPct    { get; set; } = 0.015m;  // -1.5%
    public int     MaxHoldMinutes { get; set; } = 1440;    // 24 hours max

    /// <summary>Cooldown between entries in seconds (DCA pacing).</summary>
    public int     CooldownSeconds{ get; set; } = 120;     // 2 minutes

    public decimal DailyLossLimitPct   { get; set; } = 0.15m;  // -15% of capital/day

    /// <summary>Seconds between evaluation cycles — the granularity of every bot-side exit.</summary>
    public int     EvalIntervalSeconds { get; set; } = 30;

    // ── Trailing stop ──

    /// <summary>Enable trailing stop. When price retraces TrailingStopPct from peak, the trade is closed.</summary>
    public bool    UseTrailingStop     { get; set; } = true;
    /// <summary>How far price can fall from peak before trailing stop fires. Sits inside the 2% target.</summary>
    public decimal TrailingStopPct     { get; set; } = 0.012m;

    // ── Breakeven stop ──

    /// <summary>Enable breakeven stop. After trade gains BreakevenTriggerPct, stop loss moves to entry price (risk-free).</summary>
    public bool    UseBreakevenStop    { get; set; } = true;
    /// <summary>Profit threshold that activates breakeven. Must sit below TakeProfitPct or it never engages.</summary>
    public decimal BreakevenTriggerPct { get; set; } = 0.008m;

    // ── Dynamic TP/SL ──

    /// <summary>Enable dynamic TP/SL that scales with volatility. High vol → wider TP/SL, low vol → tighter.</summary>
    public bool    UseDynamicTpSl      { get; set; } = false;

    // ── AI Integration ──

    /// <summary>Enable AI filter: only enter when AI prediction aligns with trade direction.</summary>
    public bool    UseAiFilter         { get; set; } = false;
    /// <summary>Min AI confidence to allow entry (0.0-1.0). Default 0.50 = moderate confidence needed.</summary>
    public decimal MinAiConfidence     { get; set; } = 0.50m;
    /// <summary>Enable AI-based position sizing: higher confidence = larger position.</summary>
    public bool    UseAiSizing         { get; set; } = false;

    // ── Autonomous agent ──

    /// <summary>
    /// Hand entry decisions to the LLM agent instead of the deterministic strategies.
    ///
    /// The agent only decides *entries*. Stop loss, take profit, trailing and
    /// breakeven exits stay in the deterministic evaluation loop, and every order the
    /// agent proposes is still gated by RiskEngine — it cannot size its own position
    /// or trade past an exposure, drawdown or daily-loss limit.
    ///
    /// Off by default: turning this on gives a language model discretion over when
    /// capital is committed, which is a deliberate decision an operator should make
    /// explicitly rather than inherit.
    /// </summary>
    public bool    UseAiAgent          { get; set; } = false;
}

// ── Runtime status ────────────────────────────────────────────────────────────

public sealed record BotStatus(
    bool      IsRunning,
    bool      PaperMode,
    string    Symbol,
    decimal   CapitalUsd,
    decimal   TotalPnlUsd,
    decimal   TotalPnlPct,
    int       TotalTrades,
    int       WinCount,
    int       LossCount,
    int       OpenTradeCount,
    DateTime? LastEvalAt
);

public sealed record EntryDecision(bool Pass, string Side = "BUY", decimal Confidence = 1.0m, string? Rationale = null);

// ── API DTO ───────────────────────────────────────────────────────────────────

public sealed record BotTradeDto(
    long     Id,
    string   Symbol,
    string   Side,
    string   Strategy,
    decimal  EntryPrice,
    decimal? ExitPrice,
    decimal  Quantity,
    decimal  NotionalUsd,
    decimal? PnlUsd,
    decimal? PnlPct,
    string   Status,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    string?  CloseReason,
    // Defaulted so a caller not yet taught about live trading cannot accidentally
    // present a real trade as a simulated one.
    string   Mode     = "PAPER",
    string   Exchange = "BINANCE"
);
