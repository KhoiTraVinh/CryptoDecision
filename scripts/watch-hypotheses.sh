#!/usr/bin/env bash
# Watch the two open hypotheses. Meant to be run ON the EC2 host, or piped to it
# from the workstation so nothing is written to the host at all:
#
#     bash ~/cryptodecision/scripts/watch-hypotheses.sh
#     ssh -i CryptoDecision.pem ec2-user@<host> 'bash -s' < scripts/watch-hypotheses.sh
#
# H34 is LIVE (ratchet giveback 0.40 -> 0.25, deployed 2026-10-06 13:33 UTC).
# H33 is STAGED (exit-review cadence 1h -> 30m, held behind EXIT_REVIEW_EVERY).
#
# The order here is the order the decision rules impose: H33's abort conditions are
# checked BEFORE any P&L, because "the cadence bought Ollama's queue rather than the
# model's judgement" is a reject regardless of what the money did. Everything that
# must be true is asked directly rather than inferred from a green container.

set -uo pipefail
cd "$(dirname "$0")/.." 2>/dev/null || cd ~/cryptodecision 2>/dev/null || true

PSQLI="docker exec -i postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -qtA"
# -c form must NOT read stdin. See the note above run_sql().
PSQLC="docker exec postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -qtA"

# H34 went live at this instant. Overridable so the same script can judge a later
# window without being edited.
SINCE="${H34_SINCE:-2026-10-06 13:33:23+00}"
TARGET_N="${H34_TARGET_N:-25}"

fails=0; warns=0
ok()    { printf '  \033[32mOK\033[0m    %s\n' "$1"; }
warn()  { printf '  \033[33mWARN\033[0m  %s\n' "$1"; warns=$((warns + 1)); }
fail()  { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; fails=$((fails + 1)); }
title() { printf '\n\033[1m%s\033[0m\n' "$1"; }
note()  { printf '        %s\n' "$1"; }

# Never `docker logs | grep -q` here: grep -q exits on first match, docker logs dies
# of SIGPIPE, pipefail reports the whole pipeline failed, and a pattern that IS
# present tests as absent. grep -c consumes all input. Same trap as health.sh.
log_count() { docker logs "$1" --since "$2" 2>&1 | grep -c -- "$3" || true; }

printf '\033[1mHypothesis watch — %s\033[0m\n' "$(date -u +'%Y-%m-%d %H:%M:%S UTC')"
note "H34 window opened $SINCE"

# ------------------------------------------------- 1  H33 abort conditions
title "1. H33 abort conditions (checked before any P&L)"

# `docker logs --since 24h` cannot see further back than the container has existed,
# and a deploy RECREATES the container rather than restarting it. So the first run
# after a deploy would report "0 in 24h" off eight minutes of log and read as
# reassurance. Say how much log there actually is, and stop calling it 24h when it
# is not.
BOT_UP=$(docker inspect -f '{{.State.StartedAt}}' bot 2>/dev/null)
BOT_AGE=$(( ( $(date -u +%s) - $(date -u -d "$BOT_UP" +%s 2>/dev/null || echo 0) ) / 60 ))
if [ "$BOT_AGE" -lt 1440 ]; then
    WINDOW="${BOT_AGE}m (container recreated $(date -u -d "$BOT_UP" +'%H:%M' 2>/dev/null) UTC — log does not reach back further)"
else
    WINDOW="24h"
fi
note "log window: $WINDOW"

NE=$(log_count bot 24h 'not evaluated this cycle')
UN=$(log_count bot 24h 'Unavailable')
EF=$(log_count bot 24h 'Exit review call failed')
EA=$(log_count bot 24h 'empty answer')
CB=$(log_count bot 24h 'exceeded its')

[ "$NE" -eq 0 ] && ok "'not evaluated this cycle': 0 in $WINDOW" \
                || fail "'not evaluated this cycle': $NE in $WINDOW — H33 REJECT condition"
