-- ─────────────────────────────────────────────────────────────────────────────
-- 015: Exchange-side protective order id on bot_trades
--
-- Take profit and stop loss used to live only inside the bot's evaluation loop:
-- an `if` over a price polled every 30 seconds. That is protection only while the
-- process is alive, which is the wrong assumption for the one thing a position
-- most needs to survive — the bot crashing, being OOM-killed, or losing the
-- network.
--
-- A live entry now places an OCO algo order on OKX immediately after it fills, so
-- the exchange holds the stop whether or not this process exists. exit_algo_id is
-- the handle for it, and it has to be persisted rather than kept in memory for
-- exactly the same reason the OCO exists: a restart must be able to find the
-- order it left behind. Without it, a bot-driven exit (trailing stop, breakeven,
-- timeout) would sell the position while an orphaned OCO stayed live on the
-- exchange, waiting to sell coins that are no longer there.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS exit_algo_id TEXT;

-- Reconciliation lookup: "did the exchange close this position without us?"
CREATE INDEX IF NOT EXISTS idx_bot_trades_exit_algo_id
    ON bot_trades (exit_algo_id) WHERE exit_algo_id IS NOT NULL;

-- Recovering open positions on startup is a hot path now, and on a table that
-- accumulates every trade ever made it should not be a sequential scan.
CREATE INDEX IF NOT EXISTS idx_bot_trades_open
    ON bot_trades (opened_at) WHERE status = 'OPEN';

COMMENT ON COLUMN bot_trades.exit_algo_id IS
    'OKX algoId of the OCO take-profit/stop-loss order guarding this position. '
    'NULL for paper trades, for closed trades, and for live trades whose '
    'protective order could not be placed — the last case is logged as critical.';
