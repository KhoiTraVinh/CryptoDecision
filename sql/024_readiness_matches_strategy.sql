-- ─────────────────────────────────────────────────────────────────────────────
-- 024: Make the readiness view agree with the strategy
--
-- v_flow_signal_readiness asserted 100 bars while the deployed strategy needed 48.
-- That is not a cosmetic gap: the view is the instrument an operator watches to know
-- when the bot can start scoring, and at 100 it would have reported "not ready" for
-- roughly thirteen hours after the strategy was in fact ready.
--
-- The requirement had drifted across three places:
--
--   appsettings.json      BaselineBars 96   (the code default)
--   docker-compose.yml    BaselineBars 44   (an env override, invisible to SQL)
--   this view             100               (SignalBars + BaselineBars, hardcoded)
--
-- Two of those are now one: the compose override is gone and appsettings carries 44,
-- so the value lives in the repository where a change to it and a change to this file
-- land in the same commit and the same review.
--
-- That still leaves two copies. There is no way for SQL to read the service's
-- configuration, so the coupling is stated rather than solved:
--
--   bars_required MUST equal FlowStrategy:Signal:SignalBars
--                          + FlowStrategy:Signal:BaselineBars
--                          in src/CryptoDecision.BotService/appsettings.json
--
-- and `lookback` must match FlowBarRepository's window, which is
-- (MinimumBars + SignalBars + 8) × 15 minutes.
--
-- BaselineBars is 44 rather than the 96 the code defaults to, and that is a
-- concession with a stated cost: 44 bars gives 41 rolling samples to estimate the
-- median and MAD from, against 93 at the default. Forty-one is thin — a noisier sigma
-- means noisier z-scores — but it is what the available history supports, and the
-- alternative was eleven more hours of ingestion before the strategy could score
-- anything. The backtester sweeps BaselineBars; this should be revisited from that
-- sweep rather than left because it worked.
-- ─────────────────────────────────────────────────────────────────────────────

DROP VIEW IF EXISTS v_flow_signal_readiness;

CREATE VIEW v_flow_signal_readiness AS
WITH params AS (
    SELECT
        -- SignalBars 4 + BaselineBars 44. See the header: this tracks appsettings.json.
        48                                                        AS bars_required,
        -- FlowBarRepository asks for MinimumBars + SignalBars = 52 bars and widens the
        -- scan by 8 for gaps, so it looks back (52 + 8) × 15 minutes = 15 hours.
        INTERVAL '15 hours'                                       AS lookback,
        to_timestamp(floor(extract(epoch FROM now()) / 900) * 900) AS open_bucket
),
-- Closed buckets only, matching FlowBarRepository.GetRecentAsync: the bucket in
-- progress is rewritten as trades land in it, so the strategy discards it.
closed AS (
    SELECT f.symbol, f.exchange, f.bucket_start
    FROM flow_bars_15m f, params p
    WHERE f.bucket_start < p.open_bucket
),
gapped AS (
    SELECT
        symbol, exchange, bucket_start,
        CASE
            WHEN LAG(bucket_start) OVER w IS NULL                               THEN 1
            -- One hour, not one bucket. The scorer counts buckets in its window and
            -- does not require them consecutive, so a single print-free quarter hour
            -- must not reset this and report a full day's wait that is not real.
            WHEN bucket_start - LAG(bucket_start) OVER w > INTERVAL '1 hour'     THEN 1
            ELSE 0
        END AS starts_run
    FROM closed
    WINDOW w AS (PARTITION BY symbol, exchange ORDER BY bucket_start)
),
runs AS (
    SELECT symbol, exchange, bucket_start,
           SUM(starts_run) OVER (PARTITION BY symbol, exchange ORDER BY bucket_start) AS run_id
    FROM gapped
),
current_run AS (
    SELECT DISTINCT ON (symbol, exchange)
        symbol, exchange, run_id, bucket_start AS newest_closed
    FROM runs
    ORDER BY symbol, exchange, bucket_start DESC
),
run_length AS (
    SELECT c.symbol, c.exchange, c.newest_closed, COUNT(*) AS continuous_bars
    FROM runs r
    JOIN current_run c
      ON c.symbol = r.symbol AND c.exchange = r.exchange AND c.run_id = r.run_id
    GROUP BY c.symbol, c.exchange, c.newest_closed
),
in_window AS (
    SELECT c.symbol, c.exchange, COUNT(*) AS bars_in_window
    FROM closed c, params p
    WHERE c.bucket_start >= now() - p.lookback
    GROUP BY c.symbol, c.exchange
)
SELECT
    l.symbol,
    l.exchange,
    w.bars_in_window,
    l.continuous_bars,
    p.bars_required,
    w.bars_in_window >= p.bars_required                        AS ready,
    GREATEST(p.bars_required - w.bars_in_window, 0)            AS bars_short,
    -- Derived from the current run, not the shortfall. While an older run is still
    -- expiring out of the trailing window, arrivals and expiries roughly cancel: four
    -- hours of healthy ingestion once moved the count from 28 to 30 while a
    -- shortfall-based ETA claimed the wait had shrunk by four hours.
    (GREATEST(p.bars_required - l.continuous_bars, 0) * INTERVAL '15 minutes') AS eta,
    (now() + GREATEST(p.bars_required - l.continuous_bars, 0) * INTERVAL '15 minutes')
                                                               AS ready_at,
    l.newest_closed,
    -- What FLOW_BARS_STALE measures. Sits between 0 and 15 minutes on a healthy feed.
    (now() - (l.newest_closed + INTERVAL '15 minutes'))        AS newest_bucket_age
FROM run_length l
JOIN in_window w ON w.symbol = l.symbol AND w.exchange = l.exchange
CROSS JOIN params p
ORDER BY l.symbol, l.exchange;

COMMENT ON VIEW v_flow_signal_readiness IS
    'Whether CrossVenueFlowStrategy has enough closed buckets to score. bars_required '
    'tracks FlowStrategy:Signal SignalBars + BaselineBars in the BotService '
    'appsettings.json — change one and change the other in the same commit.';
