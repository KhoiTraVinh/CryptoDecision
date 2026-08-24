DROP VIEW IF EXISTS v_flow_signal_readiness;

-- ─────────────────────────────────────────────────────────────────────────────
-- 021: Signal readiness
--
-- v_flow_bar_coverage answers "what is in the table", measured from the first
-- bucket ever written. That is the wrong question once the history has a gap in it:
-- it reported 24% completeness while the live feed was perfectly healthy, because it
-- was averaging in a day the stack spent switched off.
--
-- The strategy asks a narrower question — how many buckets exist inside the window
-- the scorer actually reads. This answers that one, so "why is nothing trading" is a
-- single select rather than a log dig.
--
-- Closed buckets only, matching FlowBarRepository.GetRecentAsync. The bucket in
-- progress is rewritten every couple of minutes as trades land in it, so the
-- strategy discards it — scoring a one-third-complete window means acting on an
-- imbalance that will have changed by the time it closes. A first version of this
-- view counted it, which put the count one ahead of the strategy's and made the
-- staleness column report a negative age for a bucket that has not happened yet.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE OR REPLACE VIEW v_flow_signal_readiness AS
WITH params AS (
    -- Mirrors FlowStrategyOptions defaults: 4 signal bars + 96 baseline bars, and
    -- FlowBarRepository fetches (bars + 8) × 15 minutes back.
    SELECT
        100                                                            AS bars_required,
        -- 28 hours, not 27: the strategy asks for MinimumBars + SignalBars = 104 bars
        -- and FlowBarRepository widens that by 8 for gaps, so it scans
        -- (104 + 8) x 15 minutes = 28 hours. A view meant to agree with the strategy
        -- has to use the strategy's window; an hour of drift here is an hour of
        -- disagreement about whether it is ready.
        INTERVAL '28 hours'                                            AS lookback,
        -- Left edge of the bucket currently in progress. Same 15-minute epoch floor
        -- the aggregation function and the repository both use.
        to_timestamp(floor(extract(epoch FROM now()) / 900) * 900)      AS open_bucket
),
counted AS (
    SELECT
        f.symbol,
        f.exchange,
        COUNT(DISTINCT f.bucket_start) AS bars_in_window,
        MAX(f.bucket_start)            AS newest_closed
    FROM flow_bars_15m f, params p
    WHERE f.bucket_start >= now() - p.lookback
      AND f.bucket_start <  p.open_bucket
    GROUP BY f.symbol, f.exchange
)
SELECT
    c.symbol,
    c.exchange,
    c.bars_in_window,
    p.bars_required,
    c.bars_in_window >= p.bars_required             AS ready,
    GREATEST(p.bars_required - c.bars_in_window, 0) AS bars_short,
    -- Buckets close every 15 minutes, so the shortfall converts directly to a wait.
    (GREATEST(p.bars_required - c.bars_in_window, 0) * INTERVAL '15 minutes') AS eta,
    c.newest_closed,
    -- Age of the newest *closed* bucket, which is what FLOW_BARS_STALE measures.
    -- Sits between 0 and 15 minutes on a healthy feed; anything larger means
    -- ingestion or aggregation has stopped.
    (now() - (c.newest_closed + INTERVAL '15 minutes')) AS newest_bucket_age
FROM counted c, params p
ORDER BY c.symbol, c.exchange;

COMMENT ON VIEW v_flow_signal_readiness IS
    'Whether CrossVenueFlowStrategy has enough closed buckets in the window it '
    'actually reads, per venue, with an ETA for the shortfall. Read this rather than '
    'v_flow_bar_coverage when asking why no entries are happening.';
