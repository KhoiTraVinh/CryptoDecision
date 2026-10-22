-- ─────────────────────────────────────────────────────────────────────────────
-- Restore the bot_config tuning that was live on the EC2 instance, captured
-- 2026-10-21 from the production row before that box was retired.
--
-- Why this file exists
-- --------------------
-- bot_config is the only part of the control surface that lives in the database
-- rather than in git. docker-compose.yml and docker-compose.prod.yml came across
-- with the repo -- they were byte-identical to git HEAD (949db7e) on the host --
-- and the .env is reconstructed by hand from secrets. The DB row is not in any
-- of those, so a fresh database does not get it.
--
-- A fresh database seeds row 1 from the column defaults, then 023 overwrites it
-- with the "intended" baseline. That baseline is NOT what production converged
-- to over three weeks of live trading. Nine columns drift:
--
--   column                        023 baseline   was live on prod
--   ----------------------------- -------------- ----------------
--   active_strategies             {XVENUE_FLOW}  {XVENUE_FLOW,CANDLE_REVERSAL}
--   capital_usd                   60             100
--   risk_pct_per_trade            0.0100         0.0060
--   max_open_trades_per_strategy  1              2
--   max_entries_per_day           4              20
--   max_hold_minutes              720            180
--   max_open_per_side             0              1
--   max_consecutive_losses        5              15
--
-- Everything else 023 and the later column defaults already set correctly, so
-- this file only touches what actually differed. Verified column by column
-- against the live row, not assumed.
--
-- NOT placed in sql/
-- ------------------
-- sql/migrate.sh globs '*.sql' in that directory -- numbering is cosmetic, every
-- file matching the glob is applied. Dropping this there would make it run
-- automatically on every database, which is wrong twice over: it would clobber
-- an operator's later tuning, and it would arm a config it has no business
-- arming. Apply it by hand, once, on the new box:
--
--   docker exec -i postgres psql -U crypto -d crypto < scripts/restore-prod-bot-config.sql
--
-- enabled and paper_mode are deliberately NOT set here
-- ---------------------------------------------------
-- Production ran enabled = TRUE, paper_mode = FALSE, on real funds. This file
-- restores the tuning and leaves the bot disarmed, for the same reason 023 says
-- it: arming is a deliberate act by a person, not a side effect of restoring a
-- backup onto a machine whose exchange connectivity has not been checked yet.
-- The arm step is at the bottom, commented out.
-- ─────────────────────────────────────────────────────────────────────────────

BEGIN;

UPDATE bot_config SET
    -- Both strategies were active. CANDLE_REVERSAL binds from DipStrategy and
    -- XVENUE_FLOW from FlowStrategy; the compose file tunes both sections, which
    -- only makes sense with both names here.
    active_strategies            = '{XVENUE_FLOW,CANDLE_REVERSAL}',

    -- The account this was actually sized against.
    capital_usd                  = 100,

    -- 0.60% of capital per trade, not 1.00%. Reduced on the live account; the
    -- 023 baseline predates that.
    risk_pct_per_trade           = 0.0060,

    -- Two per strategy, bounded to one per side by max_open_per_side below, so
    -- the pair is a long and a short rather than two correlated entries in the
    -- same direction.
    max_open_trades_per_strategy = 2,
    max_open_per_side            = 1,

    -- 20, not 4. The dip rule evaluates far more often than the flow rule and
    -- was hitting the 4-cap as a gate rather than a backstop.
    max_entries_per_day          = 20,

    -- 3 hours. H40 capped the hold; 720 was the pre-H40 value.
    max_hold_minutes             = 180,

    -- 15. The per-strategy breaker at 5 was tripping on normal variance for a
    -- rule that wins just under half its entries; see the reverted
    -- account-level breaker (208c97e / 28310dd) for the same lesson.
    max_consecutive_losses       = 15,

    updated_at                   = now()
WHERE id = 1;

-- Fail loudly rather than silently doing nothing if row 1 is missing, which is
-- what happens if this runs before migrations.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM bot_config WHERE id = 1) THEN
        RAISE EXCEPTION 'bot_config row 1 does not exist -- run sql/migrate.sh first';
    END IF;
END $$;

COMMIT;

\echo ''
\echo 'bot_config restored. Current state:'
SELECT enabled, paper_mode, symbol, exchange, active_strategies, capital_usd,
       risk_pct_per_trade, max_open_trades_per_strategy, max_open_per_side,
       max_entries_per_day, max_hold_minutes, max_consecutive_losses
FROM bot_config WHERE id = 1;

\echo ''
\echo 'Bot is DISARMED (enabled=false, paper_mode=true).'
\echo 'To arm on real funds, deliberately:'
\echo '  UPDATE bot_config SET enabled = TRUE, paper_mode = FALSE WHERE id = 1;'
