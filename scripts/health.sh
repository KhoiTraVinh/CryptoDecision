#!/usr/bin/env bash
# One-command health check, meant to be run ON the EC2 host:
#
#     bash ~/cryptodecision/scripts/health.sh
#
# Every bug on this bot that ever mattered left the containers healthy and the
# dashboard green. So this deliberately does not ask "is anything crashed?" --
# it asks, for each thing that must be true, whether it is actually true right
# now, and prints FAIL when it is not.
#
# Service expectations are DERIVED from the compose file, never hardcoded. A
# hardcoded list stops covering a service the moment someone adds one, and that
# mistake has already been made three times in this repo.

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

if docker compose version >/dev/null 2>&1; then C="docker compose"; else C="docker-compose"; fi
PSQL="docker exec -i postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -qtA"

# Compose normalises the project name to lowercase alphanumerics, so
# `CryptoDecision/` and `~/cryptodecision/` both become `cryptodecision`.
PROJECT=${COMPOSE_PROJECT_NAME:-$(basename "$PWD" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9_-')}

fails=0
warns=0

# Never `docker logs ... | grep -q` in this script. `grep -q` exits the instant
# it matches, `docker logs` then dies of SIGPIPE, and `pipefail` reports the
# whole pipeline as failed -- so a pattern that IS present tests as absent.
# Worse, it depends on whether the writer finished first, so it is not even
# deterministic: the 50110 check matched while the 42883 check silently did not.
# `grep -c` consumes all input, so there is no SIGPIPE to propagate.
log_has() { # service  since  pattern
    local n
    n=$(docker logs "$1" --since "$2" 2>/dev/null | grep -c -- "$3") || true
    [ "${n:-0}" -gt 0 ]
}
ok()    { printf '  \033[32mOK\033[0m    %s\n' "$1"; }
warn()  { printf '  \033[33mWARN\033[0m  %s\n' "$1"; warns=$((warns + 1)); }
fail()  { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; fails=$((fails + 1)); }
title() { printf '\n\033[1m%s\033[0m\n' "$1"; }

# --------------------------------------------------------------- 1 containers
title "1. Containers"
services=$($C config --services 2>/dev/null | sort)
if [ -z "$services" ]; then
    fail "compose config returned no services -- wrong directory, or compose is broken"
else
    for s in $services; do
        # Looked up by label rather than `compose ps -aq <service>`: that
        # subcommand answers "no such service" for a service that is in the
        # file but has no container, which is indistinguishable from a typo.
        cid=$(docker ps -aq \
            --filter "label=com.docker.compose.project=$PROJECT" \
            --filter "label=com.docker.compose.service=$s" | head -1)
        if [ -z "$cid" ]; then
            fail "$s: no container at all -- never started, or removed"
            continue
        fi
        # Pipe-delimited, not space-delimited. A service with no `restart:` key
        # (kafka) reports an EMPTY RestartPolicy.Name, which word-splitting
        # silently collapses -- every field after it shifts by one and, under
        # `set -u`, the loop dies mid-check having already printed OKs.
        info=$(docker inspect "$cid" --format '{{.State.Status}}|{{.State.ExitCode}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}|{{.HostConfig.RestartPolicy.Name}}|{{.RestartCount}}')
        IFS='|' read -r state code health policy restarts <<<"$info"

        # The restart policy CANNOT identify a one-shot job. Docker's default
        # for a service with no `restart:` key is the string "no" on Linux and
        # "" on Docker Desktop -- byte-identical to an explicit `restart: "no"`.
        # So kafka and postgres, which declare nothing, were reported as
        # "one-shot job still running" on EC2 while being long-running services.
        #
        # Only an explicit long-running policy is a reliable signal, so that is
        # the only thing treated as one. Everything else is ambiguous and
        # accepts either a healthy process or a clean exit -- but never a
        # non-zero exit, which is a failure under any reading.
        case "$policy" in
            unless-stopped | always | on-failure) longrunning=1 ;;
            *)                                    longrunning=0 ;;
        esac

        if [ "$longrunning" -eq 0 ]; then
            # db-check, db-migrate, kafka-init, ollama-init are SUPPOSED to be
            # exited; kafka and postgres are supposed to be running. An
            # exclusion list that forgets one is how the deploy workflow
            # reported "0 unhealthy" while db-check was failing.
            case "$state:$code:$health" in
                exited:0:*)       ok   "$s: completed (exit 0)" ;;
                exited:*:*)       fail "$s: exited $code -- docker logs $s" ;;
                running:*:healthy) ok  "$s: running, healthy" ;;
                running:*:none)   ok   "$s: running (no healthcheck defined)" ;;
                running:*:starting) warn "$s: running, health check still starting" ;;
                running:*:*)      fail "$s: running but health=$health -- docker logs $s" ;;
                *)                warn "$s: $state" ;;
            esac
            # Nothing brings these back after a host reboot or a Docker
            # restart, which is worth saying out loud for a bot meant to run
            # unattended -- see the note printed after this section.
            if [ "$state" = "running" ]; then
                norestart="${norestart:-} $s"
            fi
        else
            case "$state:$health" in
                running:healthy)  ok   "$s: running, healthy" ;;
                running:none)     ok   "$s: running (no healthcheck defined)" ;;
                # `starting` is the healthcheck's start_period, not a failure.
                # Calling it FAIL makes the script cry wolf on every deploy.
                running:starting) warn "$s: running, health check still starting -- re-run in a minute" ;;
                running:*)        fail "$s: running but health=$health -- docker logs $s" ;;
                *)                fail "$s: $state (exit $code) -- docker logs $s" ;;
            esac
            if [ "${restarts:-0}" -gt 0 ]; then
                warn "$s: has restarted $restarts time(s) -- crash-looping, or OOM-killed"
            fi
        fi
    done

    # A long-running container with no restart policy does not come back after
    # a host reboot or a Docker daemon restart. For a bot that is supposed to
    # be unattended, that is the difference between "running 24/7" and "running
    # until the next reboot", and nothing else in this output would reveal it.
    if [ -n "${norestart:-}" ]; then
        warn "no restart policy on:${norestart} -- these will NOT come back after a reboot"
        printf '        Everything downstream of them fails while looking like a bot problem.\n'
        printf '        Fix the running containers now, with no downtime and no data loss:\n'
        printf '            docker update --restart unless-stopped%s\n' "$norestart"
        printf '        The compose file needs the same change to survive a recreate.\n'
    fi
