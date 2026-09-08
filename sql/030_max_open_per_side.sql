-- Concurrent positions allowed on the same side.
--
-- 0 keeps the previous behaviour: only max_open_trades_per_strategy applies and two
-- LONGs at once are permitted. Set to 1 alongside max_open_trades_per_strategy = 2
-- for "two positions, never two the same way".
--
-- Why: on the 15-minute grid SOL reverses inside a single hold often enough that
-- being full on one side was costing the other side entirely. Holding a LONG through
-- a turn meant the SHORT the next bucket called for was never taken -- a missed trade
-- rather than a risk avoided.
--
-- Requires the venue to keep the sides separate. OKX does that only in
-- long_short_mode; in net mode a SHORT against an open LONG reduces or closes it
-- rather than opening a second position. This account was verified as long_short_mode
-- (account level 2) on 2026-09-08.

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS max_open_per_side INTEGER NOT NULL DEFAULT 0;

COMMENT ON COLUMN bot_config.max_open_per_side IS
    'Concurrent positions per side (LONG/SHORT). 0 disables the constraint. '
    'Pair with max_open_trades_per_strategy: 2 total with 1 per side means the bot '
    'may hold one LONG and one SHORT but never two of either.';

-- Consecutive losses before the bot disables itself. Was hardcoded at 5 in the call
-- to RiskEngine.CheckCircuitBreakers, which is wrong once the geometry is a setting:
-- the right number depends on the win rate that geometry implies. At a 39% win rate
-- five in a row occurs in 8.6% of any five-trade window -- near certain across forty
-- trades -- so a breaker tuned for a 50% strategy halts a 39% one behaving exactly as
-- designed.

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS max_consecutive_losses INTEGER NOT NULL DEFAULT 5;

COMMENT ON COLUMN bot_config.max_consecutive_losses IS
    'Losing trades in a row on one strategy before the bot writes enabled = false. '
    'Raise only alongside a measured reason: it is the last thing between a signal '
    'that has stopped working and an account that finds out slowly.';
