-- ─────────────────────────────────────────────────────────────────────────────
-- 020: Fixed-fractional risk sizing
--
-- position_pct sized a fraction of capital and then shrank it when volatility was
-- high. That was coherent while the stop was a constant. It stopped being coherent
-- when the stop became a multiple of measured volatility, because both knobs then
-- read volatility and compound in the same direction: a volatile day gives a smaller
-- position AND a wider stop, so risk per trade collapses; a quiet day gives a full
-- position with a tight stop. Nobody chose that schedule.
--
-- Observed on this account: daily volatility 15.9% pinned the sizing scalar at its
-- 0.5 floor and halved every order, while the 15-minute ATR that sets the stop was
-- 1.07%. Two different volatility measures adjusting one decision.
--
-- risk_pct_per_trade replaces it. notional = (capital × risk_pct) / stop_pct, so
-- every trade risks the same money whatever the regime. position_pct is kept for the
-- strategies that produce no stop distance, and is ignored for those that do.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS risk_pct_per_trade NUMERIC(6, 4) NOT NULL DEFAULT 0.0100;

COMMENT ON COLUMN bot_config.risk_pct_per_trade IS
    'Fraction of capital lost if the stop is hit. Used instead of position_pct '
    'whenever the strategy supplies a stop distance. Note Okx:MaxOrderNotionalUsd '
    'still caps the resulting notional, and will bind first on a small account — '
    'which lowers realised risk below this figure rather than raising it.';
