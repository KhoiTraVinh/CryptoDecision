-- ─────────────────────────────────────────────────────────────────────────────
-- 045: bring back bot_config.suspend_on_high_volume — the H31 switch
--
-- Dropped by sql/043 on 2026-09-28, when H29 switched XVENUE_FLOW off and the rule
-- became unreachable. H31 turns the waiver back on, so the switch is needed again.
--
-- Why this column matters more this time than last
-- -------------------------------------------------
-- H26 shipped on five positions. H31 ships on ONE EVENT. The entire route out of this
-- configuration, without a deploy, is:
--
--     UPDATE bot_config SET suspend_on_high_volume = FALSE WHERE id = 1;   -- stop the cut/block
--     UPDATE bot_config SET active_strategies = '{CANDLE_REVERSAL}' WHERE id = 1;  -- stop the waiver
--
-- Both are recorded in H31 so they do not have to be reconstructed under pressure.
--
-- DEFAULT TRUE, which is what H31 asks for, and the assertion below checks the single
-- bot_config row actually carries it rather than a NULL that the code default would
-- silently paper over.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS suspend_on_high_volume BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN bot_config.suspend_on_high_volume IS
    'H31, 2026-09-30. While a high-volume-waiver position is open, close every position '
    'belonging to another strategy as HV_REGIME and let that strategy open nothing until '
    'it closes. Keyed on the strategy holding the position, never on a strategy name. Set '
    'FALSE to remove the behaviour without a deploy.';

DO $$
DECLARE
    v BOOLEAN;
BEGIN
    SELECT suspend_on_high_volume INTO v FROM bot_config WHERE id = 1;

    IF v IS NULL THEN
        RAISE EXCEPTION
            '045: bot_config.suspend_on_high_volume is NULL on row 1 — the default did not '
            'backfill and the bot would run on whatever the code default happens to be';
    END IF;

    RAISE NOTICE '045: suspend_on_high_volume = % on bot_config row 1 (H31).', v;
END $$;
