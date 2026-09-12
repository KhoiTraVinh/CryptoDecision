#!/usr/bin/env bash
# What FlowRatio actually reads: volume, OFI and the buy/sell imbalance, per closed
# 15-minute bucket.
#
#     bash ~/cryptodecision/scripts/flow.sh          once
#     bash ~/cryptodecision/scripts/flow.sh -w       every 30s until Ctrl-C
#
# Replaces z.sh, which reconstructed the ZScore statistic -- a 4-bucket rolling OFI
# standardised against a 44-bucket MAD baseline, with per-venue agreement flags. None of
# that decides anything any more. On 2026-09-11 it was flagging "<<< past +/-1.00" on
# three consecutive buckets and "AGREES" on Bybit while the bot sat at RATIO_TOO_LOW and
# would not have entered on any of them. A monitor that reports signals the strategy
# ignores is worse than no monitor: it makes a correctly idle bot look broken, and it
# would make a genuinely broken one look busy.
#
# Two numbers are printed and they are NOT the same thing:
#
#   1. The bot's own verdict, from bot_config. Authoritative -- it is the scorer that
#      actually ran. It can be stale: the loop stops forming verdicts while at the
#      position limit and while the bot is disabled.
#
#   2. A reconstruction from flow_bars_15m, applying the SAME rule in SQL. This exists so
#      there is a live reading when the bot is not scoring. Unlike z.sh's reconstruction,
#      it is the deployed rule rather than a retired one, so "would fire" here is a claim
#      worth checking rather than noise.
#
# Thresholds are READ FROM appsettings.json, never hardcoded. Config drift across tools
# has already cost this repository three parameters; a monitor carrying its own copy of a
# threshold is the fourth waiting to happen.
set -euo pipefail

SYMBOL="${SYMBOL:-SOLUSDT}"
REPO="${REPO:-$HOME/cryptodecision}"
BOT="${BOT_CONTAINER:-bot}"
CFG_REPO="$REPO/src/CryptoDecision.BotService/appsettings.json"

PSQL="docker exec -i postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -qtA -P pager=off"
PSQLT="docker exec -i postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -P pager=off -P border=0 -P footer=off"

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
dim()  { printf '  \033[2m%s\033[0m\n' "$1"; }
m()    { awk "BEGIN{printf \"%.1f\", $1/1000000}"; }

# Read the config ONCE, from the running container, because that is the copy in force.
#
# The first version of this script read $REPO/src/.../appsettings.json and silently fell
# back to built-in defaults, because the host checkout holds only docker-compose, scripts
# and sql -- src/ is never deployed, the images come from ghcr. So a script written
# specifically to avoid carrying its own copy of a threshold was carrying its own copy of
# every threshold, and saying "appsettings.json" while doing it. The repo path is kept as
# a fallback for running this from a dev machine, and it is labelled differently, because
# a checkout can be any commit whereas the container is what is running.
RAW=""
if RAW=$(docker exec "$BOT" cat /app/appsettings.json 2>/dev/null) && [ -n "$RAW" ]; then
    SRC="$BOT:/app/appsettings.json -- deployed"
elif [ -f "$CFG_REPO" ]; then
    RAW=$(cat "$CFG_REPO")
    SRC="repo checkout -- NOT necessarily what is deployed"
else
    SRC="BUILT-IN FALLBACK -- no config found, these numbers may be wrong"
fi

# One numeric key out of the Signal block. Empty when absent, which the caller reads as
# "not deployed" rather than substituting a guess.
cfg() {
    [ -n "$RAW" ] || return 0
    printf '%s' "$RAW" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\([0-9.]*\).*/\1/p" | head -1
}

RATIO=$(cfg RatioMinimum);        RATIO=${RATIO:-2.1}
MINVOL=$(cfg RatioMinVolumeUsd);  MINVOL=${MINVOL:-3000000}
SETTLE=$(cfg RatioSettleMinutes); SETTLE=${SETTLE:-3}
HIVOL=$(cfg RatioHighVolumeUsd);  HIVOL=${HIVOL:-0}

