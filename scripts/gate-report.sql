-- What the gate's decisions were worth, by the reason it gave.
--
--     docker exec -i postgres psql -U crypto -d crypto -f /sql/../scripts/gate-report.sql
--     ssh ec2 "cd ~/cryptodecision && docker exec -i postgres psql -U crypto -d crypto \
--              < scripts/gate-report.sql"
--
-- Reads signal_outcomes, which holds every actionable signal the strategy produced —
-- traded or not — with the outcome the market delivered. The refused signals are the
-- entire point: a gate can only be judged against what its refusals would have done.
--
-- READ SECTION 0 FIRST. Every number below is meaningless until the sample is large
-- enough, and the first version of this analysis was run on 15 signals from a single
-- 23-hour window in a single trending market. It produced clean, confident,
-- unreliable percentages.

\pset border 2
\timing off

\echo ''
\echo '════════ 0. Is there enough data to say anything? ════════'
SELECT
    COUNT(*)                                                         AS signals_recorded,
    COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT'))      AS decided,
    COUNT(*) FILTER (WHERE outcome = 'PENDING')                      AS pending,
    COUNT(*) FILTER (WHERE outcome IN ('EXPIRED','NO_TICKS'))        AS unlabelable,
    ROUND(EXTRACT(epoch FROM (MAX(signal_at) - MIN(signal_at))) / 86400.0, 1) AS days_spanned,
    CASE
        WHEN COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT')) < 200
          OR EXTRACT(epoch FROM (MAX(signal_at) - MIN(signal_at))) < 7 * 86400
        THEN 'INSUFFICIENT — under 200 decided signals or under 7 days. '
             'These numbers describe one market regime. Do not tune thresholds from them.'
        ELSE 'Sample floor met. Still one instrument and one strategy.'
    END AS verdict
FROM signal_outcomes;

