-- The five bot_config columns a fresh deployment never got.
--
-- Found by tearing the local stack down with `-v` and bringing it back up. All ten
-- services came up correct and 28 migrations applied from an empty volume, but
-- `GET /api/bot/status` returned 500 forever and the SignalR BotStatus broadcast
-- failed every 15 seconds:
--
--     42703: column "last_refusal_reason" does not exist
--
-- Why it only happens on a fresh volume. These columns were added by
-- `DatabaseInitializer.EnsureBotConfigColumnsAsync` in ProcessorService, and that
-- ALTER is deliberately guarded:
--
--     IF to_regclass('public.bot_config') IS NOT NULL THEN ...
--
-- The guard exists because the startup order is
-- postgres -> db-check -> processor -> db-migrate -> bot, and `bot_config` is
-- created by sql/005 and sql/007 — which run in db-migrate, AFTER the processor.
-- So on a clean database the table does not exist yet, the guard is false, the
-- whole ALTER is skipped in silence, and nothing later adds the columns.
--
-- On a long-lived database they exist only because the processor happened to
-- restart at some point after bot_config was created. That is not a mechanism, it
-- is a coincidence that every existing deployment has had and a new one has not.
-- `use_ai_agent` escaped because sql/013 adds it explicitly; these five did not.
--
-- Putting them in a migration is the fix rather than reordering the services: the
-- order is itself a constraint, since the processor owns the base tables that
-- sql/006 alters. db-migrate runs after bot_config exists, so this is
-- deterministic where the guarded ALTER was accidental.
--
-- Blast radius while it was broken: the dashboard's main endpoint returned 500,
-- and RecordEntryRefusalAsync failed on every call — wrapped in SafeRecordAsync,
-- so it degraded quietly rather than stopping trading. The bot traded fine and the
-- operator's view of *why it was refusing* was the part that went missing, which
-- is the same failure this trail of columns was added to fix in the first place.

ALTER TABLE bot_config
    -- Why the last entry was not placed, and how many the bot has refused today.
    -- A refused entry is a normal outcome and was logged as one, which meant a bot
    -- that had refused every entry for hours looked identical on screen to one
    -- waiting patiently for a signal.
    ADD COLUMN IF NOT EXISTS last_refusal_reason TEXT,
    ADD COLUMN IF NOT EXISTS last_refusal_at     TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS refusal_count       INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS refusal_count_date  DATE,
    -- What the last real sizing decision produced. The dashboard can compute what
    -- sizing *would* ask for; only the bot knows what survived the exchange's lot
    -- grid and the per-order ceiling.
    ADD COLUMN IF NOT EXISTS last_sizing_note    TEXT;

COMMENT ON COLUMN bot_config.refusal_count IS
    'Refusals today. Counts refusals that mattered — the daily entry cap and the gate '
    'declining a finished proposal — not per-cycle abstentions, which land in '
    'last_verdict_code instead and would push this to ~2,880 a day.';

DO $$
DECLARE missing TEXT;
BEGIN
    SELECT string_agg(c, ', ') INTO missing
    FROM (VALUES ('last_refusal_reason'), ('last_refusal_at'), ('refusal_count'),
                 ('refusal_count_date'), ('last_sizing_note')) v(c)
    WHERE NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'bot_config' AND column_name = v.c);

    IF missing IS NOT NULL THEN
        RAISE EXCEPTION 'bot_config still missing: %', missing;
    END IF;
    RAISE NOTICE 'bot_config refusal and sizing columns present';
END $$;
