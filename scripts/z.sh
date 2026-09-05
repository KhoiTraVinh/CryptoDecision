#!/usr/bin/env bash
# Show the flow signal's z right now, per venue and in aggregate.
#
#     bash ~/cryptodecision/scripts/z.sh          once
#     bash ~/cryptodecision/scripts/z.sh -w       every 30s until Ctrl-C
#
# Two numbers are printed and they are NOT the same thing:
#
#   1. The bot's own verdict, from bot_config. This is the scorer — the real
#      arithmetic, with the participation floors and the venue-agreement rule
#      applied. It is authoritative, and it is also the only one that can be
#      stale: the loop stops forming verdicts while a position is open (it is at
#      the per-strategy limit) and while the bot is disabled.
#
#   2. A reconstruction from flow_bars_15m, computed here in SQL. This is what
#      makes a live reading possible when the bot is not scoring. It follows the
#      same formula — 4-bucket OFI, 44-bucket baseline excluding the signal
#      window, MAD x 1.4826 — but it does NOT apply the volume, print-count or
#      concentration floors, so a venue this script shows as leaning may be one
#      the scorer would have excluded.
#
# Print both, always, and label which is which. A single number here would become
# a second source of truth for the one quantity this repository has already had
# drift three times, and the reconstruction is the half that is allowed to be wrong.
set -euo pipefail

SYMBOL="${SYMBOL:-SOLUSDT}"
PSQL="docker exec -i postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -qtA -P pager=off"
# Aligned, with headers, for the tables. The -qtA one above is for reading single values.
PSQLT="docker exec -i postgres psql -U ${POSTGRES_USER:-crypto} -d ${POSTGRES_DB:-crypto} -P pager=off -P border=0 -P footer=off"

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
dim()  { printf '  \033[2m%s\033[0m\n' "$1"; }

render() {
clear 2>/dev/null || true
bold "z — $SYMBOL — $(date -u '+%Y-%m-%d %H:%M:%S') UTC"
echo

# ── 1. The scorer's own answer ────────────────────────────────────────────────
bold "1. THE BOT'S VERDICT (the scorer — authoritative)"
row=$($PSQL <<SQL
SELECT enabled, coalesce(last_verdict_code,'(none)'), coalesce(last_verdict_z::text,'—'),
       coalesce(last_verdict_agree::text,'—'), coalesce(last_verdict_venues::text,'—'),
       coalesce(date_trunc('second', now()-last_verdict_at)::text,'never'),
       coalesce(left(last_verdict_detail,150),'')
FROM bot_config WHERE id=1;
SQL
)
IFS="|" read -r en code z agree venues age detail <<< "$row"
printf '  %-28s z=%-9s %s/%s agree   %s old\n' "$code" "$z" "$agree" "$venues" "$age"
if [ -n "$detail" ]; then dim "$detail"; fi
if [ "$en" = "f" ]; then
    printf '  \033[31mFROZEN\033[0m  bot_config.enabled = false — this will not update.\n'
fi
echo

# ── 2. Reconstruction, so there is a live number even when the bot is not scoring
bold "2. RECONSTRUCTED FROM flow_bars_15m (approximate — no participation floors)"
$PSQLT <<SQL
WITH agg AS (
  SELECT bucket_start, sum(buy_volume_usd) buy, sum(sell_volume_usd) sell
  FROM flow_bars_15m WHERE symbol='$SYMBOL' GROUP BY bucket_start),
roll AS (
  SELECT bucket_start, sum(buy) OVER w b4, sum(sell) OVER w s4, count(*) OVER w n4
  FROM agg WINDOW w AS (ORDER BY bucket_start ROWS BETWEEN 3 PRECEDING AND CURRENT ROW)),
ofi AS (
  SELECT bucket_start, CASE WHEN b4+s4>0 THEN ((b4-s4)/(b4+s4))::numeric ELSE 0 END ofi
  FROM roll WHERE n4=4)
SELECT to_char(o.bucket_start,'MM-DD HH24:MI') AS bucket_utc,
       to_char(o.ofi,'S0.000') AS ofi,
       CASE WHEN b.sigma>0.005
            THEN to_char(round(((o.ofi-b.med)/b.sigma)::numeric,2),'S0.00') ELSE 'n/a' END AS z,
       CASE WHEN b.sigma>0.005 AND abs((o.ofi-b.med)/b.sigma) >= 1.0 THEN '<<< past ±1.00' ELSE '' END AS flag
FROM ofi o LEFT JOIN LATERAL (
  SELECT m.med, percentile_cont(0.5) WITHIN GROUP (ORDER BY abs(s.ofi-m.med))*1.4826 sigma
  FROM (SELECT o2.ofi FROM ofi o2
        WHERE o2.bucket_start <= o.bucket_start - interval '60 minutes'
          AND o2.bucket_start >  o.bucket_start - interval '720 minutes') s
  CROSS JOIN (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY o3.ofi) med FROM ofi o3
              WHERE o3.bucket_start <= o.bucket_start - interval '60 minutes'
                AND o3.bucket_start >  o.bucket_start - interval '720 minutes') m
  GROUP BY m.med) b ON true
