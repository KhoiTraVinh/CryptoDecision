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
}

// ── Bot configuration ─────────────────────────────────────────────────────────

public sealed class BotOptions
{
    public bool         Enabled                  { get; set; } = false;
    public bool         PaperMode                { get; set; } = true;
    public string       Symbol                   { get; set; } = "BTCUSDT";
    public string       Exchange                 { get; set; } = "BINANCE";
    public List<string> ActiveStrategies         { get; set; } = ["GRID", "MOMENTUM"];
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
    public int     EvalIntervalSeconds { get; set; } = 30;

    // ── Grid fields ──
    public decimal GridStepPct         { get; set; } = 0.005m; // 0.5% default step

    // ── Momentum fields ──
    public decimal MinConfidence       { get; set; } = 0m;
    public decimal MinMomentumBuyRatio { get; set; } = 0.65m;  // Trigger if 5-min buy ratio > 65%
    public decimal MinBuyRatio1h       { get; set; } = 0.55m;  // Global trend > 55%
    public int     MinWhaleBuys1h      { get; set; } = 0;

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
    string?  CloseReason
);
