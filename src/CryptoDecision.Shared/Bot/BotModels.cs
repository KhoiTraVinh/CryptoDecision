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

    // ── Exit geometry, fixed at entry ──
    //
    // Absolute prices rather than percentages, and stored on the row rather than
    // re-derived from configuration every cycle. Two reasons, both learned the hard
    // way:
    //
    //   • Once the stop is scaled to measured volatility it is a fact about the
    //     moment of entry. Recomputing it later moves the stop under the position it
    //     exists to protect, because the ATR has moved since.
    //   • Percentages read live from bot_config made a config edit retroactive. A
    //     widened stop_loss_pct silently moved the stop on positions already open,
    //     which is the one thing a stop must never do.
    //
    // Null means this row predates volatility-scaled exits, and the configured
    // percentages apply — which is what every trade opened before it needs.

    /// <summary>Price at which this position closes for a loss. Null = use config.</summary>
    public decimal? StopPrice     { get; set; }

    /// <summary>Price at which this position closes for a profit. Null = use config.</summary>
    public decimal? TargetPrice   { get; set; }

    /// <summary>
    /// ATR as a percent of price when the geometry was set.
    ///
    /// Recorded because without it a losing trade cannot be attributed: a stop that
    /// was too tight and a market that moved further than usual look identical on the
    /// row, and telling them apart is the entire reason the stop stopped being a
    /// constant.
    /// </summary>
    public decimal? AtrPctAtEntry { get; init; }

    /// <summary>
    /// How this entry got past the gate: APPROVED, APPROVED_DEGRADED, or NOT_GATED.
    ///
    /// Only ever set on entries that happened. A refused candidate produces no row —
    /// the refusal trail on bot_config is where those live.
    /// </summary>
    public string? GateVerdict { get; set; }

    /// <summary>What the gate said, in its own words.</summary>
    public string? GateReason  { get; set; }

    /// <summary>True when this trade committed real funds.</summary>
    public bool IsLive => Mode == "LIVE";

    /// <summary>
    /// Whether this position carries volatility-scaled exit levels, as opposed to
    /// falling back to the configured percentages.
    /// </summary>
    public bool HasGeometry => StopPrice is > 0m && TargetPrice is > 0m;
}

// ── Bot configuration ─────────────────────────────────────────────────────────

public sealed class BotOptions
{
    public bool         Enabled                  { get; set; } = false;
    public bool         PaperMode                { get; set; } = true;
    public string       Symbol                   { get; set; } = "BTCUSDT";
    public string       Exchange                 { get; set; } = "BINANCE";
    public List<string> ActiveStrategies         { get; set; } = ["XVENUE_FLOW"];
    public decimal      CapitalUsd               { get; set; } = 100m;
    
    /// <summary>Number of concurrent positions per strategy.</summary>
    public int          MaxOpenTradesPerStrategy { get; set; } = 5;
    
    public decimal PositionPctOfCapital{ get; set; } = 0.10m;   // 10% per trade (1/10th)

    /// <summary>
    /// Fraction of capital to lose if the stop is hit — the sizing rule used whenever
    /// the strategy supplies a stop distance.
    ///
    /// Replaces PositionPctOfCapital for those strategies rather than joining it.
    /// Sizing a fraction of capital and then shrinking it for volatility made sense
    /// while the stop was a constant; once the stop is itself a multiple of measured
    /// volatility, the two compound and risk per trade wanders with the regime instead
    /// of staying put. See PositionSizer.ResolveByRisk.
    ///
    /// Okx:MaxOrderNotionalUsd still caps the notional this produces and on a small
    /// account will bind first — which lowers the realised risk below this number,
    /// never raises it.
    /// </summary>
    public decimal RiskPctPerTrade    { get; set; } = 0.01m;    // 1% of capital at risk

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

    // ── Entry gate ──

    /// <summary>
    /// Require the AI gate to approve before any entry is placed.
    ///
    /// Defaults to true, unlike every other AI switch above, and the asymmetry is the
    /// reason. The others add the model's opinion to a decision that happens anyway,
    /// so off is the conservative default. This one can only ever *prevent* an entry,
    /// so on is the conservative default — and it is the switch that makes the
    /// discipline real: no position is opened that the gate did not approve, while
    /// sizing, stops, exits and circuit breakers stay entirely deterministic and out
    /// of the model's reach.
    /// </summary>
    public bool    RequireAiGate          { get; set; } = true;

    /// <summary>
    /// Whether an unreachable gate falls back to the deterministic signal alone.
    ///
    /// False by default: a gate that cannot be reached stops entries rather than
    /// silently reverting to ungated trading. The alternative failure mode is a
    /// deployment where the gate has been dead for a week and nothing looks any
    /// different, which is the exact shape of every expensive bug in this codebase so
    /// far.
    /// </summary>
    public bool    AllowEntryWithoutGate  { get; set; } = false;