render() {
clear 2>/dev/null || true
bold "flow -- $SYMBOL -- $(date -u '+%Y-%m-%d %H:%M:%S') UTC"
echo

# -- 1. The scorer's own answer -----------------------------------------------
bold "1. EACH STRATEGY'S VERDICT (the scorer that actually ran -- authoritative)"
# One row per strategy, from strategy_verdicts. This read bot_config.last_verdict_*,
# which is a single set of columns written from inside the loop over active_strategies --
# so with two strategies each overwrote the other every cycle and this section showed
# whichever ran last, permanently hiding the other. See sql/034.
if [ "$($PSQL -c "SELECT to_regclass('public.strategy_verdicts') IS NOT NULL")" = "t" ]; then
    $PSQLT <<SQL
SELECT v.strategy,
       CASE WHEN c.active_strategies @> ARRAY[v.strategy] THEN 'trading' ELSE 'idle' END AS state,
       v.code,
       date_trunc('second', now() - v.updated_at)::text AS age,
       left(v.detail, 96) AS detail
FROM strategy_verdicts v, bot_config c
WHERE c.id = 1 ORDER BY v.strategy;
SQL
    dim "a strategy marked idle is registered but not in active_strategies -- its verdict is stale"
else
    warn_line="  strategy_verdicts is missing -- apply sql/034. Falling back to bot_config."
    printf '\033[33m%s\033[0m\n' "$warn_line"
    $PSQLT <<SQL
SELECT coalesce(last_verdict_code,'(none)') AS code,
       coalesce(date_trunc('second', now()-last_verdict_at)::text,'never') AS age,
       left(coalesce(last_verdict_detail,''),96) AS detail
FROM bot_config WHERE id=1;
SQL
fi
row=$($PSQL <<SQL
SELECT enabled, (SELECT count(*) FROM bot_trades WHERE status='OPEN') FROM bot_config WHERE id=1;
SQL
)
IFS="|" read -r en open <<< "$row"
printf '  %s open position(s)\n' "$open"
if [ "$en" = "f" ]; then
    printf '  \033[31mFROZEN\033[0m  bot_config.enabled = false -- this will not update.\n'
fi
echo

# -- 2. Closed buckets, scored by the deployed rule ---------------------------
bold "2. CLOSED BUCKETS -- every input the rule has"
if [ "${HIVOL%%.*}" != "0" ]; then
    dim "ratio >= ${RATIO}:1 on >= \$$(m "$MINVOL")M, OR any ratio on >= \$$(m "$HIVOL")M  ·  settle ${SETTLE} min  [$SRC]"
else
    dim "ratio >= ${RATIO}:1 on >= \$$(m "$MINVOL")M  ·  settle ${SETTLE} min  [$SRC]"
fi
$PSQLT <<SQL
WITH b AS (
  SELECT bucket_start,
         sum(buy_volume_usd)  AS buy,
         sum(sell_volume_usd) AS sell,
         sum(buy_volume_usd + sell_volume_usd) AS vol
  FROM flow_bars_15m WHERE symbol='$SYMBOL' GROUP BY bucket_start),
g AS (SELECT to_timestamp(floor(extract(epoch from now())/900)*900) AS open_bar)
SELECT to_char(bucket_start,'HH24:MI') AS bucket,
       '\$' || to_char(vol/1e6,'FM990.00')  || 'M' AS volume,
       '\$' || to_char(buy/1e6,'FM990.00')  || 'M' AS buy,
       '\$' || to_char(sell/1e6,'FM990.00') || 'M' AS sell,
       to_char(greatest(buy,sell)/nullif(least(buy,sell),0),'FM990.00') || ':1 ' ||
         CASE WHEN buy >= sell THEN 'B' ELSE 'S' END AS imbalance,
       to_char((buy-sell)/nullif(vol,0),'S0.000') AS ofi,
       CASE
         WHEN $HIVOL > 0 AND vol >= $HIVOL
           THEN '>>> ' || CASE WHEN buy >= sell THEN 'LONG' ELSE 'SHORT' END || '  (high volume)'
         WHEN vol < $MINVOL
           THEN 'VOLUME_TOO_THIN  (short \$' || to_char(($MINVOL-vol)/1e6,'FM990.00') || 'M)'
         WHEN greatest(buy,sell)/nullif(least(buy,sell),0) < $RATIO
           THEN 'RATIO_TOO_LOW  (needs \$' ||
                to_char((least(buy,sell) - greatest(buy,sell)/$RATIO)/1e6,'FM990.00') ||
                'M off the quiet side)'
         ELSE '>>> ' || CASE WHEN buy >= sell THEN 'LONG' ELSE 'SHORT' END
       END AS would_fire
FROM b, g
WHERE bucket_start < g.open_bar AND bucket_start > now() - interval '3 hours'
ORDER BY bucket_start DESC;
SQL
echo

# -- 3. The live bucket, shown apart because the scorer must not read it ------
bold "3. THE BUCKET STILL FORMING (the scorer does not read this)"
$PSQLT <<SQL
WITH b AS (
  SELECT bucket_start,
         sum(buy_volume_usd)  AS buy,
         sum(sell_volume_usd) AS sell,
         sum(buy_volume_usd + sell_volume_usd) AS vol
  FROM flow_bars_15m WHERE symbol='$SYMBOL' GROUP BY bucket_start),
g AS (SELECT to_timestamp(floor(extract(epoch from now())/900)*900) AS open_bar)
SELECT to_char(bucket_start,'HH24:MI') AS bucket,
       '\$' || to_char(vol/1e6,'FM990.00') || 'M' AS so_far,
       to_char(greatest(buy,sell)/nullif(least(buy,sell),0),'FM990.00') || ':1 ' ||
         CASE WHEN buy >= sell THEN 'B' ELSE 'S' END AS imbalance,
       to_char((buy-sell)/nullif(vol,0),'S0.000') AS ofi,
       to_char(bucket_start + interval '15 min','HH24:MI') AS closes,
       to_char(bucket_start + interval '15 min' + interval '$SETTLE min','HH24:MI') AS tradeable
FROM b, g WHERE bucket_start >= g.open_bar;
SQL
dim "partial and still moving -- a live bucket turned +0.17 OFI into -0.081 an hour later"
echo

# -- 4. Per venue ------------------------------------------------------------
bold "4. PER VENUE, newest closed bucket"
$PSQLT <<SQL
WITH g AS (SELECT to_timestamp(floor(extract(epoch from now())/900)*900) AS open_bar),
l AS (SELECT max(bucket_start) AS bs FROM flow_bars_15m, g
      WHERE symbol='$SYMBOL' AND bucket_start < g.open_bar)
SELECT f.exchange AS venue,
       '\$' || to_char((f.buy_volume_usd+f.sell_volume_usd)/1e6,'FM990.00') || 'M' AS volume,
       to_char(greatest(f.buy_volume_usd,f.sell_volume_usd)
               / nullif(least(f.buy_volume_usd,f.sell_volume_usd),0),'FM990.00') || ':1 ' ||
         CASE WHEN f.buy_volume_usd >= f.sell_volume_usd THEN 'B' ELSE 'S' END AS imbalance,
       to_char((f.buy_volume_usd-f.sell_volume_usd)
               / nullif(f.buy_volume_usd+f.sell_volume_usd,0),'S0.000') AS ofi,
       f.buy_count + f.sell_count AS prints,
       '\$' || to_char(greatest(f.max_buy_usd,f.max_sell_usd),'FM999,999,999') AS largest_print
FROM flow_bars_15m f, l
WHERE f.symbol='$SYMBOL' AND f.bucket_start = l.bs
ORDER BY f.exchange;
SQL
dim "the rule reads the AGGREGATE. These are here to spot one venue, or one print,"
dim "carrying the whole imbalance -- which the FlowRatio path does not check for itself,"
dim "because it never calls Prepare and so never applies MaxConcentration."
echo

# -- 5. Whether a signal could become a trade even if one fired ---------------
#
# A signal is only half the question. Four nested position caps sit between an
# actionable verdict and an order, all checked before the gate, and from the outside a
# bot blocked by one of them looks identical to a bot with nothing to trade -- which is
# the confusion this whole script exists to remove.
#
# Caps are read via row_to_json so this section still prints against a database that has
# not had sql/032 and sql/033 applied. Naming a missing column aborts the statement;
# a missing json key is NULL, which prints as "not migrated" and is the more useful
# answer during a half-finished deploy.
bold "5. POSITION BOOK -- can a signal even become a trade?"
$PSQLT <<SQL
WITH o AS (SELECT COALESCE(strategy,'?') s, COALESCE(entry_path,'(unmarked)') p, side
           FROM bot_trades WHERE status='OPEN'),
c AS (SELECT row_to_json(b) j FROM bot_config b WHERE b.id=1),
l AS (SELECT (j->>'max_open_total')::int t, (j->>'max_open_trades_per_strategy')::int ps,
             (j->>'max_open_per_side')::int pd, (j->>'max_open_high_volume')::int hv FROM c),
r AS (
  SELECT 1 ord, 'all strategies' lim, (SELECT count(*) FROM o)::int cur, t cap FROM l
  UNION ALL SELECT 2, 'per strategy',
    COALESCE((SELECT max(n) FROM (SELECT count(*) n FROM o GROUP BY s) x),0)::int, ps FROM l
  UNION ALL SELECT 3, 'per strategy per side',
    COALESCE((SELECT max(n) FROM (SELECT count(*) n FROM o GROUP BY s,side) x),0)::int, pd FROM l
  UNION ALL SELECT 4, 'high-volume waiver',
    (SELECT count(*) FROM o WHERE p='HIGH_VOLUME')::int, hv FROM l)
SELECT lim AS limit_on, cur AS open, COALESCE(cap::text,'--') AS cap,
       CASE WHEN cap IS NULL THEN 'not migrated'
            WHEN cap = 0 THEN 'disabled'
            WHEN cur >= cap THEN 'BLOCKING'
            ELSE 'room' END AS status
FROM r ORDER BY ord;
SQL

# The four rows above are NESTED, and the table cannot say so on its own.
#
# Read as a table they look like four independent budgets that add up, and that reading
# is wrong in a way that matters: they are four gates in series, each one able only to
# refuse, none able to grant a slot. The high-volume waiver in particular is a SUBSET of
# the per-strategy count -- a waiver entry occupies one of the strategy's own positions,
# never a fifth one.
#
# This is not hypothetical. On 2026-09-12 the table was read as "2x2 plus one free
# high-volume slot = 6" -- exactly the arithmetic ACCOUNT_LIMIT_INERT exists to
# contradict, and read that way by someone who knew the code. A monitor whose layout
# leads a reader to the wrong model of the system is the failure this script was written
# to remove, one level up.
#
# So the reachable maximum is computed and printed. It is the only number on this screen
# that answers "how many positions can actually exist at once".
POS=$($PSQL <<SQL
WITH c AS (SELECT row_to_json(b) j FROM bot_config b WHERE b.id=1),
l AS (SELECT (j->>'max_open_total')::int t, (j->>'max_open_trades_per_strategy')::int ps,
             (j->>'max_open_per_side')::int pd,
             COALESCE(json_array_length(j->'active_strategies'), 1) n FROM c),
-- Per side is the tighter bound whenever it is set, because there are only two sides.
e AS (SELECT n, t, ps, pd,
             LEAST(ps, CASE WHEN pd > 0 THEN pd * 2 ELSE ps END) per_strat FROM l),
f AS (SELECT n, t, ps, pd, per_strat, n * per_strat reachable FROM e)
SELECT concat_ws(' ', n, ps, pd, COALESCE(t,0), reachable,
                 CASE WHEN t > 0 THEN LEAST(t, reachable) ELSE reachable END)
FROM f WHERE per_strat IS NOT NULL;
SQL
) || POS=""

if [ -n "$POS" ]; then
    read -r fNS fPS fPD fTOT fREACH fCAP <<<"$POS"
    dim "reachable maximum $fCAP  --  $fNS strateg(ies) x min(per-strategy $fPS, per-side $fPD x 2 sides)"
    if [ "${fTOT:-0}" -gt 0 ] && [ "${fTOT:-0}" -gt "${fREACH:-0}" ]; then
        dim "all-strategies cap $fTOT can never bind -- this is what [Risk] ACCOUNT_LIMIT_INERT reports"
    fi
    dim "the high-volume waiver is a SUBSET of those $fCAP, never an extra slot"
fi

$PSQLT <<SQL
SELECT COALESCE(strategy,'?') AS strategy, COALESCE(entry_path,'(unmarked)') AS via,
       side, to_char(opened_at,'MM-DD HH24:MI') AS opened,
       round(entry_price,3) AS entry, round(stop_price,3) AS stop, round(target_price,3) AS target
FROM bot_trades WHERE status='OPEN' ORDER BY opened_at;
SQL
$PSQLT <<SQL
SELECT active_strategies::text AS trading, enabled, paper_mode,
       use_dynamic_tp_sl AS dyn_tpsl, max_entries_per_day AS cap_per_day,
       cooldown_seconds AS cooldown_s
FROM bot_config WHERE id=1;
SQL
dim "slots are first come, first served and NOT reserved per strategy -- a rule that"
dim "signals often will hold them against one that signals rarely"
}

if [ "${1:-}" = "-w" ]; then
    while true; do render; sleep 30; done
else
    render
fi
