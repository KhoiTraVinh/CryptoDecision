-- ─────────────────────────────────────────────────────────────────────────────
-- 046: bot_config.high_volume_max_hold_minutes — the wave-catching leash (H31)
--
-- A position opened through the $20M news-print waiver is closed after 60 minutes
-- instead of the account-wide 720. Operator's rule: "lệnh bắt sóng phải chốt trong 1
-- giờ, vì hết sóng sẽ dễ bị đảo chiều."
--
-- The measurement agrees, for once
-- --------------------------------
-- Forward return after a >=$20M print, signed to the heavy side, entered 18 minutes
-- after the bucket opens which is where the bot actually enters:
--
--     15 min   +0.0593 %        60 min   +0.0611 %
--     30 min   -0.0439 %       120 min   +0.0573 %  but median +0.1881 and
--                                        second half -0.1755
--
-- Whatever edge exists is in the first hour. It does not follow that the first hour is
-- profitable — every horizon is negative after the 10 bps round trip, and the second
-- half of the sample is negative at all of them. The leash bounds the damage; it does
-- not create the edge. H31 carries that distinction in full.
--
-- Keyed on the ENTRY PATH, not the strategy, so it stays correct if the ratio path is
-- reopened beside the waiver. Positions closed this way carry `HV_TIMEOUT` rather than
-- `TIMEOUT`, so H31 can be judged without inferring which cap fired from the hold time.
--
-- 0 disables it and falls back to max_hold_minutes.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS high_volume_max_hold_minutes INT NOT NULL DEFAULT 60;

COMMENT ON COLUMN bot_config.high_volume_max_hold_minutes IS
    'H31, 2026-09-30. Minutes a position opened through the $20M waiver may be held, in '
    'place of max_hold_minutes. 60. Keyed on entry_path = HIGH_VOLUME, not on strategy. '
    'Such positions close as HV_TIMEOUT. 0 falls back to max_hold_minutes.';

DO $$
DECLARE v INT;
BEGIN
    SELECT high_volume_max_hold_minutes INTO v FROM bot_config WHERE id = 1;

    IF v IS NULL THEN
        RAISE EXCEPTION '046: high_volume_max_hold_minutes is NULL on bot_config row 1';
    END IF;

    RAISE NOTICE '046: high_volume_max_hold_minutes = % on bot_config row 1 (H31).', v;
END $$;
