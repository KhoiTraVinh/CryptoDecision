-- ─────────────────────────────────────────────────────────────────────────────
-- 042: bot_config.suspend_on_high_volume — the H26 switch
--
-- While a position opened through the $20M high-volume waiver is live, every OTHER
-- strategy is closed out as HV_REGIME and opens nothing until that position is gone.
--
-- Why the column exists rather than a constant in code
-- ----------------------------------------------------
-- This rule ships on five observations. There is no UI here, so bot_config IS the
-- control surface, and a rule this thinly evidenced has to be switchable without a
-- deploy:
--
--     UPDATE bot_config SET suspend_on_high_volume = FALSE WHERE id = 1;
--
-- The measurement behind it
-- -------------------------
-- The premise is the strong part. Over every 15-minute bucket recorded:
--
--     bucket >= $20M       63 buckets    median range 1.590 %   mean 1.967 %
--     bucket <  $20M    2,966 buckets    median range 0.377 %   mean 0.443 %
--
-- 4.2x the volatility. A rule that buys a 0.60 % two-bar fall is not reading a dip
-- there, and the operator's position is that only the rule which detected the state
-- should be trading in it.
--
-- The rule itself is thinner, and the two halves disagree:
--
--     CUT    4 positions   +1.181 R
--     BLOCK  1 position    -0.547 R     <- the single observation is against it
--                          --------
--                          +0.634 R
--
-- Cross-checked against a different population — cut at every >=$20M bucket close
-- rather than at a taken trade — and it agrees: +0.557 R over 4. Same sign, same size.
--
-- DEFAULT TRUE is the operator's explicit call, made with the BLOCK half's single
-- contrary observation in front of them. H26 carries the decision rule and the date.
--
-- Backfilled, not just defaulted
-- ------------------------------
-- ADD COLUMN ... DEFAULT fills existing rows in PostgreSQL 11+, so the single
-- bot_config row gets TRUE without a separate UPDATE. The assertion below checks that
-- rather than assuming it, because a configuration column that silently reads NULL is
-- how a live parameter ends up being whatever the code default happened to be.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS suspend_on_high_volume BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN bot_config.suspend_on_high_volume IS
    'H26, 2026-09-24. While a high-volume-waiver position is open, close every position '
    'belonging to another strategy as HV_REGIME and let that strategy open nothing until '
    'it closes. A >=$20M bucket is a 4.2x-volatility state (median 15m range 1.590% vs '
    '0.377% over 63 buckets against 2,966) and only the rule that detected it trades in '
    'it. Keyed on the strategy holding the position, never on a strategy name. Set FALSE '
    'to remove the behaviour without a deploy.';

DO $$
DECLARE
    v BOOLEAN;
BEGIN
    SELECT suspend_on_high_volume INTO v FROM bot_config WHERE id = 1;

    IF v IS NULL THEN
        RAISE EXCEPTION
            '042: bot_config.suspend_on_high_volume is NULL on row 1 — the column default '
            'did not backfill and the bot would run on whatever the code default is';
    END IF;

    RAISE NOTICE '042: suspend_on_high_volume = % on bot_config row 1 (H26).', v;
END $$;
