-- ─────────────────────────────────────────────────────────────────────────────
-- 047: bot_trades.last_exit_review_note — keep what the reviewer actually said
--
-- The reviewer's reasoning existed only in `docker logs`. A deploy RECREATES the
-- bot container rather than restarting it, so every redeploy destroys it. Three
-- deploys on 2026-10-06 wiped the reasoning behind every LLM_EXIT before it,
-- including trade 164 -- the worst LIVE trade to date, cut at the first review at
-- exactly 60 minutes, and the reason it gave is now unrecoverable.
--
-- That is not a cosmetic loss. H33 is a hypothesis about whether reviewing twice
-- as often produces better judgement, and its KEEP condition is written against
-- LLM_EXIT performance. Judging it means reading what the model said and whether
-- the position deserved it. `bot_trades` already keeps `entry_rationale` for
-- exactly this purpose on the entry side; there was no exit equivalent, so the
-- half of the decision that closes positions kept nothing.
--
-- Written on EVERY review, not only on the one that closes the trade. A HOLD is a
-- decision too, and the sequence of HOLDs before a CUT is what shows whether an
-- extra look changed anything -- which is the whole question H33 asks. The column
-- is overwritten each review, so it holds the most recent answer; on a closed row
-- that is the answer that closed it.
--
-- It rides on the UPDATE that already stamps last_exit_review_at, so this costs no
-- extra write.
--
-- Nullable with no default: NULL means no review has happened yet, which is a real
-- and common state (nothing is reviewed in its first hour), and is not the same as
-- a review that answered with an empty string.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS last_exit_review_note TEXT;

COMMENT ON COLUMN bot_trades.last_exit_review_note IS
    'What the exit reviewer said at last_exit_review_at, CUT or HOLD. Written on every '
    'review; on a closed row it is the answer that closed it. Added 2026-10-06 because '
    'the reasoning lived only in docker logs, which a redeploy destroys. NULL = never '
    'reviewed (normal inside the first hour).';

DO $$
DECLARE n INT;
BEGIN
    SELECT count(*) INTO n
      FROM information_schema.columns
     WHERE table_name = 'bot_trades' AND column_name = 'last_exit_review_note';

    IF n <> 1 THEN
        RAISE EXCEPTION '047: last_exit_review_note is not present on bot_trades';
    END IF;

    RAISE NOTICE '047: bot_trades.last_exit_review_note added. Existing rows stay NULL — '
                 'the reasoning behind every LLM_EXIT before this point is already gone.';
END $$;
