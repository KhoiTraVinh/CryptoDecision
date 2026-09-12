-- What each entry rule is actually worth, read separately.
--
--     ssh ec2 "cd ~/cryptodecision && docker exec -i postgres psql -U crypto -d crypto \
--              < scripts/rules.sql"
--
-- Why this file exists
-- --------------------
-- Four or five hypotheses are open on one trade stream at once, and they are not
-- separable by eye:
--
--     H9   the 2.1x ratio entry              strategy = XVENUE_FLOW, entry_path = RATIO
--     H11  the >=$20M news-print waiver      strategy = XVENUE_FLOW, entry_path = HIGH_VOLUME
--     H12  dynamic TP/SL                     affects every row; no column marks it
--     H13  CandleReversal in parallel        strategy = CANDLE_REVERSAL
--
-- The rule at the top of HYPOTHESES.md is one live change at a time, precisely so this
-- situation does not arise. It has arisen anyway, so the next best thing is to read each
-- rule on its own rows. `strategy` and `entry_path` make three of the four a one-predicate
-- query. H12 is the one that cannot be split this way — it changes the barriers on every
-- trade, so its counterfactual has to be simulated offline, which is what its decision
-- rule says.
--
-- It replaces flow-vs-passive.sql, which banded forward returns by |z| and labelled the
-- band ">= 1.5" as "bot ENTERS". EnterZ has been 1.0 since 2026-08-27 and no deployed
-- rule has read z at all since the entry moved to FlowRatio, so both halves of that
-- label were wrong. The measurement underneath was real; it was measuring a statistic
-- nothing trades on.
--
-- R is recovered as pnl_pct / stop_pct, since the stop distance is what one R is. Rows
-- without stored geometry cannot be expressed in R and are counted separately rather
-- than folded in at some assumed stop.

\pset border 2
\pset pager off
\timing off

\echo ''
\echo '════════ 0. Is there enough here to say anything? ════════'
\echo '-- Read this first. Every hypothesis in HYPOTHESES.md carries a bar in closed'
\echo '-- trades, and a number below its bar is a number that will move.'
WITH t AS (
    SELECT COALESCE(strategy, '?')   AS strategy,
           COALESCE(entry_path, '(before entry_path existed)') AS entry_path,
           status, pnl_pct, stop_price, entry_price, opened_at
    FROM bot_trades
    WHERE mode = 'PAPER'
)
SELECT strategy,
       entry_path,
       COUNT(*)                                              AS trades,
       COUNT(*) FILTER (WHERE status = 'OPEN')               AS still_open,
       COUNT(*) FILTER (WHERE stop_price IS NULL)            AS no_geometry,
       MIN(opened_at)::DATE                                  AS first_day,
       MAX(opened_at)::DATE                                  AS last_day,
       ROUND(EXTRACT(epoch FROM (MAX(opened_at) - MIN(opened_at))) / 86400.0, 1) AS days
FROM t
GROUP BY 1, 2
ORDER BY 1, 2;

\echo ''
\echo '════════ 1. Each rule, priced ════════'
\echo '-- R = pnl_pct / stop_pct, so one R is one stop. A rule that cannot cover its own'
\echo '-- execution cost shows up here as a mean R near zero with a plausible win rate.'
WITH t AS (
    SELECT COALESCE(strategy, '?') AS strategy,
           COALESCE(entry_path, '(unmarked)') AS entry_path,
           pnl_usd,
           pnl_pct / NULLIF(ABS(entry_price - stop_price) / NULLIF(entry_price, 0), 0) AS r
    FROM bot_trades
    WHERE mode = 'PAPER' AND status IN ('CLOSED', 'STOPPED') AND stop_price IS NOT NULL
)
SELECT strategy,
       entry_path,
       COUNT(*)                                       AS closed,
       COUNT(*) FILTER (WHERE r > 0)                   AS wins,
       COUNT(*) FILTER (WHERE r <= 0)                  AS losses,
       ROUND(100.0 * COUNT(*) FILTER (WHERE r > 0) / NULLIF(COUNT(*), 0), 1) AS win_pct,
       ROUND(AVG(r), 3)                                AS mean_r,
       ROUND(SUM(r), 2)                                AS total_r,
       ROUND(SUM(pnl_usd), 4)                          AS pnl_usd
FROM t
GROUP BY 1, 2
ORDER BY 1, 2;

