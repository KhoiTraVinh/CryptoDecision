-- ─────────────────────────────────────────────────────────────────────────────
-- 056: drop bot_config.sl_side_block_minutes — the post-stop side block is removed
--
-- Added by 054 earlier the same day and removed at the operator's decision, 2026-11-14.
-- It never ran: the column was applied but the code using it was not deployed before
-- the box went down, so this drops a setting that never influenced a live order.
--
-- Why it goes
-- -----------
-- It was fitted on 3 blocked trades out of 28, and most of the benefit came from ONE
-- cascade (31 -> 33 -> 34 on 11-13/14). Excluding that episode it was worth about
-- +$0.09. At the 180 minutes the operator chose it caught only the LAST trade of that
-- cascade -- the gap between the first stop and the next entry was 352 minutes -- so
-- it bought roughly half of a result that was already thin.
--
-- The honest summary is that it was a rule inferred from one bad morning. This
-- session produced two of those: a trend filter that reversed sign between a 4h and
-- an 8h lookback, and this. The pre-existing SameSideLossBlockMinutes stays at 60,
-- because that one was measured on 134 trades and held in both sample halves.
--
-- What remains uncovered, stated so it is not rediscovered as a surprise: every gate
-- in this system keys on VOLUME, and a slow decline on ordinary volume engages none
-- of them. The dip rule's own volume cap only lets it trade quiet tape, so a quiet
-- crash is the condition it is designed to buy into.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    DROP COLUMN IF EXISTS sl_side_block_minutes;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'bot_config' AND column_name = 'sl_side_block_minutes') THEN
        RAISE EXCEPTION '056: sl_side_block_minutes survived the drop';
    END IF;

    RAISE NOTICE '056: post-stop side block removed. SameSideLossBlockMinutes (60 min) is now the only side gate.';
END $$;
