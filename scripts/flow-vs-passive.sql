-- Is the entry threshold excluding tradeable moves, or excluding noise?
--
-- The strategy only enters when aggressive taker imbalance is unusual. That is a
-- deliberate choice with a cost: a price move driven by PASSIVE repricing --
-- limit sellers marking down, bids pulled -- is invisible to it. SOL fell 2.7%
-- over three hours while taker flow was net BUYING on the two deepest venues,
-- and the bot correctly, by its own rules, did nothing.
--
-- So the question is not "did we miss a move" but "conditional on |z| being
-- below threshold, how much directional money was actually on the table?" If
-- forward returns in the direction flow pointed grow with |z|, the threshold is
-- doing real work. If they are flat across bands, the threshold is only cutting
-- trade count and the edge is elsewhere.
--
-- PROXY WARNING: standardisation here is trailing mean/stddev per venue, not the
-- scorer's median/MAD, because a windowed median is not available as a window
-- function. Band boundaries are therefore close to but not identical to the live
-- EnterZ. Use this to read the SHAPE across bands, never to set a threshold --
-- for that, run the backtester, which calls the real scorer.
\pset pager off

WITH v AS (
    SELECT exchange, bucket_start,
           (buy_volume_usd - sell_volume_usd)
             / NULLIF(buy_volume_usd + sell_volume_usd, 0) AS ofi,
           buy_volume_usd + sell_volume_usd                AS vol
    FROM flow_bars_15m
    WHERE symbol = 'SOLUSDT'
),
z AS (
    SELECT exchange, bucket_start, vol,
           (ofi - AVG(ofi)  OVER w) / NULLIF(STDDEV_SAMP(ofi) OVER w, 0) AS vz
    FROM v
    WINDOW w AS (PARTITION BY exchange ORDER BY bucket_start
                 ROWS BETWEEN 44 PRECEDING AND 1 PRECEDING)
),
agg AS (
    -- Volume-weighted across venues, the same shape the scorer aggregates in.
    SELECT bucket_start,
           SUM(vz * vol) / NULLIF(SUM(vol), 0) AS az,
           COUNT(*)                            AS venues
    FROM z WHERE vz IS NOT NULL
    GROUP BY bucket_start
),
px AS (
    -- Close of the bucket, and of the bucket 4 and 16 ahead (1h and 4h).
    SELECT to_timestamp(floor(extract(epoch FROM open_time) / 900) * 900) AS bucket,
           (array_agg(close_price ORDER BY open_time DESC))[1]            AS close
    FROM klines_1m WHERE symbol = 'SOLUSDT'
    GROUP BY 1
),
fwd AS (
    SELECT p.bucket, p.close,
           LEAD(p.close,  4) OVER (ORDER BY p.bucket) AS c1h,
           LEAD(p.close, 16) OVER (ORDER BY p.bucket) AS c4h
    FROM px p
),
j AS (
    SELECT a.az, a.venues,
           100 * (f.c1h - f.close) / f.close AS r1h,
           100 * (f.c4h - f.close) / f.close AS r4h
    FROM agg a JOIN fwd f ON f.bucket = a.bucket_start
    WHERE f.c1h IS NOT NULL
)
SELECT CASE
           WHEN ABS(az) <  0.5 THEN '|z| < 0.5        (bot ignores)'
           WHEN ABS(az) <  1.0 THEN '0.5 <= |z| < 1.0 (bot ignores)'
           WHEN ABS(az) <  1.5 THEN '1.0 <= |z| < 1.5 (bot ignores)'
           ELSE                     '|z| >= 1.5       (bot ENTERS)'
       END                                                       AS band,
       COUNT(*)                                                  AS buckets,
       -- Signed return in the direction flow pointed. This is the number that
       -- matters: positive means following flow paid, negative means fighting it
       -- would have paid, near zero means flow said nothing at that magnitude.
       ROUND(AVG(SIGN(az) * r1h)::NUMERIC, 3)                    AS mean_1h_with_flow,
       ROUND(AVG(SIGN(az) * r4h)::NUMERIC, 3)                    AS mean_4h_with_flow,
       -- How much movement existed at all, regardless of direction. A band with
       -- big |return| but ~zero signed return is money that was there and was
       -- not predictable from flow.
       ROUND(AVG(ABS(r1h))::NUMERIC, 3)                          AS mean_abs_1h,
       ROUND(STDDEV_SAMP(r1h)::NUMERIC, 3)                       AS sd_1h
FROM j
GROUP BY 1
ORDER BY 1;
