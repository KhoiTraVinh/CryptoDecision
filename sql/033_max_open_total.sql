-- A ceiling on concurrent positions across ALL strategies.
--
-- The position limits that existed before this one were all per-strategy:
--
--   max_open_trades_per_strategy  2   positions one strategy may hold
--   max_open_per_side             1   of those, on the same side -- so "two at once,
--                                     never two the same way"
--   max_open_high_volume          1   from the FlowRatio high-volume waiver
--
-- Every one of them is scoped by `strategy`, which was complete while one strategy ran
-- and stopped being complete the moment a second was registered. Nothing bounded the
-- SUM. Two strategies at 2 each is 4; a third takes it to 6, and the only thing that
-- would have noticed is RiskEngine's exposure warning, which is a log line at startup
-- and not a limit.
--
-- 5 is the operator's number. Note that it does not bind at two strategies -- the
-- per-strategy caps already hold the total to 4 -- so this is a ceiling for the third
-- strategy onward rather than a constraint on today's configuration. That is the right
-- time to add it: a cap introduced after the limit it protects has already been passed
-- is a cap that has already failed once.
--
-- 0 disables it.
--
-- What it does NOT do: it does not reserve slots per strategy. Whichever strategy signals
-- first takes the slot, so a rule that fires 7.6 times a day will crowd out one that
-- fires twice. If CANDLE_REVERSAL is enabled, expect it to hold most of these five most
-- of the time, and read H9 and H11 with `WHERE strategy = 'XVENUE_FLOW'`.

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS max_open_total INTEGER NOT NULL DEFAULT 5;

COMMENT ON COLUMN bot_config.max_open_total IS
    'Concurrent open positions across every strategy, for the configured symbol. 0 '
    'disables. Sits above max_open_trades_per_strategy, which is per-strategy and '
    'therefore cannot bound the sum once more than one strategy runs. Slots are first '
    'come first served: a high-frequency strategy will crowd out a low-frequency one.';
