-- ─────────────────────────────────────────────────────────────────────────────
-- 038: Drop the eight schema objects nothing reads, and the two research
--      functions that were never in git
--
-- How these were found
-- --------------------
-- Every table, view and function declared under sql/ was cross-referenced against the
-- C# source, scripts/ and docker-compose.yml. Anything with zero references outside its
-- own definition is listed below. The same sweep cleared nine dead members out of the C#
-- side on the same day — see HYPOTHESES.md, "Removed features", section 5.
--
-- Two objects failed that test and are deliberately NOT dropped, because the count was
-- misleading rather than because they earn their place some other way:
--
--   gate_reason_cluster()      called by the signal_gate_report VIEW at sql/029:235, and
--   gate_premise_contradicted()   that view is what scripts/gate-report.sql reads. A
--                              reference from inside a view does not show up as a
--                              reference from code, which is exactly how a live
--                              dependency gets mistaken for a dead one.
--
-- bot_trades_archive is also kept: it holds 54 rows of retired-strategy history. That is
-- data, not code, and sql/027 archived it there on purpose.
--
-- All five tables below were verified EMPTY on production before this was written.
--
-- What each was, and what killed it
-- ---------------------------------
--   ensure_trade_partition()   Creates a daily partition of `trades` and its index.
--                              DUPLICATE: DatabaseInitializer.EnsureDailyPartitionAsync
--                              does the same job in C#, and that is the one that runs —
--                              called from TradeRepository on startup and from
--                              FeatureAggregationWorker each cycle. Two implementations
--                              of one rule, disagreeing about index naming, is the drift
--                              this repo has paid for three times. drop_old_trade_
--                              partitions() stays: it IS called from code.
--
--   price_alerts               A price-alert feature with no code behind it since the
--   alert_notifications        API and UI were deleted on 2026-09-06. Dropped child
--                              first: alert_notifications has an FK to price_alerts.
--
--   app_users                  Registered devices for the deleted UI. Not a database
--                              role — dropping the table touches no credential.
--
--   prediction_table           Written by the ML service, read by ApiService. Both are
--                              gone; the compose file has had no prediction service for
--                              weeks. sql/001 and sql/012 created and extended it.
--
--   gate_tuning_log            A record of gate threshold changes that nothing ever
--                              wrote. HYPOTHESES.md is where that history actually
--                              lives, with the decision rule fixed in advance, which is
--                              more than this table was ever going to hold.
--
--   v_whale_summary            Diagnostic views nothing reads. v_flow_signal_readiness
--   v_partition_sizes          is NOT dropped — scripts/health.sh reads it, and it is
--   v_flow_bar_coverage        the one that answers "can the strategy score at all".
--                              v_flow_bar_coverage answered the narrower "what is in
--                              flow_bars_15m"; recreate it from sql/017 if that question
--                              comes back.
--
-- chop_test / rev_test — present on production, absent from git
-- -------------------------------------------------------------
-- Two SQL functions created by hand during a research session and never committed, so a
-- database built from sql/ has never had them and `IF EXISTS` is doing real work here.
-- Both scan klines_1m for a range-edge touch and ask whether price reverted to the
-- midpoint or extended by half the range width within 240 minutes: rev_test takes the
-- range from a fixed lookback, chop_test additionally requires the window to be choppy
-- (net move over path length below a threshold). They are the measurement behind "SOL
-- reverts at a range edge only 18-36% of the time", which is the finding that makes grid
-- trading negative by construction: a grid needs n = W/2f fills to clear its own fees,
-- and a 1-in-4 revert rate does not supply them.
--
-- The finding is what matters and it is recorded; the ad-hoc function bodies are not a
-- thing the deploy pipeline should be carrying. Anything worth re-running belongs in
-- sql/ as a committed file.
--
-- Safety
-- ------
-- Every statement is IF EXISTS and none of these objects is recreated by
-- DatabaseInitializer, which only creates trades, klines_1m, daily_feature_table,
-- bot_trades, strategy_verdicts and the daily trade partitions. Re-running is a no-op.
-- ─────────────────────────────────────────────────────────────────────────────

DROP FUNCTION IF EXISTS ensure_trade_partition(DATE);

DROP TABLE IF EXISTS alert_notifications;
DROP TABLE IF EXISTS price_alerts;
DROP TABLE IF EXISTS app_users;
DROP TABLE IF EXISTS prediction_table;
DROP TABLE IF EXISTS gate_tuning_log;

DROP VIEW IF EXISTS v_whale_summary;
DROP VIEW IF EXISTS v_partition_sizes;
DROP VIEW IF EXISTS v_flow_bar_coverage;

DROP FUNCTION IF EXISTS chop_test(NUMERIC, NUMERIC);
DROP FUNCTION IF EXISTS rev_test(INTEGER, NUMERIC);

DO $$
DECLARE
    leftover TEXT;
BEGIN
    SELECT string_agg(c, ', ') INTO leftover
    FROM (VALUES ('price_alerts'), ('alert_notifications'), ('app_users'),
                 ('prediction_table'), ('gate_tuning_log')) v(c)
    WHERE to_regclass(v.c) IS NOT NULL;

    IF leftover IS NOT NULL THEN
        RAISE EXCEPTION 'sql/038 left these behind: %', leftover;
    END IF;

    IF to_regclass('signal_gate_report') IS NULL
       OR to_regclass('v_flow_signal_readiness') IS NULL
       OR to_regclass('bot_trades_archive') IS NULL THEN
        RAISE EXCEPTION 'sql/038 dropped something it was meant to keep';
    END IF;

    RAISE NOTICE 'sql/038: dead objects dropped; gate report, readiness view and trade archive intact';
END $$;