\echo ''
\echo '════════ 1. Approve vs refuse: what each was worth ════════'
\echo '-- R is in multiples of the risk to the stop. A win pays the reward:risk ratio,'
\echo '-- a loss is exactly -1, a timeout is what the position was worth when the'
\echo '-- 12-hour hold limit closed it. Refused rows carry the R the trade WOULD have'
\echo '-- returned, which is the only way to price a refusal.'
SELECT
    COALESCE(gate_decision, 'NOT_ASKED')  AS decision,
    COUNT(*)                              AS signals,
    COUNT(*) FILTER (WHERE outcome = 'WIN')     AS wins,
    COUNT(*) FILTER (WHERE outcome = 'LOSS')    AS losses,
    COUNT(*) FILTER (WHERE outcome = 'TIMEOUT') AS timeouts,
    ROUND(100.0 * COUNT(*) FILTER (WHERE outcome = 'WIN')
          / NULLIF(COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS')), 0), 1) AS win_rate_pct,
    ROUND(SUM(outcome_r) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT')), 2) AS total_r,
    ROUND(AVG(outcome_r) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT')), 3) AS avg_r
FROM signal_outcomes
GROUP BY 1 ORDER BY 2 DESC;

\echo ''
\echo '════════ 2. Every refusal reason, priced ════════'
\echo '-- A reason cluster with a NEGATIVE avg_r among refused signals was doing its'
\echo '-- job: the trades it stopped would have lost. A POSITIVE avg_r means that'
\echo '-- reason is costing money — the gate is filtering out the trades it should be'
\echo '-- taking. That is the single most useful column in this file.'
SELECT
    reason_cluster,
    premise_contradicted,
    signals,
    wins,
    losses,
    timeouts,
    win_rate_pct,
    total_r,
    avg_r
FROM signal_gate_report
WHERE gate_decision IN ('REFUSED', 'APPROVED')
ORDER BY signals DESC;

\echo ''
\echo '════════ 3. Refusals on a premise the brief contradicted ════════'
\echo '-- Not a judgement call and not a sample-size question: the gate asserted'
\echo '-- something the brief it was handed says is false. Excluded venues above zero'
\echo '-- when zero were excluded; positions already open when none were; dispersion'
\echo '-- "wide" at a fraction of the ceiling the scorer already enforced.'
\echo '-- Any row here is a defect regardless of how the trade would have gone.'
SELECT
    signal_at,
    side,
    aggregate_z,
    agreeing_venues || '/' || participating_venues AS agreement,
    excluded_venues,
    dispersion_bps,
    outcome,
    outcome_r,
    LEFT(gate_reason, 90) AS reason
FROM signal_outcomes
WHERE gate_decision = 'REFUSED'
  AND gate_premise_contradicted(gate_reason, excluded_venues, bot_trade_id)
ORDER BY signal_at DESC
LIMIT 40;

\echo ''
\echo '════════ 4. The worst calls, both directions ════════'
\echo '-- The weekly review list: refusals that would have won big, and approvals that'
\echo '-- lost. Read with the features next to them — the point is to find what the'
\echo '-- gate could have seen and did not, not to relitigate individual trades.'
(SELECT 'SKIPPED_BUT_WON' AS kind, signal_at, side, aggregate_z, dispersion_bps,
        agreeing_venues, outcome_r, LEFT(gate_reason, 70) AS reason
 FROM signal_outcomes
 WHERE gate_decision = 'REFUSED' AND outcome = 'WIN'
 ORDER BY outcome_r DESC LIMIT 10)
UNION ALL
(SELECT 'APPROVED_AND_LOST', signal_at, side, aggregate_z, dispersion_bps,
        agreeing_venues, outcome_r, LEFT(gate_reason, 70)
 FROM signal_outcomes
 WHERE gate_decision IN ('APPROVED', 'APPROVED_DEGRADED') AND outcome = 'LOSS'
 ORDER BY outcome_r ASC LIMIT 10)
ORDER BY kind, outcome_r DESC;

\echo ''
\echo '════════ 5. Is the strategy itself worth gating? ════════'
\echo '-- Before asking whether the gate picks well, ask whether the signal is'
\echo '-- profitable unfiltered. If taking every signal is negative, a perfect gate is'
\echo '-- the only thing that could save it — and no gate is perfect. In the first'
\echo '-- audited window the raw signal won 3 of 15 against a breakeven rate near 33%,'
\echo '-- which is a strategy problem that no amount of gate tuning addresses.'
SELECT
    ROUND(AVG(target_pct / NULLIF(stop_pct, 0)), 2)                  AS avg_reward_risk,
    ROUND(100.0 / (1 + AVG(target_pct / NULLIF(stop_pct, 0))), 1)    AS breakeven_win_rate_pct,
    COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS'))                AS decided,
    ROUND(100.0 * COUNT(*) FILTER (WHERE outcome = 'WIN')
          / NULLIF(COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS')), 0), 1) AS actual_win_rate_pct,
    ROUND(SUM(outcome_r) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT')), 2) AS total_r_all_signals
FROM signal_outcomes;

\echo ''
\echo '════════ 6. Dispersion, the gate''s most-used reason, against outcomes ════════'
\echo '-- Buckets the feature the gate refuses on most often. If "late entry" carries'
\echo '-- information, the higher buckets should show a worse win rate. If they do not,'
\echo '-- the ceiling in FlowSignalOptions is the only dispersion check worth having.'
SELECT
    width_bucket(dispersion_bps, 0, 25, 5) AS bucket,
    MIN(dispersion_bps) || '-' || MAX(dispersion_bps) || ' bps' AS range,
    COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT')) AS decided,
    ROUND(100.0 * COUNT(*) FILTER (WHERE outcome = 'WIN')
          / NULLIF(COUNT(*) FILTER (WHERE outcome IN ('WIN','LOSS')), 0), 1) AS win_rate_pct,
    ROUND(AVG(outcome_r) FILTER (WHERE outcome IN ('WIN','LOSS','TIMEOUT')), 3) AS avg_r
FROM signal_outcomes
WHERE dispersion_bps IS NOT NULL
GROUP BY 1 ORDER BY 1;

\echo ''
\echo '-- Reminder: if section 0 said INSUFFICIENT, everything above is one regime.'
