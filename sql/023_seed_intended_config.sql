-- ─────────────────────────────────────────────────────────────────────────────
-- 023: Seed the configuration this system is actually designed to run
--
-- Why this is a migration and not a runbook step
-- ---------------------------------------------
-- A fresh database seeds bot_config row 1 from the column defaults in 007, and those
-- defaults describe a different system than the one this repo now contains:
--
--   symbol            BTCUSDT   — ingestion only feeds SOLUSDT, so every read is empty
--   take_profit_pct   0.003     — with stop_loss_pct 0.05 this is the pair that needs a
--   stop_loss_pct     0.05        98% win rate to break even. RiskEngine refuses to
--                                 start on it, which is the safe outcome and also a
--                                 confusing one to debug on a new instance.
--   capital_usd       1000      — 16x the account this is sized for
--   use_trailing_stop true      — a 1.5% trailing stop inside SOL's intraday range,
--                                 the mechanism that closed four consecutive live
--                                 entries having moved at most +0.29% in their favour
--   strategy_list     GRID,MOMENTUM — GRID does not exist as an ITradingStrategy
--
-- Leaving that to a manual step after every deploy is leaving it to be forgotten
-- once. The values below are the ones arrived at from measured data — 15-minute median
-- true range 1.07% on SOL, OKX perp maker/taker at 2/5 bps — and they belong with the
-- code that assumes them.
--
-- Runs once per database, recorded in schema_migrations, so it cannot overwrite an
-- operator's later tuning. On a database already configured by hand, baseline it
-- (MIGRATE_BASELINE=1) rather than applying it.
--
-- enabled stays FALSE and paper_mode stays TRUE. A migration must never be the thing
-- that starts trading; arming is a deliberate act by a person.
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE bot_config SET
    enabled                      = FALSE,
    paper_mode                   = TRUE,

    symbol                       = 'SOLUSDT',
    exchange                     = 'OKX',
    active_strategies            = '{XVENUE_FLOW}',

    capital_usd                  = 60,

    -- Sizing comes from risk_pct_per_trade over the strategy's stop distance; see
    -- PositionSizer.ResolveByRisk. position_pct survives only for strategies that
    -- express no stop distance, and is left at a conservative value for those.
    risk_pct_per_trade           = 0.0100,
    position_pct                 = 0.1000,

    -- One position at a time. The signal persists across adjacent buckets, so an
    -- unrestricted book would open near-identical trades minutes apart and report
    -- their correlated outcome as independent evidence.
    max_open_trades_per_strategy = 1,

    -- Bounds fee spend rather than risk. Expected traffic is 2-3 entries a day at a
    -- 12-hour hold; 4 only binds when something is wrong.
    max_entries_per_day          = 4,

    -- 12 hours, matching the horizon over which quarter-hour order-flow imbalance has
    -- documented predictive content. Was 1440.
    max_hold_minutes             = 720,

    -- One bucket. Re-deciding faster than the signal updates just pays fees.
    cooldown_seconds             = 900,
    eval_interval_seconds        = 30,

    -- Fallback only. XVENUE_FLOW computes its own levels from ATR and stores them per
    -- trade; these apply to rows opened without geometry, and are kept fee-aware and
    -- the right way round so RiskEngine's start gate passes.
    take_profit_pct              = 0.0200,
    stop_loss_pct                = 0.0150,

    -- Both off, and not optional.
    --
    -- The breakeven check lives in StrategyEvaluator.EvaluateExit and runs *before* the
    -- strategy, so it fires even though XVENUE_FLOW ignores it. It closed any trade
    -- that reached +0.8% and came back to entry, which after fees is a small loss —
    -- between them these two truncated nearly every winner the bot had.
    use_trailing_stop            = FALSE,
    use_breakeven_stop           = FALSE,
    use_dynamic_tp_sl            = FALSE,

    daily_loss_limit_pct         = 0.1500,

    -- The gate replaces these. They fed a model's opinion into a composite score and
    -- a sizing multiplier, where a wrong answer moved money the wrong way; the gate
    -- can only ever refuse an entry.
    use_ai_filter                = FALSE,
    use_ai_sizing                = FALSE,
    use_ai_agent                 = FALSE,

    require_ai_gate              = TRUE,
    allow_entry_without_gate     = FALSE,

    updated_at                   = now()
WHERE id = 1;