fi

# --------------------------------------------------------------- 2 the loop
title "2. Bot loop (this has stalled silently twice)"
row=$($PSQL -c "SELECT enabled, paper_mode, symbol, COALESCE(EXTRACT(EPOCH FROM now() - last_eval_at)::INT, -1), eval_interval_seconds FROM bot_config WHERE id = 1;" 2>/dev/null)
if [ -z "$row" ]; then
    fail "cannot read bot_config -- postgres unreachable, or the password does not match"
else
    IFS='|' read -r enabled paper symbol age interval <<<"$row"
    limit=$(( ${interval:-60} * 3 ))
    if [ "$enabled" != "t" ]; then
        fail "bot_config.enabled = false. Either you stopped it, or A CIRCUIT BREAKER TRIPPED."
        printf '        why:    docker logs bot --since 24h | grep -i "circuit breaker"\n'
        printf '        re-arm: docker exec postgres psql -U crypto -d crypto -c "UPDATE bot_config SET enabled = true WHERE id = 1;"\n'
    elif [ "$age" -lt 0 ]; then
        fail "enabled = true but last_eval_at is NULL -- the loop has never completed a cycle"
    elif [ "$age" -gt "$limit" ]; then
        fail "loop is STALLED: last cycle ${age}s ago, interval ${interval}s (limit ${limit}s)"
    else
        ok "loop alive: last cycle ${age}s ago (interval ${interval}s)"
    fi
    if [ "$paper" = "t" ]; then
        ok "paper_mode = true (no real money at risk)"
    else
        warn "paper_mode = FALSE -- this is trading real money"
    fi
    ok "symbol = $symbol"
fi

# --------------------------------------------------------------- 3 ingestion
title "3. Ingestion -- all three venues, right now"
# A venue that silently disconnects is exactly this bot's failure mode: two
# venues keep flowing, cross-venue consensus quietly becomes unreachable, and
# nothing anywhere logs an error.
ing=$($PSQL -c "SELECT exchange, COUNT(*) FROM trades WHERE trade_time > now() - INTERVAL '5 minutes' GROUP BY exchange;" 2>/dev/null)
live=0
for venue in BINANCE BYBIT OKX; do
    n=$(printf '%s\n' "$ing" | awk -F'|' -v v="$venue" '$1 == v { print $2 }')
    if [ -n "${n:-}" ] && [ "$n" -gt 0 ]; then
        ok "$venue: $n trades in the last 5 min"
        live=$((live + 1))
    else
        fail "$venue: NO trades in the last 5 min -- socket dead or unsubscribed"
    fi
done
if [ "$live" -lt 2 ]; then
    fail "fewer than 2 live venues: consensus needs 2, so the bot can never enter"
fi

