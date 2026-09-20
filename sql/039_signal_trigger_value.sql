-- ─────────────────────────────────────────────────────────────────────────────
-- 039: Give signal_outcomes something the gate's neighbour search can rank on
--
-- The defect
-- ----------
-- `SignalOutcomeRepository.FindSimilarAsync` picks the five closest past signals with
--
--     sqrt( ((atr_pct - a)/0.15)^2 + ((|aggregate_ofi| - o)/0.10)^2 + ((stop_pct - s)/0.004)^2 )
--
-- Counted over every signal on record on 2026-09-20:
--
--     strategy          n   distinct atr_pct   distinct stop_pct   distinct |aggregate_ofi|
--     CANDLE_REVERSAL  35                 35                   1                          1
--     XVENUE_FLOW      41                 40                   2                         26
--
-- For CANDLE_REVERSAL — the majority of the trade stream — TWO OF THE THREE TERMS ARE
-- CONSTANTS. The distance collapses to `atr_pct` alone, which is not why that rule fired,
-- and the remaining ties break on `signal_at DESC`. That is how "similar past setups"
-- became recency wearing a similarity label. It is not free: five retrieved cases cost a
-- measured +10.4s on every gate call, against a 75s client timeout and a 120s cycle.
--
-- What this adds
-- --------------
-- `trigger_value` — how hard the rule fired, in that rule's own units:
--
--     CANDLE_REVERSAL   the absolute percent move that crossed the threshold (1.98 = a
--                       1.98% fall or rise)
--     XVENUE_FLOW       the volume ratio of the dominant side (2.44 = 2.44:1)
--
-- **The units differ per strategy and that is deliberate.** Every consumer filters by
-- strategy before comparing — retrieval matches a CandleReversal signal only against other
-- CandleReversal signals — so within any one comparison the unit is constant. The column
-- must never be averaged or ranked across strategies, and the comment on the column says
-- so for whoever writes the next query.
--
-- Why a new column rather than reusing a dead one
-- -----------------------------------------------
-- `aggregate_z`, `venue_votes` and `excluded_venues` are all structurally 0 or NULL since
-- the venue-scoring rule was deleted, so one of them could have carried this. They are not
-- reused because the rows written BEFORE that deletion hold real values in them, and
-- overloading a column changes what history means. This repository has already paid for a
-- column whose meaning shifted underneath its readers.
--
-- Backfill
-- --------
-- Deliberately none. Existing rows keep NULL, and `FindSimilarAsync` treats NULL as "no
-- trigger recorded" and falls back to the old distance for those rows, so retrieval keeps
-- working on day one and improves as new signals accumulate. The values COULD be parsed
-- back out of `gate_reason`/`reason` prose for old rows; that is exactly the
-- recover-a-number-from-a-sentence pattern this repo keeps being bitten by, so it is not
-- done.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE signal_outcomes
    ADD COLUMN IF NOT EXISTS trigger_value NUMERIC(12, 4);

COMMENT ON COLUMN signal_outcomes.trigger_value IS
    'How hard the rule fired, in that rule''s OWN units: percent move for '
    'CANDLE_REVERSAL, volume ratio for XVENUE_FLOW. Comparable only within one strategy '
    '-- always filter by strategy before ranking or averaging on it. NULL on rows written '
    'before sql/039, which retrieval handles by falling back to the older distance.';

CREATE INDEX IF NOT EXISTS ix_signal_outcomes_trigger
    ON signal_outcomes (symbol, strategy, side, trigger_value)
    WHERE trigger_value IS NOT NULL
      AND outcome::text = ANY (ARRAY['WIN', 'LOSS', 'TIMEOUT']);

DO $$
BEGIN
    IF to_regclass('signal_outcomes') IS NULL
       OR NOT EXISTS (SELECT 1 FROM information_schema.columns
                      WHERE table_name = 'signal_outcomes' AND column_name = 'trigger_value')
    THEN
        RAISE EXCEPTION 'sql/039 did not add signal_outcomes.trigger_value';
    END IF;

    RAISE NOTICE 'sql/039: trigger_value present, NULL on % existing row(s)',
        (SELECT count(*) FROM signal_outcomes WHERE trigger_value IS NULL);
END $$;
