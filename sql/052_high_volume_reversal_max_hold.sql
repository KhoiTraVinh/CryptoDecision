-- ─────────────────────────────────────────────────────────────────────────────
-- 052: the waiver's longer leash belongs to the REVERSAL leg, not to the LONG side
--
-- Replaces 051, which was wrong and lived for one day.
--
-- What 051 got wrong
-- ------------------
-- 051 keyed the longer leash on side: LONG 180, SHORT 60. The operator's rule is about
-- SEQUENCE, not direction -- the waiver enters with the heavier side, and the leg that
-- deserves the longer leash is the one opened afterwards on the OPPOSITE side, whichever
-- direction that happens to be. 051 is right only when the heavy side was the sell side
-- and inverts the intent when it was the buy side:
--
--     heavy SELL -> first SHORT (60), reversal LONG  (180)   051 agrees
--     heavy BUY  -> first LONG  (180), reversal SHORT (60)   051 is backwards
--
-- So the leash is now carried by bot_trades.entry_path = 'HIGH_VOLUME_REVERSAL', set at
-- entry when the side differs from the previous waiver trade, and this column holds the
-- minutes for it. See EntryPaths.HighVolumeReversal.
--
-- NOT conditioned on the previous trade having taken its target. The operator was asked
-- and chose every reversal: only one waiver position in 22 has ever exited at target, so
-- a post-TP-only rule would almost never fire. The target LOCK in TradingBotService still
-- turns on tookTarget -- same row, different rule.
--
-- Still not measured
-- ------------------
-- H31 measured the waiver entry as ONE population and found nothing worth holding past
-- the first hour. The four-trade replay quoted in 051 was split by SIDE, so it does not
-- even speak to the split this column makes. There is no measurement behind this one.
--
--     SELECT entry_path, count(*), round(avg(pnl_pct * 100), 3), round(sum(pnl_usd), 4)
--     FROM bot_trades WHERE close_reason = 'HV_TIMEOUT' GROUP BY entry_path;
--
-- Set to 0 if the reversal leg is not clearly better for the extra two hours; that falls
-- back to high_volume_max_hold_minutes and the two legs collapse to H31's one leash.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS high_volume_reversal_max_hold_minutes INT NOT NULL DEFAULT 180;

COMMENT ON COLUMN bot_config.high_volume_reversal_max_hold_minutes IS
    'Operator, 2026-11-13. Minutes a waiver entry with entry_path = HIGH_VOLUME_REVERSAL '
    'may be held, in place of high_volume_max_hold_minutes. 180. The reversal leg is the '
    'waiver entry opened opposite to the waiver trade before it, any close reason. Not '
    'measured -- H31 measured the waiver entry as one population. 0 falls back.';

-- 051's column never drove a live decision for more than a few hours and its name is now
-- actively misleading. Dropped rather than left at 0, for the reason 049 and 050 give:
-- a column nothing reads is a question someone will waste an afternoon on.
ALTER TABLE bot_config
    DROP COLUMN IF EXISTS high_volume_long_max_hold_minutes;

DO $$
DECLARE r INT; s INT;
BEGIN
    SELECT high_volume_reversal_max_hold_minutes, high_volume_max_hold_minutes
      INTO r, s
      FROM bot_config WHERE id = 1;

    IF r IS NULL THEN
        RAISE EXCEPTION '052: high_volume_reversal_max_hold_minutes is NULL on bot_config row 1';
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'bot_config'
                 AND column_name = 'high_volume_long_max_hold_minutes') THEN
        RAISE EXCEPTION '052: high_volume_long_max_hold_minutes survived the drop';
    END IF;

    RAISE NOTICE '052: waiver leash is % min on the reversal leg, % min otherwise.', r, s;
END $$;
