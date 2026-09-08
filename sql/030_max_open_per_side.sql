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
