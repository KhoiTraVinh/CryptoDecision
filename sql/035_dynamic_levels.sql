-- Make the dynamic stop and target VISIBLE, without making them authoritative.
--
-- Why this exists
-- ---------------
-- `use_dynamic_tp_sl` is TRUE and has been throughout. It scales both barriers by
-- 1 + 10 x the trade's own favourable excursion, capped at 2, recomputed every cycle
-- from the stored levels. Because the excursion grows as price advances, the TARGET
-- RETREATS: the two only meet where x = t(1 + 10t)⁻¹ ... precisely, where
--
--     x = t / (1 - 10t)
--
-- so a stored 4.00% target is a 6.667% one in practice.
--
-- Nothing recorded that. The levels were never written back, the log line announcing
-- them sat at Debug under an Information minimum, and `bot_trades.target_price` kept
-- showing the stored number. The result, on 2026-09-18: trade 93 peaked at 106.12
-- against a stored target of 105.06 and did not close, because the level it was really
-- compared against was 107.75. From outside that is indistinguishable from a broken
-- take-profit, and it was reported as one.
--
-- Nine days, 35 closed trades, and NOT ONE exited on TP -- 28 on the OFI reversal,
-- 5 on the stop, 2 on the timeout. The mechanism was deciding every exit and leaving
-- no trace.
--
-- Why these are NEW columns and not an update to target_price
-- ----------------------------------------------------------
-- Because the widening reads its own input:
--
--     tgtDist = |target_price - entry_price| * scale
--
-- Writing the scaled value into the column that expression reads compounds it once per
-- cycle: d·s, then d·s², then d·s³, every 30 seconds. At s ≈ 1.4 the barrier is past
-- +116% within five minutes -- and the stop widens identically, so the position ends up
-- with no stop at all. `target_price` and `stop_price` must stay as the fixed anchor the
-- arithmetic is applied TO, and switching the feature off has to restore them exactly.
--
-- These two columns are write-only from the bot's point of view: set every cycle, never
-- read back into any decision. They exist so an operator can answer "where is the
-- barrier right now" with a SELECT instead of by re-deriving the formula.
--
-- NULL means the widening is not currently moving anything -- either the feature is off,
-- or the trade has no favourable excursion yet. NULL is not "same as stored"; it is
-- "not applicable", and the distinction matters when reading a closed trade back.

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS dynamic_stop_price   NUMERIC(20, 8),
    ADD COLUMN IF NOT EXISTS dynamic_target_price NUMERIC(20, 8);

COMMENT ON COLUMN bot_trades.dynamic_stop_price IS
    'Stop the dynamic widening was applying at the last evaluation, or NULL when it was '
    'moving nothing. Observational only -- never read back into the widening arithmetic, '
    'which reads stop_price. See sql/035.';

COMMENT ON COLUMN bot_trades.dynamic_target_price IS
    'Target the dynamic widening was applying at the last evaluation, or NULL when it was '
    'moving nothing. This is the level a take-profit is actually compared against; '
    'target_price is the anchor it is derived from, not the trigger. See sql/035.';
