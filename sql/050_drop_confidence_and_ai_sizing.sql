-- ─────────────────────────────────────────────────────────────────────────────
-- 050: Drop the confidence scalar and the flag that gated it
--
-- Three columns, one dead mechanism. PositionSizer multiplied a position by
-- 0.5 + confidence, clamped to [0.5, 1.5], when bot_config.use_ai_sizing was
-- true. Both inputs were already dead:
--
--   * use_ai_sizing has been FALSE on every row this repository has seen, and
--     023 set it false explicitly on a fresh database.
--   * confidence was |AggregateZ| / (EnterZ * 2). The venue-scoring rule that
--     produced AggregateZ was deleted on 2026-09-11, so the numerator has been
--     structurally 0 ever since. The strategy stopped pretending on 2026-10-06
--     and wrote `const decimal confidence = 0m` with a comment titled
--     "Confidence: there isn't one".
--
-- So entry_confidence and signal_outcomes.confidence record a constant, and the
-- scalar they feed has been 1.0 on every order ever placed. The code went on
-- 2026-10-25; this removes the columns behind it.
--
-- Despite the name, this is NOT part of the LLM removal in 049. No model ever
-- produced this number -- it was arithmetic over venue z-scores. The "ai" in
-- use_ai_sizing was a name, not a mechanism, which is most of why it survived
-- three cleanups that were looking for models.
--
-- entry_confidence is also dropped from DatabaseInitializer in the same commit.
-- That matters for ordering: db-migrate waits on processor, so the initializer
-- runs FIRST. Leaving the ADD COLUMN there would have this migration drop a
-- column that the next startup silently recreates, with the ledger saying the
-- drop already happened.
-- ─────────────────────────────────────────────────────────────────────────────

DO $$
DECLARE
    armed    BIGINT;
    scored   BIGINT;
BEGIN
    SELECT count(*) INTO armed FROM bot_config WHERE use_ai_sizing IS TRUE;
    IF armed > 0 THEN
        RAISE EXCEPTION
            '050: use_ai_sizing is TRUE on % row(s). Nothing reads it any more, so '
            'dropping it changes no behaviour -- but it was switched on deliberately '
            'by someone, and that intent should be understood before it is erased.', armed;
    END IF;

    SELECT count(*) INTO scored
    FROM bot_trades
    WHERE entry_confidence IS NOT NULL AND entry_confidence <> 0;
    IF scored > 0 THEN
        RAISE EXCEPTION
            '050: % trade(s) carry a non-zero entry_confidence. The column was '
            'believed to be a constant 0; it is not, so something computed a real '
            'value and this drop would destroy it. Archive before re-running.', scored;
    END IF;

    RAISE NOTICE '050: use_ai_sizing false everywhere, entry_confidence constant; dropping.';
END $$;

ALTER TABLE bot_config      DROP COLUMN IF EXISTS use_ai_sizing;
ALTER TABLE bot_trades      DROP COLUMN IF EXISTS entry_confidence;
ALTER TABLE signal_outcomes DROP COLUMN IF EXISTS confidence;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE (table_name = 'bot_config'      AND column_name = 'use_ai_sizing')
           OR (table_name = 'bot_trades'      AND column_name = 'entry_confidence')
           OR (table_name = 'signal_outcomes' AND column_name = 'confidence')
    ) THEN
        RAISE EXCEPTION '050: a confidence column survived the drop';
    END IF;

    RAISE NOTICE '050: use_ai_sizing, entry_confidence and signal_outcomes.confidence dropped.';
END $$;
