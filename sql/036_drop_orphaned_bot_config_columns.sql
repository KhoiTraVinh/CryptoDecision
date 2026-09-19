-- ─────────────────────────────────────────────────────────────────────────────
-- 036: Drop the sixteen bot_config columns that nothing reads
--
-- Why a schema change for columns that cost nothing to keep
-- --------------------------------------------------------
-- `bot_config` is the entire control surface of this system: there is no UI and no API,
-- so an operator answering "what is this bot configured to do" reads this row. Sixteen of
-- its columns describe features that no longer exist, and the harm is not storage — it is
-- that grepping for one of them finds a column and concludes the feature is still there.
-- This repository has paid for that confusion repeatedly: a banner naming a retired rule,
-- a monitor reading a field nobody writes, a validation tool certifying a configuration
-- nobody runs. A config row that describes a bot which does not exist is the same defect
-- one layer down.
--
-- What each column was, and what removed it
-- -----------------------------------------
--   grid_step_pct            GRID strategy, gone before the 2026-08-16 rework
--   min_ai_confidence        the MOMENTUM-era composite score and its AI term
--   min_buy_ratio_1h         MOMENTUM's buy-ratio thresholds
--   min_momentum_buy_ratio
--   trailing_stop_pct        the trailing stop, removed after it closed four consecutive
--   use_trailing_stop        live entries that had moved at most +0.29% in their favour
--   use_ai_agent             the tool-calling agent, ~853 lines deleted 2026-09-06; the
--   use_ai_filter            flag was false throughout its life
--   use_breakeven_stop       the breakeven stop, deleted 2026-09-19 with the other three
--   breakeven_trigger_pct    dormant features -- see HYPOTHESES.md "Removed features"
--   last_verdict_code        superseded by the strategy_verdicts table in sql/034. One
--   last_verdict_detail      row could not hold two strategies' verdicts, so whichever
--   last_verdict_z           strategy ran last silently overwrote the other. The bot has
--   last_verdict_agree       not written these since 034; on this database they were
--   last_verdict_venues      frozen 174 hours at the moment this migration was written.
--   last_verdict_at
--
-- Ordering
-- --------
-- migrate.sh applies files in filename order, so 005/007/010/011/023 all run — creating
-- and then setting these columns — before 036 removes them. 023 must not be edited to
-- stop setting them: it is already recorded in schema_migrations with a checksum, and
-- editing an applied migration is what makes two machines disagree about the schema.
--
-- One consequence, verified rather than assumed: **023 is no longer independently
-- re-runnable.** After 036 it fails with `column "use_trailing_stop" does not exist`.
-- That is harmless because migrate.sh never re-runs a file it has recorded, but anyone
-- replaying migrations by hand will hit it.
--
-- A NOTE ON "FRESH DATABASE", because the first draft of this comment asserted something
-- that turned out to be false. Applying sql/*.sql in filename order to an empty database
-- does NOT work today, and has not for reasons that predate this file: 006 runs
-- `ALTER TABLE bot_trades` and 008 is what creates `bot_trades`. So the sequence stops at
-- 006 long before reaching 036. That is a real defect in the migration set — migrate.sh's
-- own header says it exists so "a new EC2 instance would boot with a schema" — and it is
-- filed separately rather than fixed here, because renumbering applied migrations is
-- exactly what the checksum ledger forbids.
--
-- This file was verified against a throwaway local postgres:16 with the full set applied
-- (008 hoisted to work around that defect): all sixteen columns are gone afterwards, and
-- every bot_config column that remains has a reader in BotConfigRepository.
--
-- Checked before writing this, because DROP COLUMN is not reversible:
--   • no VIEW depends on bot_config (pg_depend, zero rows), so no CASCADE is needed and
--     nothing is dropped silently along with them
--   • scripts/health.sh and scripts/flow.sh were the only readers of last_verdict_*, both
--     as a FALLBACK path. Left in place they would not merely stop working — health.sh
--     would error, get an empty result, and skip the branch that prints the LIVE
--     per-strategy verdicts. Both scripts were changed in the same commit to read
--     strategy_verdicts directly.
--   • no C# reads any of the sixteen. BotConfigRepository's SELECT was narrowed on
--     2026-09-19; BotOptions no longer carries the breakeven pair.
--
-- IF NOT EXISTS on every drop so this is re-runnable, and so it applies cleanly to a
-- database that never had some of them.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    DROP COLUMN IF EXISTS grid_step_pct,
    DROP COLUMN IF EXISTS min_ai_confidence,
    DROP COLUMN IF EXISTS min_buy_ratio_1h,
    DROP COLUMN IF EXISTS min_momentum_buy_ratio,
    DROP COLUMN IF EXISTS trailing_stop_pct,
    DROP COLUMN IF EXISTS use_trailing_stop,
    DROP COLUMN IF EXISTS use_ai_agent,
    DROP COLUMN IF EXISTS use_ai_filter,
    DROP COLUMN IF EXISTS use_breakeven_stop,
    DROP COLUMN IF EXISTS breakeven_trigger_pct,
    DROP COLUMN IF EXISTS last_verdict_code,
    DROP COLUMN IF EXISTS last_verdict_detail,
    DROP COLUMN IF EXISTS last_verdict_z,
    DROP COLUMN IF EXISTS last_verdict_agree,
    DROP COLUMN IF EXISTS last_verdict_venues,
    DROP COLUMN IF EXISTS last_verdict_at;

COMMENT ON TABLE bot_config IS
    'One row. The entire control surface: start, stop, sizing, limits and risk. There is '
    'no UI and no API, so this row is what an operator reads and edits. Every column here '
    'is read by the running bot -- sixteen that were not were dropped in sql/036.';