# --------------------------------------------------------------- 4 readiness
title "4. Signal readiness (can the strategy score at all?)"
r=$($PSQL -c "SELECT bars_available, bars_required, enough_bars, bars_fresh, ready, eta, newest_bucket_age FROM v_flow_signal_readiness;" 2>/dev/null)
if [ -z "$r" ]; then
    warn "v_flow_signal_readiness returned no rows -- no flow bars for this symbol yet"
else
    IFS='|' read -r have need enough fresh ready eta bage <<<"$r"
    if [ "$ready" = "t" ]; then
        ok "ready: $have/$need bars, newest bar $bage old"
    else
        warn "NOT ready: $have/$need bars (enough=$enough fresh=$fresh), eta $eta, newest bar $bage old"
    fi
fi

# --------------------------------------------------------------- 5 results
title "5. Paper results, last 24h"
t=$($PSQL -c "SELECT COUNT(*), COALESCE(ROUND(SUM(pnl_usd), 4), 0), (COUNT(*) FILTER (WHERE pnl_usd > 0)), (COUNT(*) FILTER (WHERE status IN ('OPEN', 'PENDING'))) FROM bot_trades WHERE opened_at > now() - INTERVAL '24 hours';" 2>/dev/null)
IFS='|' read -r n pnl wins open <<<"${t:-0|0|0|0}"
ok "${n:-0} trades, ${wins:-0} winners, PnL ${pnl:-0} USDT, ${open:-0} still open"
if [ "${n:-0}" = "0" ]; then
    printf '        Zero trades is normal here -- the abstain reasons below say why.\n'
fi

# --------------------------------------------------------------- 6 abstains
title "6. Why it is not entering (abstain codes logged in 24h)"
# These are LOG LINES, not decisions. CrossVenueFlowStrategy logs an abstention
# when the code CHANGES and then only every 120th repeat, precisely so a stable
# refusal does not fill the log. So "1" here can mean one cycle or a thousand
# consecutive cycles with the same verdict -- read it as which reasons are in
# play, never as how many decisions were made.
counted=$(docker logs bot --since 24h 2>/dev/null \
    | grep -o '"Code":"[A-Z_]*"' | sed 's/.*://; s/"//g' \
    | sort | uniq -c | sort -rn | head -8 \
    | awk '{ printf "        %-34s %s\n", $2, $1 }')
if [ -n "$counted" ]; then
    printf '%s\n' "$counted"
else
    printf '        no abstain codes logged -- either it is entering, or the loop is not running\n'
fi

# --------------------------------------------------------------- 7 errors
title "7. Errors and fatals, last hour"
for svc in bot processor ingestion; do
    n=$(docker logs "$svc" --since 1h 2>/dev/null | grep -c '"@l":"\(Error\|Fatal\|Critical\)"')
    if [ "${n:-0}" -eq 0 ]; then
        ok "$svc: clean"
    else
        warn "$svc: $n error/fatal line(s)"
        # These are Serilog message TEMPLATES, so `{Code}` style placeholders
        # are literal here -- the values live in sibling JSON properties on the
        # same line. Templates are what you grep for; the drill-down below
        # prints the values.
        docker logs "$svc" --since 1h 2>&1 | grep '"@l":"\(Error\|Fatal\|Critical\)"' \
            | grep -o '"@mt":"[^"]*"' | sed 's/"@mt":"//; s/"$//' | sort -u | head -3 \
            | sed 's/^/          . /'
        printf '          full detail: docker logs %s --since 1h | grep '"'"'"@l":"Error"'"'"'\n' "$svc"
    fi
done
# 50110 is Fatal at startup but harmless until paper_mode = false, so it is
# reported as a warning with that caveat rather than as a failure.
if log_has bot 24h '50110'; then
    warn "OKX 50110: this host's IP is NOT on the API key whitelist."
    printf '        Harmless while paper_mode = true. Blocks every order the moment it is false.\n'
fi
if log_has bot 24h 'credentials authenticated'; then
    ok "OKX credentials authenticated"
fi

