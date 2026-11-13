-- ─────────────────────────────────────────────────────────────────────────────
-- 051: bot_config.high_volume_long_max_hold_minutes — the waiver leash, per side
--
-- 046 gave every news-print waiver entry the same 60-minute leash. This splits it:
-- a LONG may be held for this many minutes instead, a SHORT keeps the 60 from 046.
--
-- Operator's rule, 2026-11-13: a dump needs longer to react than a pump, so the side
-- bought into weakness deserves time the side sold into strength does not.
--
-- This is a hypothesis, not a measurement
-- ---------------------------------------
-- H31 measured the waiver entry as ONE population and found nothing worth holding
-- past the first hour. Splitting it by side is a new claim, and the only evidence is
-- four trades replayed on klines_1m from each entry, signed to the trade's own
-- direction:
--
--     SHORT (n 2)   60m -1.83 %   120m -2.43 %   180m -4.77 %
--     LONG  (n 2)   60m -1.78 %   120m -1.79 %   180m +0.26 %
--
-- The long row turns over entirely on ONE trade recovering to +2.15 %. Two trades a
-- side is not a sample, and H31's 73-bucket forward test still says the opposite for
-- the population as a whole. Treat this as the operator's hypothesis under test.
--
-- How to judge it
-- ---------------
-- The same way H31 was judged. Once there are enough HV_TIMEOUT closes of each side,
-- compare them directly -- the close reason already records which cap fired:
--
--     SELECT side, count(*), round(avg(pnl_pct * 100), 3), round(sum(pnl_usd), 4)
--     FROM bot_trades WHERE close_reason = 'HV_TIMEOUT' GROUP BY side;
--
-- If the long side is not clearly better for the extra two hours, set this back to 60
-- and the two sides collapse to one leash again.
--
-- 0 falls back to high_volume_max_hold_minutes, which is the exact revert.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS high_volume_long_max_hold_minutes INT NOT NULL DEFAULT 180;

COMMENT ON COLUMN bot_config.high_volume_long_max_hold_minutes IS
    'Operator, 2026-11-13. Minutes a LONG opened through the $20M waiver may be held, in '
    'place of high_volume_max_hold_minutes. 180. SHORT keeps the 60 from 046. Hypothesis '
    'under test against H31, which measured the waiver entry as one population. 0 falls '
    'back to high_volume_max_hold_minutes.';

DO $$
DECLARE v INT; s INT;
BEGIN
    SELECT high_volume_long_max_hold_minutes, high_volume_max_hold_minutes
      INTO v, s
      FROM bot_config WHERE id = 1;

    IF v IS NULL THEN
        RAISE EXCEPTION '051: high_volume_long_max_hold_minutes is NULL on bot_config row 1';
    END IF;

    RAISE NOTICE '051: waiver leash is now % min LONG / % min SHORT on bot_config row 1.', v, s;
END $$;