WHERE o.bucket_start > now() - interval '3 hours'
ORDER BY o.bucket_start DESC;
SQL
dim "newest row is the bucket still forming — it moves until the quarter hour closes"
echo

# ── 3. Per venue, because consensus is what actually decides ──────────────────
bold "3. PER VENUE, newest closed bucket (this is what decides consensus)"
$PSQLT <<SQL
WITH b AS (
  SELECT exchange, bucket_start, buy_volume_usd bv, sell_volume_usd sv
  FROM flow_bars_15m WHERE symbol='$SYMBOL'
    AND bucket_start <= date_trunc('hour', now())
                      + floor(extract(minute FROM now())/15)*interval '15 minutes'
                      - interval '15 minutes'),
roll AS (
  SELECT exchange, bucket_start, sum(bv) OVER w b4, sum(sv) OVER w s4, count(*) OVER w n4
  FROM b WINDOW w AS (PARTITION BY exchange ORDER BY bucket_start ROWS BETWEEN 3 PRECEDING AND CURRENT ROW)),
ofi AS (
  SELECT exchange, bucket_start, b4+s4 AS vol,
         CASE WHEN b4+s4>0 THEN ((b4-s4)/(b4+s4))::numeric ELSE 0 END ofi
  FROM roll WHERE n4=4),
latest AS (SELECT exchange, max(bucket_start) bs FROM ofi GROUP BY exchange)
SELECT o.exchange AS venue,
       CASE WHEN st.sigma>0.005
            THEN to_char(round(((o.ofi-st.med)/st.sigma)::numeric,2),'S0.00') ELSE 'n/a' END AS z,
       to_char(o.ofi,'S0.000') AS ofi,
       '$'||to_char(o.vol/1e6,'FM990.00')||'M' AS volume,
       CASE WHEN st.sigma>0.005 AND abs((o.ofi-st.med)/st.sigma) >= 1.50 THEN 'AGREES (past ±1.50)' ELSE '' END AS flag
FROM ofi o JOIN latest l ON l.exchange=o.exchange AND l.bs=o.bucket_start
LEFT JOIN LATERAL (
  SELECT m.med, percentile_cont(0.5) WITHIN GROUP (ORDER BY abs(s.ofi-m.med))*1.4826 sigma
  FROM (SELECT o2.ofi FROM ofi o2 WHERE o2.exchange=o.exchange
          AND o2.bucket_start <= o.bucket_start - interval '60 minutes'
          AND o2.bucket_start >  o.bucket_start - interval '720 minutes') s
  CROSS JOIN (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY o3.ofi) med FROM ofi o3
              WHERE o3.exchange=o.exchange
                AND o3.bucket_start <= o.bucket_start - interval '60 minutes'
                AND o3.bucket_start >  o.bucket_start - interval '720 minutes') m
  GROUP BY m.med) st ON true
ORDER BY o.exchange;
SQL
echo
dim "in force: aggregate ±EnterZ, venue ±VenueAgreementZ, SufficientVenue may vouch alone"
dim "read them from src/CryptoDecision.BotService/appsettings.json — this script does not guess"
}

if [ "${1:-}" = "-w" ]; then
    while true; do render; sleep 30; done
else
    render
fi
