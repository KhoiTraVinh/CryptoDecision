-- ─────────────────────────────────────────────────────────────────────────────
-- 019: Daily entry cap
--
-- The one limit that was missing. daily_loss_limit_pct caps what a bad day can
-- lose and max_open_trades_per_strategy caps what is at risk at once, but nothing
-- capped how many round trips the bot could pay fees on. On 2026-08-22 it opened
-- ten positions in under seven hours, four of them riding a single 6% move — for a
-- signal whose horizon is hours, that is a handful of decisions billed ten times.
--
-- Six against an expected two or three, so it only binds when something is wrong.
-- Zero disables it.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS max_entries_per_day INT NOT NULL DEFAULT 6;

COMMENT ON COLUMN bot_config.max_entries_per_day IS
    'Hard ceiling on entries opened per UTC day for the configured symbol and mode. '
    'Bounds fee spend rather than risk. 0 disables.';