# Known post-deploy signature, called out by name so it is not mistaken for a
# real fault -- and not hidden either, because it has a cost.
#
# The startup order is postgres -> db-check -> processor -> db-migrate -> bot:
# the processor must create the base tables before sql/006 can alter them. So
# the processor's first aggregation pass runs BEFORE the SQL functions exist and
# fails with 42883. The worker then waits its full 60-minute interval before
# trying again, so each deploy silently costs up to an hour of flow bars.
# Matched as "42883: function", never as the bare number. Serilog timestamps
# carry seven fractional digits, so `18:32:40.9342883Z` and `18:43:17.1142883Z`
# both contain "42883" -- which made this fire on a host where the functions
# existed and aggregation was running normally. A health check that cries wolf
# is worse than no health check, because it teaches the operator to skim.
if log_has processor 1h '42883: function'; then
    up=$(docker inspect "$(docker ps -aq --filter "label=com.docker.compose.project=$PROJECT" --filter "label=com.docker.compose.service=processor" | head -1)" --format '{{.State.StartedAt}}' 2>/dev/null)
    warn "processor hit 42883 (SQL function missing) -- the known deploy race, started $up"
    printf '        Expected right after a deploy and it self-heals, but the next\n'
    printf '        aggregation attempt is a full interval away, so up to an hour of\n'
    printf '        flow bars is lost per deploy. If it is still appearing an hour\n'
    printf '        after the deploy, db-migrate did NOT apply -- check that first.\n'
fi

# --------------------------------------------------------------- 8 arming
title "8. Live-trading gates (all three, by name)"
# `Okx__Passphrase` was empty on this host for a whole deployment. The compose
# file reads ${OKX_PASSPHRASE:-} while the workflow wrote OKX_PASS=, and a
# defaulted substitution is silent -- so the credentials were simply absent,
# every other check was green, and the bot logged "credentials are not
# configured" once at startup where nobody was looking. Nothing would have
# surfaced it until the first real order failed to place.
for v in Okx__ApiKey Okx__ApiSecret Okx__Passphrase; do
    val=$(docker exec bot printenv "$v" 2>/dev/null || true)
    if [ -n "$val" ]; then
        ok "$v: set (${#val} chars)"
    else
        warn "$v: EMPTY -- no OKX order can be placed, whatever the other switches say"
    fi
done
# Three independent switches gate real money. Print all three together: knowing
# two of them is how you convince yourself you are safe when you are not, or
# that you are trading when you are not.
pm=$($PSQL -c "SELECT paper_mode FROM bot_config WHERE id = 1;" 2>/dev/null)
lt=$(docker exec bot printenv Okx__EnableLiveTrading 2>/dev/null || echo "unset")
dt=$(docker exec bot printenv Okx__DemoTrading 2>/dev/null || echo "unset")
printf '        bot_config.paper_mode   = %s   (true = internal simulation, OKX never called)\n' "${pm:-?}"
printf '        Okx__EnableLiveTrading  = %s   (the arm switch)\n' "${lt:-unset}"
printf '        Okx__DemoTrading        = %s   (true = OKX simulated endpoint, not real funds)\n' "${dt:-unset}"
if [ "${pm:-t}" = "f" ] && [ "${lt:-}" = "true" ] && [ "${dt:-}" = "false" ]; then
    warn "all three gates open: this is REAL MONEY"
else
    ok "real money is NOT reachable in this configuration"
fi

# --------------------------------------------------------------- 9 resources
title "9. Host resources"
df -h / | awk 'NR == 2 { u = $5 + 0; printf "  %s    disk %s used of %s\n", (u > 85 ? "\033[31mFAIL\033[0m" : (u > 70 ? "\033[33mWARN\033[0m" : "\033[32mOK\033[0m  ")), $5, $2 }'
if command -v free >/dev/null 2>&1; then
    free -m | awk 'NR == 2 { p = int(100 * ($2 - $7) / $2); printf "  %s    memory %d%% committed (%d MB available of %d MB)\n", (p > 92 ? "\033[31mFAIL\033[0m" : (p > 85 ? "\033[33mWARN\033[0m" : "\033[32mOK\033[0m  ")), p, $7, $2 }'
else
    printf '        (no `free` on this platform -- host memory not checked)\n'
fi
printf '\n  per-container memory:\n'
docker stats --no-stream --format '        {{.Name}}  {{.MemUsage}}  {{.MemPerc}}' 2>/dev/null | sort
rows=$($PSQL -c "SELECT COUNT(*) FROM trades;" 2>/dev/null)
size=$($PSQL -c "SELECT pg_size_pretty(pg_database_size(current_database()));" 2>/dev/null)
printf '        postgres: %s rows in trades, database %s -- 7-day retention should hold this steady\n' "${rows:-?}" "${size:-?}"

# --------------------------------------------------------------- verdict
printf '\n'
if [ "$fails" -gt 0 ]; then
    printf '\033[31m%d FAIL, %d WARN -- something is actually broken, see above.\033[0m\n' "$fails" "$warns"
    exit 1
fi
if [ "$warns" -gt 0 ]; then
    printf '\033[33m0 FAIL, %d WARN -- running, but read the warnings.\033[0m\n' "$warns"
    exit 0
fi
printf '\033[32mAll checks passed.\033[0m\n'
exit 0