[ "$((UN + EF + EA))" -eq 0 ] && ok "exit review Unavailable/failed/empty: 0 in $WINDOW" \
                || fail "exit review unavailable $UN / failed $EF / empty $EA — H33 REJECT condition"
[ "$CB" -eq 0 ] && ok "cycle budget exceeded: 0 in $WINDOW" \
                || fail "cycle budget exceeded: $CB in $WINDOW"

# ------------------------------------------------- 2  cadence actually running
title "2. Exit-review cadence — what is CONFIGURED and what is OBSERVED"

BANNER=$(docker logs bot 2>&1 | grep -oE 'EARLY EXIT: after [0-9]+h and then every [0-9]+h' | tail -1)
[ -n "$BANNER" ] && note "configured: $BANNER" || warn "no EARLY EXIT banner found in the log"

RATCH=$(docker logs bot 2>&1 | grep -oE 'gives back [0-9]+ %' | tail -1)
if [ "$RATCH" = "gives back 25 %" ]; then
    ok "ratchet banner: $RATCH  (H34 applied)"
else
    fail "ratchet banner reads '$RATCH' — expected 'gives back 25 %'. H34 did NOT take."
fi

# Observed spacing between consecutive reviews of the same trade. This is the line
# that tells you whether the cadence is 1h or 30m without trusting the config.
docker logs bot --since 48h 2>&1 \
  | grep -oE '"@t":"[^"]{19}[^"]*","@mt":"\[ExitReview\] Trade \{Id\}[^"]*","@l?r?"?[^}]*"Id":[0-9]+' \
  | sed -E 's/"@t":"([^"]{19})[^"]*".*"Id":([0-9]+)/\1 trade \2/' > /tmp/_rv 2>/dev/null || true

if [ -s /tmp/_rv ]; then
    note "last reviews (UTC):"
    tail -6 /tmp/_rv | while read -r line; do note "  $line"; done
else
    # Fall back to the decision lines, which are Information level and always present.
    docker logs bot --since 48h 2>&1 | grep -oE '"@t":"[^"]{19}' | sed 's/"@t":"//' > /tmp/_all || true
    N=$(docker logs bot --since 48h 2>&1 | grep -c 'ExitReview\] Trade' || true)
    note "exit-review decisions in 48h: $N"
fi
rm -f /tmp/_rv /tmp/_all

# Latency: brief logged, then decision logged. Only present when Debug is on.
LAT=$(docker logs bot --since 48h 2>&1 | grep -oE '"@t":"[^"]{19}[^"]*"\[ExitReview\]' | wc -l || true)
note "review log lines seen in 48h: $LAT (brief+decision pairs)"

# ------------------------------------------------- 3  H34 progress
title "3. H34 — ratchet giveback 0.25, judged at $TARGET_N closed trades"

$PSQLI <<SQL 2>/dev/null | while IFS='|' read -r n wins pnl meanr; do
SELECT count(*), count(*) FILTER (WHERE pnl_usd > 0),
       coalesce(round(sum(pnl_usd),4),0), coalesce(round(sum(pnl_usd)/0.60,3),0)
FROM bot_trades
WHERE status='CLOSED' AND mode='LIVE' AND opened_at > '$SINCE';
SQL
    if [ "${n:-0}" -eq 0 ]; then
        note "no trades opened since the deploy yet — nothing to judge"
    else
        note "closed since deploy: $n / $TARGET_N   wins $wins   P&L \$$pnl   total ${meanr}R"
    fi
done