    /// <summary>
    /// Hard ceiling on entries opened per UTC day, for this symbol and execution mode.
    /// Zero disables the cap.
    ///
    /// This bounds cost rather than risk, and it is the guard that was missing. The
    /// daily loss limit caps how much a losing day can lose, and the concurrency limit
    /// caps how much is at risk at once, but nothing capped how many round trips the
    /// bot could pay for. Over 2026-08-22 it opened ten positions in under seven
    /// hours, four of them riding a single 6% move — for a signal whose horizon is
    /// hours, that is not ten decisions, it is a handful of decisions billed ten times.
    ///
    /// Six is deliberately generous against an expected two or three, so it only binds
    /// when something is wrong: a signal firing every bucket, a stop far too tight, or
    /// a cooldown that is not doing its job.
    /// </summary>
    public int     MaxEntriesPerDay       { get; set; } = 6;
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
    DateTime? LastEvalAt,

    // Everything below defaults, so the three call sites that build a BotStatus
    // keep compiling and each fills in only what it can actually see: the API
    // reads the persisted refusal trail, the bot's own in-process status cannot.
    //
    // These exist because "is it running?" and "is it doing anything?" turned out
    // to be different questions. A bot refusing every entry answered yes to the
    // first and nothing on screen answered the second.
    string?   LastRefusalReason = null,
    DateTime? LastRefusalAt     = null,
    int       RefusalsToday     = 0,

    // What sizing is asking for right now, re-derived from the same PositionSizer
    // the bot uses rather than reimplemented — one copy of the arithmetic.
    decimal?  Volatility        = null,
    double?   VolatilityScalar  = null,
    decimal?  SizingNotionalUsd = null,

    // What the last real attempt produced, which only the bot can know: the venue's
    // lot grid is applied there, not here.
    string?   LastSizingNote    = null,

    // The scorer's verdict on the last cycle. This replaced a panel that rendered
    // prediction_table, whose writer has been deleted — so the dashboard was
    // showing an empty row from a service that no longer exists while the number
    // that actually decides entries was not on screen at all.
    string?   LastVerdictCode   = null,
    string?   LastVerdictDetail = null,
    decimal?  LastVerdictZ      = null,
    int?      LastVerdictAgree  = null,
    int?      LastVerdictVenues = null,
    DateTime? LastVerdictAt     = null
);

/// <param name="Composite">
/// The strategy's headline score at the moment of the decision, as a number rather
/// than buried in the Rationale text.
///
/// It is separate because the question it answers is a SQL question: after four
/// losses in a row, "were these entries taken close to the threshold?" needed a
/// numeric column and there wasn't one. The rationale string carried the score, the
/// container log carried the rest, and the container log died with the container —
/// so the four winning trades that morning could not be compared with the four
/// losers on the one dimension that mattered. Nullable because not every strategy
/// reduces its decision to a single score.
/// </param>
/// <param name="Geometry">
/// Where the stop and target go, if this strategy scales them to measured
/// volatility. Null means the configured percentages apply.
///
/// Carried on the decision rather than recomputed at the point of order placement
/// because it was derived from the volatility reading that justified this entry, and
/// a second reading a moment later is a different number. The pair has to travel
/// together or the position ends up sized against one stop and protected by another.
/// </param>
/// <param name="Flow">
/// The cross-venue flow verdict behind this decision, when there was one. Passed
/// through so the entry gate can be shown the actual per-venue evidence rather than
/// a summary of it — the failure worth catching is a headline consensus resting on
/// one venue while the others were excluded, and that is invisible in an aggregate.
/// </param>
public sealed record EntryDecision(
    bool     Pass,
    string   Side       = "BUY",
    decimal  Confidence = 1.0m,
    string?  Rationale  = null,
    decimal? Composite  = null,
    CryptoDecision.Shared.Signals.StopGeometry? Geometry = null,
    CryptoDecision.Shared.Signals.FlowVerdict?  Flow     = null);

/// <summary>
/// How long the bot may go without starting an evaluation before it is stalled
/// rather than merely between cycles.
///
/// One definition, because there are now two callers with different views of the
/// same fact — the API judging a heartbeat column, and the bot's own health check
/// judging its in-process clock — and two copies of this arithmetic would drift
/// into disagreeing about whether the bot is alive.
///
/// The window has to cover a whole cycle, not a fixed minute. With the AI agent
/// enabled one cycle is the eval interval plus three sequential LLM tool calls,
/// which on CPU runs 60-90s, so a flat 60s threshold reported a perfectly healthy
/// bot as dead between heartbeats. Four intervals with a 180s floor covers that
/// while still noticing a genuinely stopped loop within a few minutes.
/// </summary>
public static class BotLiveness
{
    public static TimeSpan StaleAfter(int evalIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(180, evalIntervalSeconds * 4));
}

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
