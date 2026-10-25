-- ─────────────────────────────────────────────────────────────────────────────
-- 049: Drop the two columns the LLM exit reviewer wrote
--
-- sql/040 added last_exit_review_at to pace the reviewer, and sql/047 added
-- last_exit_review_note so the reasoning behind an LLM_EXIT survived a deploy --
-- a real problem at the time, because `docker logs` does not survive a container
-- being recreated.
--
-- The reviewer is gone. It closed 30 positions for -$3.74 as LLM_EXIT, which was
-- the worst line in the book; H44 switched it off on 2026-10-16 and the code
-- behind the switch was removed on 2026-10-25. Nothing writes these columns and
-- nothing reads them.
--
-- What is being given up, stated rather than discovered later
-- ----------------------------------------------------------
-- 047's own NOTICE said the reasoning behind every LLM_EXIT before it was
-- already gone. This drops what was collected after. On THIS database that is
-- nothing: it was built fresh on 2026-10-22 and the reviewer never ran against
-- it, so the columns are NULL on every row. The assertion below refuses to run
-- if that is not true somewhere else, because losing a reviewer's stated reasons
-- is exactly the kind of silent deletion this ledger exists to prevent.
--
-- Recovering them means restoring a backup, not re-deriving them. If you are
-- applying this to a database that DID run the reviewer and you want the
-- history, copy the rows out first:
--
--   CREATE TABLE llm_exit_archive AS
--   SELECT id, close_reason, last_exit_review_at, last_exit_review_note
--   FROM bot_trades WHERE last_exit_review_at IS NOT NULL;
-- ─────────────────────────────────────────────────────────────────────────────

DO $$
DECLARE
    stamped BIGINT;
BEGIN
    SELECT count(*) INTO stamped
    FROM bot_trades
    WHERE last_exit_review_at IS NOT NULL
       OR last_exit_review_note IS NOT NULL;

    IF stamped > 0 THEN
        RAISE EXCEPTION
            '049: % trade(s) carry exit-review data. Archive them before dropping '
            'these columns -- see the CREATE TABLE in this file''s header -- then '
            'baseline this migration (MIGRATE_BASELINE=1) or re-run it.', stamped;
    END IF;

    RAISE NOTICE '049: no trade carries exit-review data; dropping both columns.';
END $$;

ALTER TABLE bot_trades
    DROP COLUMN IF EXISTS last_exit_review_at,
    DROP COLUMN IF EXISTS last_exit_review_note;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'bot_trades'
          AND column_name IN ('last_exit_review_at', 'last_exit_review_note')
    ) THEN
        RAISE EXCEPTION '049: a review column survived the drop';
    END IF;

    RAISE NOTICE '049: bot_trades.last_exit_review_at and last_exit_review_note dropped.';
END $$;