\echo ''
\echo '════════ 2. The three checks, per rule ════════'
\echo '-- Nothing in this repository is treated as a finding unless it holds in BOTH'
\echo '-- halves of its own window AND after discarding its single best trade. Two'
\echo '-- separate "findings" turned out to be one flash crash each.'
\echo '-- A rule with fewer than ~20 closed trades cannot fail these honestly; it can'
\echo '-- only fail to populate them.'
WITH t AS (
    SELECT COALESCE(strategy, '?') AS strategy,
           COALESCE(entry_path, '(unmarked)') AS entry_path,
           opened_at,
           pnl_pct / NULLIF(ABS(entry_price - stop_price) / NULLIF(entry_price, 0), 0) AS r
    FROM bot_trades
    WHERE mode = 'PAPER' AND status IN ('CLOSED', 'STOPPED') AND stop_price IS NOT NULL
),
b AS (
    SELECT strategy, entry_path,
           MIN(opened_at) AS lo, MAX(opened_at) AS hi
    FROM t GROUP BY 1, 2
),
j AS (
    SELECT t.*, t.opened_at < b.lo + (b.hi - b.lo) / 2 AS first_half,
           ROW_NUMBER() OVER (PARTITION BY t.strategy, t.entry_path ORDER BY t.r DESC) AS rank_desc
    FROM t JOIN b ON b.strategy = t.strategy AND b.entry_path = t.entry_path
)
SELECT strategy,
       entry_path,
       COUNT(*)                                                         AS n,
       ROUND(AVG(r), 3)                                                 AS mean_r,
       ROUND(AVG(r) FILTER (WHERE first_half), 3)                       AS first_half,
       ROUND(AVG(r) FILTER (WHERE NOT first_half), 3)                   AS second_half,
       ROUND(AVG(r) FILTER (WHERE rank_desc > 1), 3)                    AS less_best_trade,
       CASE
           WHEN COUNT(*) < 20 THEN 'too few to judge'
           WHEN AVG(r) > 0
            AND AVG(r) FILTER (WHERE first_half) > 0
            AND AVG(r) FILTER (WHERE NOT first_half) > 0
            AND AVG(r) FILTER (WHERE rank_desc > 1) > 0 THEN 'passes all three'
           ELSE 'FAILS at least one'
       END                                                              AS verdict
FROM j
GROUP BY 1, 2
ORDER BY 1, 2;

\echo ''
\echo '════════ 3. How each rule exits ════════'
\echo '-- The exit mix is the fastest way to see a rule doing something other than what'
\echo '-- it was measured doing. Two shapes to watch for: OFI_REVERSAL crowding out TP'
\echo '-- entirely (the barriers have stopped participating), and TP collapsing toward'
\echo '-- zero under dynamic TP/SL (the target retreats as price advances toward it,'
\echo '-- which is H12 failing in exactly the way its simulation predicted).'
SELECT COALESCE(strategy, '?')         AS strategy,
       COALESCE(entry_path, '(unmarked)') AS entry_path,
       COALESCE(close_reason, '(open)')   AS close_reason,
       COUNT(*)                        AS trades,
       ROUND(AVG(pnl_pct * 100), 3)    AS mean_pct,
       ROUND(SUM(pnl_usd), 4)          AS pnl_usd
FROM bot_trades
WHERE mode = 'PAPER'
GROUP BY 1, 2, 3
ORDER BY 1, 2, 4 DESC;

\echo ''
\echo '════════ 4. The position book against every limit ════════'
\echo '-- Four nested caps, checked in this order, all before the gate is asked anything.'
\echo '-- A rule that never seems to trade is usually not abstaining -- it is losing the'
\echo '-- slot to a rule that signals more often, because slots are first come first'
\echo '-- served and are NOT reserved per strategy.'
-- The caps are read through row_to_json rather than as columns, so this file runs
-- against a database that has not had sql/032 and sql/033 applied yet. Naming a column
-- that does not exist aborts the statement with "column does not exist"; extracting a
-- missing key from a json row yields NULL, which prints as "not migrated" and tells the
-- operator something useful instead of failing. That matters because this script is most
-- wanted exactly when a deploy is half-done.
WITH o AS (
    SELECT COALESCE(strategy, '?') AS strategy,
           COALESCE(entry_path, '(unmarked)') AS entry_path,
           side
    FROM bot_trades WHERE status = 'OPEN'
),
c AS (SELECT row_to_json(b) AS j FROM bot_config b WHERE b.id = 1),
lim AS (
    SELECT (j ->> 'max_open_total')::INT               AS total,
           (j ->> 'max_open_trades_per_strategy')::INT AS per_strategy,
           (j ->> 'max_open_per_side')::INT            AS per_side,
           (j ->> 'max_open_high_volume')::INT         AS high_volume
    FROM c
),
rows AS (
    SELECT 1 AS ord, 'open across all strategies' AS limit_name,
           (SELECT COUNT(*) FROM o)::INT AS current, total AS cap FROM lim
    UNION ALL
    SELECT 2, 'open per strategy (worst strategy)',
           COALESCE((SELECT MAX(n) FROM (SELECT COUNT(*) n FROM o GROUP BY strategy) x), 0)::INT,
           per_strategy FROM lim
    UNION ALL
    SELECT 3, 'open per strategy per side (worst)',
           COALESCE((SELECT MAX(n) FROM (SELECT COUNT(*) n FROM o GROUP BY strategy, side) x), 0)::INT,
           per_side FROM lim
    UNION ALL
    SELECT 4, 'open from the high-volume waiver',
           (SELECT COUNT(*) FROM o WHERE entry_path = 'HIGH_VOLUME')::INT,
           high_volume FROM lim
)
SELECT limit_name,
       current,
       COALESCE(cap::TEXT, 'not migrated') AS cap,
       CASE
           WHEN cap IS NULL   THEN 'column missing -- apply sql/032 and sql/033'
           WHEN cap = 0       THEN 'disabled'
           WHEN current >= cap THEN 'BINDING'
           ELSE ''
       END AS note
FROM rows ORDER BY ord;

\echo ''
\echo '-- Which strategies are allowed to trade at all:'
SELECT active_strategies::TEXT AS active_strategies,
       enabled,
       paper_mode,
       use_dynamic_tp_sl,
       max_entries_per_day,
       cooldown_seconds
FROM bot_config WHERE id = 1;
