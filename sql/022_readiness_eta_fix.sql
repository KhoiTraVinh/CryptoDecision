-- ─────────────────────────────────────────────────────────────────────────────
-- 022: Fix the readiness ETA
--
-- 021 computed the wait as bars_short × 15 minutes. That assumes every new bucket is
-- a net gain of one, which is only true once the window holds nothing but continuous
-- data. While an older run is still expiring out of the trailing 28 hours, arrivals
-- and expiries roughly cancel: measured here, four hours of healthy ingestion moved
-- the count from 28 to 30 while the ETA column claimed the wait had shrunk by four
-- hours. An ETA that counts down faster than the thing it measures is worse than no
-- ETA — it invites someone to come back and find nothing changed.
--
-- The count that actually decides readiness is the current unbroken run of buckets,
-- because the strategy needs 100 of them inside the window and a run shorter than
-- that cannot fill it however long the historical tail hangs around. So the ETA is
-- derived from the run, not from the shortfall.
-- ─────────────────────────────────────────────────────────────────────────────

DROP VIEW IF EXISTS v_flow_signal_readiness;

CREATE VIEW v_flow_signal_readiness AS
WITH params AS (
    SELECT
        100                                                       AS bars_required,
        -- Matches FlowBarRepository: (MinimumBars + SignalBars + 8) × 15 minutes.
        INTERVAL '28 hours'                                       AS lookback,
        to_timestamp(floor(extract(epoch FROM now()) / 900) * 900) AS open_bucket
),
-- Closed buckets only, matching FlowBarRepository.GetRecentAsync: the bucket in
-- progress is rewritten as trades land in it, so the strategy discards it.
closed AS (
    SELECT f.symbol, f.exchange, f.bucket_start
    FROM flow_bars_15m f, params p
    WHERE f.bucket_start < p.open_bucket
),
-- Mark where each venue's series breaks. A bucket more than 15 minutes after its
-- predecessor starts a new run.
gapped AS (
    SELECT
        symbol, exchange, bucket_start,
        CASE
            WHEN LAG(bucket_start) OVER w IS NULL                                    THEN 1
            -- One hour, not one bucket. A run broken by every single dropped
            -- bucket is not a useful measure: the scorer counts buckets inside the
            -- window and does not require them to be consecutive, so one missing
            -- print-free quarter hour would reset this to zero and report a 25-hour
            -- wait that is not real. An hour-long hole is a genuine outage and does
            -- reset it.
            WHEN bucket_start - LAG(bucket_start) OVER w > INTERVAL '1 hour'          THEN 1
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
-- The run still in progress is the one containing each venue's newest closed bucket.
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
    -- Derived from the run rather than the shortfall, so it counts down in real time
    -- instead of racing ahead of it.
    (GREATEST(p.bars_required - l.continuous_bars, 0) * INTERVAL '15 minutes') AS eta,
    (now() + GREATEST(p.bars_required - l.continuous_bars, 0) * INTERVAL '15 minutes')
                                                               AS ready_at,
    l.newest_closed,
    (now() - (l.newest_closed + INTERVAL '15 minutes'))        AS newest_bucket_age
FROM run_length l
JOIN in_window w ON w.symbol = l.symbol AND w.exchange = l.exchange
CROSS JOIN params p
ORDER BY l.symbol, l.exchange;

COMMENT ON VIEW v_flow_signal_readiness IS
    'Whether CrossVenueFlowStrategy has enough closed buckets to score. bars_in_window '
    'is what the scorer counts; continuous_bars is the current unbroken run and is what '
    'the ETA is derived from, because a historical tail expiring out of the window makes '
    'the shortfall shrink far slower than arrivals suggest.';
