-- ─────────────────────────────────────────────────────────────────────────────
-- 043: drop what H29 and H17 left behind
--
-- 1. bot_config.suspend_on_high_volume  — the H26 switch
-- -----------------------------------------------------
-- H26 closed a position belonging to any strategy other than the one holding a
-- >=$20M-waiver position, and blocked that strategy's entries until it closed. H29
-- switched XVENUE_FLOW off, so no HIGH_VOLUME position can exist and the rule is
-- unreachable rather than merely disabled. The C# went with it in the same commit.
--
-- Its numbers are kept in HYPOTHESES.md rather than here, because they are the reason
-- not to rebuild it the same way: over its window, positions cut by the rule averaged
-- +0.017R against +0.055R for CANDLE_REVERSAL positions left alone, and structurally it
-- suspended the rule that made money (+0.696R over 14) for the path that lost it
-- (-1.542R over 5). The premise it rested on is NOT withdrawn — a >=$20M bucket really
-- is a 4.2x-volatility state, measured on 63 buckets against 2,966 — only the step from
-- detecting that state to trading it alone.
--
-- 2. btc_probe_1m  — a probe table nothing has read since it was filled
-- ---------------------------------------------------------------------
-- 5,685 rows, 656 kB, and zero references in any .cs, .sh or compose file. It was a
-- one-off probe; the question it answered is long settled and the rows are a snapshot of
-- a market that has moved on. Nothing derives from it.
--
-- What is deliberately NOT dropped
-- --------------------------------
-- `bot_trades_archive` — 54 rows of real trade history at 200 kB. It has no code
-- references either, and that is exactly why it is safe: nothing can break, and the rows
-- are the only copy of what those trades did. Cheap to keep, unrecoverable to drop.
--
-- The XVENUE_FLOW machinery in the C# is also NOT deleted. H29 gives it a written restore
-- condition, and deleting the code would turn that condition from an UPDATE into a
-- rewrite — which is how a reversible decision quietly becomes an irreversible one.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config DROP COLUMN IF EXISTS suspend_on_high_volume;

DROP TABLE IF EXISTS btc_probe_1m;

DO $$
DECLARE
    leftover TEXT;
BEGIN
    SELECT string_agg(x, ', ') INTO leftover FROM (
        SELECT 'bot_config.suspend_on_high_volume' AS x
        WHERE EXISTS (SELECT 1 FROM information_schema.columns
                      WHERE table_name='bot_config' AND column_name='suspend_on_high_volume')
        UNION ALL
        SELECT 'btc_probe_1m'
        WHERE EXISTS (SELECT 1 FROM information_schema.tables
                      WHERE table_schema='public' AND table_name='btc_probe_1m')
    ) t;

    IF leftover IS NOT NULL THEN
        RAISE EXCEPTION '043: still present after the drop: %', leftover;
    END IF;

    -- The keeper, asserted rather than assumed. A migration that drops things should say
    -- out loud that it did not drop the one it promised to leave.
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables
                   WHERE table_schema='public' AND table_name='bot_trades_archive') THEN
        RAISE EXCEPTION '043: bot_trades_archive is gone and this migration was meant to keep it';
    END IF;

    RAISE NOTICE '043: suspend_on_high_volume and btc_probe_1m dropped; bot_trades_archive kept.';
END $$;
