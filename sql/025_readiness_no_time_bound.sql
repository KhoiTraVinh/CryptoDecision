-- ─────────────────────────────────────────────────────────────────────────────
-- 025: Drop the time window the repository does not have
--
-- 024 counted buckets inside a trailing 15-hour window, on the stated grounds that
-- "FlowBarRepository asks for 52 bars and widens the scan by 8 for gaps, so it looks
-- back 15 hours". That described an implementation that no longer exists.
--
-- FlowBarRepository.GetRecentAsync was changed to fetch the newest N buckets **per
-- venue by row count**:
--
--     ROW_NUMBER() OVER (PARTITION BY exchange ORDER BY bucket_start DESC) ... rn <= N
--
-- with no time predicate beyond excluding the bucket still in progress. The change was
-- deliberate: the baseline measures the *distribution* a venue's imbalance normally
-- has, which is a property of the venue rather than of the last day, and bounding it by
-- recency meant a gap in the history could not be stepped over — 56 perfectly usable
-- buckets sat in the table while the strategy refused for want of 48.
--
-- The view then kept the old bound, so it under-reported readiness in exactly the
-- situation the repository change existed to fix. Concretely: with seeded history two
-- days old plus three fresh buckets, the strategy has 59 buckets and is ready, while
-- this view reported 3 of 48 and an eleven-hour wait.
--
-- What genuinely must be fresh is the signal window — the newest few buckets the
-- verdict is computed from — and that is guarded separately by MaxBarAge (35 minutes)
-- on FlowBarSet.Age, surfacing as the FLOW_BARS_STALE abstain code. Freshness and
-- sufficiency are two different questions and this view now reports both without
-- conflating them.
--
-- Coupling, unchanged and still stated rather than solved:
--
--   bars_required  MUST equal  FlowStrategy:Signal:SignalBars + BaselineBars
--   fetch_cap      MUST equal  bars_required + SignalBars
--                              in src/CryptoDecision.BotService/appsettings.json
-- ─────────────────────────────────────────────────────────────────────────────

DROP VIEW IF EXISTS v_flow_signal_readiness;

CREATE VIEW v_flow_signal_readiness AS
WITH params AS (
    SELECT
        -- SignalBars 4 + BaselineBars 44.
        48                                                        AS bars_required,
        -- What the repository asks for: MinimumBars + SignalBars.
        52                                                        AS fetch_cap,
        -- How stale the newest closed bucket may be before the strategy refuses.
        -- Mirrors FlowStrategyOptions.MaxBarAge.
        INTERVAL '35 minutes'                                     AS max_bar_age,
        to_timestamp(floor(extract(epoch FROM now()) / 900) * 900) AS open_bucket
),
-- Closed buckets only, matching the repository: the bucket in progress is rewritten as
-- trades land in it, so scoring it means acting on a number that will have changed.
closed AS (
    SELECT f.symbol, f.exchange, f.bucket_start
    FROM flow_bars_15m f, params p
    WHERE f.bucket_start < p.open_bucket
),
-- Availability, with no time bound — the repository has none.
available AS (
    SELECT
        c.symbol,
        c.exchange,
        LEAST(COUNT(*), p.fetch_cap) AS bars_available,
        MAX(c.bucket_start)          AS newest_closed
    FROM closed c, params p
    GROUP BY c.symbol, c.exchange, p.fetch_cap
),
-- Kept as a data-health signal, not as the readiness criterion. The scorer counts
-- buckets and does not require them contiguous, so a gap costs a few rolling samples
-- that the median and MAD absorb — it does not reset the clock.
gapped AS (
    SELECT
        symbol, exchange, bucket_start,
        CASE
            WHEN LAG(bucket_start) OVER w IS NULL                           THEN 1
            WHEN bucket_start - LAG(bucket_start) OVER w > INTERVAL '1 hour' THEN 1
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
    SELECT DISTINCT ON (symbol, exchange) symbol, exchange, run_id
    FROM runs ORDER BY symbol, exchange, bucket_start DESC
),
run_length AS (
    SELECT c.symbol, c.exchange, COUNT(*) AS continuous_bars
    FROM runs r
    JOIN current_run c
      ON c.symbol = r.symbol AND c.exchange = r.exchange AND c.run_id = r.run_id
    GROUP BY c.symbol, c.exchange
)
SELECT
    a.symbol,
    a.exchange,
    a.bars_available,
    p.bars_required,
    l.continuous_bars,

    -- Both conditions the strategy actually applies, reported separately so a failure
    -- names itself: not enough history, or history that stopped arriving.
    (a.bars_available >= p.bars_required)                              AS enough_bars,
    (now() - (a.newest_closed + INTERVAL '15 minutes') <= p.max_bar_age) AS bars_fresh,
    (a.bars_available >= p.bars_required
     AND now() - (a.newest_closed + INTERVAL '15 minutes') <= p.max_bar_age) AS ready,

    GREATEST(p.bars_required - a.bars_available, 0)                    AS bars_short,
    (GREATEST(p.bars_required - a.bars_available, 0) * INTERVAL '15 minutes') AS eta,

    a.newest_closed,
    (now() - (a.newest_closed + INTERVAL '15 minutes'))               AS newest_bucket_age
FROM available a
JOIN run_length l ON l.symbol = a.symbol AND l.exchange = a.exchange
CROSS JOIN params p
ORDER BY a.symbol, a.exchange;

COMMENT ON VIEW v_flow_signal_readiness IS
    'Whether CrossVenueFlowStrategy can score. enough_bars counts closed buckets with no '
    'time bound, matching FlowBarRepository which fetches the newest N per venue by row '
    'count; bars_fresh checks the newest bucket against MaxBarAge. Both must hold. '
    'bars_required tracks SignalBars + BaselineBars in the BotService appsettings.json.';