# Capture ratio on winners — the mechanism check. 60.5% before, KEEP needs >= 70%.
CAP=$($PSQLI <<SQL 2>/dev/null
WITH t AS (SELECT * FROM bot_trades
           WHERE status='CLOSED' AND mode='LIVE' AND pnl_usd > 0 AND opened_at > '$SINCE'),
x AS (SELECT t.id, t.side, t.entry_price, t.pnl_pct,
             max(k.high_price) hi, min(k.low_price) lo
      FROM t JOIN klines_1m k
        ON k.open_time >= t.opened_at AND k.open_time <= t.closed_at
      GROUP BY 1,2,3,4)
SELECT coalesce(round(avg(
   (pnl_pct*100) / nullif(CASE WHEN side='LONG' THEN (hi-entry_price)/entry_price
                               ELSE (entry_price-lo)/entry_price END*100,0))::numeric*100,1),-1)
FROM x;
SQL
)
CAP=$(echo "$CAP" | tr -d ' ')
if [ -z "$CAP" ] || [ "$CAP" = "-100.0" ] || [ "$CAP" = "-1" ]; then
    note "capture ratio: no winners since deploy yet (baseline 60.5%, KEEP needs >= 70%)"
elif awk "BEGIN{exit !($CAP >= 70)}"; then
    ok "capture ratio on winners: ${CAP}%  (was 60.5%, KEEP needs >= 70%)"
else
    note "capture ratio on winners: ${CAP}%  (was 60.5%, KEEP needs >= 70% — not there yet)"
fi

# Win/loss asymmetry — baseline was 1.23:1 against.
$PSQLI <<SQL 2>/dev/null | while IFS='|' read -r aw al ratio; do
SELECT coalesce(round(avg(pnl_pct*100) FILTER (WHERE pnl_usd>0)::numeric,3),0),
       coalesce(round(avg(pnl_pct*100) FILTER (WHERE pnl_usd<=0)::numeric,3),0),
       coalesce(round((abs(avg(pnl_pct) FILTER (WHERE pnl_usd<=0))
                     / nullif(avg(pnl_pct) FILTER (WHERE pnl_usd>0),0))::numeric,3),0)
FROM bot_trades WHERE status='CLOSED' AND mode='LIVE' AND opened_at > '$SINCE';
SQL
    [ "${ratio:-0}" != "0" ] && note "avg win ${aw}%  avg loss ${al}%  ratio ${ratio}:1 against (was 1.23)"
done

# Exit mix — the failure mode shows up here before it shows up in P&L.
note "exit mix since deploy:"
$PSQLC -c "SELECT '          '||close_reason||'  n='||count(*)||'  R='||round(sum(pnl_usd)/0.60,3)
          FROM bot_trades WHERE status='CLOSED' AND mode='LIVE' AND opened_at > '$SINCE'
          GROUP BY close_reason ORDER BY 1;" 2>/dev/null

# ------------------------------------------------- 4  errors
title "4. Errors and fatals, last hour"
for svc in bot processor ingestion; do
    n=$(docker logs "$svc" --since 1h 2>&1 | grep -c '"@l":"\(Error\|Fatal\)"' || true)
    if [ "${n:-0}" -eq 0 ]; then
        ok "$svc: clean"
    else
        # Post-only entries that did not fill are logged at Error and are not faults.
        po=$(docker logs "$svc" --since 1h 2>&1 | grep -c 'without filling and was cancelled' || true)
        if [ "$n" -eq "$po" ]; then
            ok "$svc: $n Error line(s), all post-only no-fill (expected, not a fault)"
        else
            fail "$svc: $n Error/Fatal in the last hour ($po are post-only no-fill)"
            docker logs "$svc" --since 1h 2>&1 | grep '"@l":"\(Error\|Fatal\)"' \
              | grep -v 'without filling and was cancelled' | tail -3 | cut -c1-220 \
              | while read -r l; do note "$l"; done
        fi
    fi
done

# ------------------------------------------------- 5  loop alive
title "5. Loop"
$PSQLC -c "SELECT '        last cycle '||round(extract(epoch from (now()-last_eval_at)))||'s ago, '||
          'open '||open_trade_count||', paper_mode='||paper_mode FROM bot_config;" 2>/dev/null

printf '\n'
if [ "$fails" -gt 0 ]; then
    printf '\033[31m%d FAIL, %d WARN — a decision rule may have tripped, read above.\033[0m\n' "$fails" "$warns"
    exit 1
fi
printf '\033[32m0 FAIL, %d WARN — both hypotheses still running cleanly.\033[0m\n' "$warns"
