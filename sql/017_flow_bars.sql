-- ─────────────────────────────────────────────────────────────────────────────
-- 017: Per-venue 15-minute order-flow bars
--
-- Why this table exists
-- ---------------------
-- Every order-flow read in this stack was a live scan of `trades` bounded to the
-- trailing hour (MomentumRepository.GetMultiTimeframeAsync, PredictionService's
-- get_timeframe_flows). That shape has three consequences that together made the
-- signal unverifiable:
--
--   1. The lookback could never exceed an hour, so there was no baseline to judge
--      "is this imbalance unusual?" against. The strategy compared a raw buy ratio
--      to a hardcoded 62, a number with no units and no way to be right.
--   2. The windows were cumulative (5m nested in 15m nested in 1h), so weighting
--      them as three "confirmations" weighted one number three times.
--   3. Nothing was persisted, so no backtest was possible. Every threshold in the
--      bot was chosen by hand and deployed against real funds unvalidated.
--
-- Persisting disjoint 15-minute buckets per venue fixes all three. Buckets sum
-- cleanly into any longer horizon (4 bars = 1h, 16 = 4h) precisely because they do
-- not overlap, a trailing baseline of any length is a cheap range scan, and the
-- backtester replays exactly the rows the live bot reads.
--
-- Why 15 minutes, and why aligned to the clock
-- --------------------------------------------
-- Order-flow imbalance measured at quarter-hour boundaries has documented
-- predictive content over the following 4-12 hours, peaking around 8-12h, and the
-- effect is specific to the 15-minute calendar grid rather than to 15-minute
-- spacing in general — it tracks the schedules other algorithms run on. The same
-- imbalance at 1m and 5m boundaries shows no such effect. Aligning to
-- date_trunc-style 15-minute boundaries is therefore part of the signal, not a
-- storage convenience.
--
-- What is stored, and what is deliberately not
-- --------------------------------------------
-- Raw sums, not ratios. A ratio cannot be re-aggregated: averaging four bars'
-- buy ratios is not the hour's buy ratio unless the bars carry equal volume, which
-- they never do. Storing sums means every derived horizon is an honest sum.
--
-- No is_whale here. That flag is a generated column on `trades` hardcoded to
-- quote_qty > 100000, which for SOL at ~$92 requires a single print above 1,087
-- SOL and is therefore identically false — it silently zeroed the whale term in
-- the heuristic, pinned one of XGBoost's four features to a constant, and dropped
-- the strategy's 15% whale component every cycle. Concentration is captured
-- instead as max_buy_usd / max_sell_usd, which is scale-free: it answers "was this
-- bar one participant or many?" without a magic threshold that has to be re-tuned
-- per symbol.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS flow_bars_15m (
    symbol          VARCHAR(20)   NOT NULL,
    exchange        VARCHAR(16)   NOT NULL,

    -- Left edge of the bucket, aligned to :00/:15/:30/:45 UTC.
    bucket_start    TIMESTAMPTZ   NOT NULL,

    -- Aggressive (taker) flow, split by which side crossed the spread.
    -- is_buyer_maker = false means the taker was the buyer lifting the offer;
    -- that is the convention every normaliser in IngestionService maps onto.
    buy_volume_usd  NUMERIC(30, 8) NOT NULL,
    sell_volume_usd NUMERIC(30, 8) NOT NULL,
    buy_count       INT            NOT NULL,
    sell_count      INT            NOT NULL,

    -- Largest single print on each side. Concentration = max / total tells you
    -- whether the bar's imbalance is a crowd or one order, which is the difference
    -- between a signal and an accident.
    max_buy_usd     NUMERIC(20, 8) NOT NULL,
    max_sell_usd    NUMERIC(20, 8) NOT NULL,

    -- Volume-weighted average price of the bucket, per venue. Used to measure
    -- cross-venue price dispersion without a second scan of `trades`.
    vwap            NUMERIC(20, 8) NOT NULL,

    computed_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),

    PRIMARY KEY (symbol, exchange, bucket_start)
);

-- The read pattern is always "this symbol, this time range, all venues" — the
-- trailing baseline and the backtester's replay are both range scans in that
-- shape. Venue is the trailing key so a single index serves both.
CREATE INDEX IF NOT EXISTS ix_flow_bars_symbol_bucket
    ON flow_bars_15m (symbol, bucket_start DESC, exchange);

COMMENT ON TABLE flow_bars_15m IS
    'Disjoint 15-minute aggressive-flow buckets per venue, clock-aligned. Sums '
    'rather than ratios so any longer horizon is an honest re-aggregation. Read '
    'by both the live strategy and the backtester through the same code path.';

COMMENT ON COLUMN flow_bars_15m.max_buy_usd IS
    'Largest single aggressive buy in the bucket. max/total is a scale-free '
    'concentration measure, replacing the hardcoded >100k USDT whale flag that '
    'never fired on SOL.';


-- ─────────────────────────────────────────────────────────────────────────────
-- Aggregation, as an idempotent upsert over a half-open time range.
--
-- Called two ways:
--   • Live, by ProcessorService every few minutes over the trailing few buckets,
--     so late-arriving trades are folded in. Re-running a bucket recomputes it
--     from `trades` rather than adding to it, which is what makes it safe to call
--     repeatedly and what makes "re-run the last hour" a valid repair.
--   • Once, over all history, to backfill before the first backtest.
--
-- Bounded to a range and driven off the partition key so PostgreSQL prunes to the
-- daily partitions the range touches instead of scanning the whole table.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE OR REPLACE FUNCTION upsert_flow_bars_15m(
    p_symbol TEXT,
    p_from   TIMESTAMPTZ,
    p_to     TIMESTAMPTZ
) RETURNS INT AS $$
DECLARE
    affected INT;
BEGIN
    INSERT INTO flow_bars_15m (
        symbol, exchange, bucket_start,
        buy_volume_usd, sell_volume_usd, buy_count, sell_count,
        max_buy_usd, max_sell_usd, vwap, computed_at
    )
    SELECT
        t.symbol,
        t.exchange,
        -- 15-minute floor. to_timestamp of the floored epoch is exact here and
        -- avoids the interval-arithmetic edge cases of date_trunc + modulo.
        to_timestamp(floor(extract(epoch FROM t.trade_time) / 900) * 900),

        COALESCE(SUM(t.quote_qty) FILTER (WHERE NOT t.is_buyer_maker), 0),
        COALESCE(SUM(t.quote_qty) FILTER (WHERE     t.is_buyer_maker), 0),
        -- Cast wraps the whole aggregate: FILTER binds to the aggregate call, so
        -- `COUNT(*)::INT FILTER (...)` is a syntax error rather than a precedence
        -- subtlety.
        (COUNT(*) FILTER (WHERE NOT t.is_buyer_maker))::INT,
        (COUNT(*) FILTER (WHERE     t.is_buyer_maker))::INT,
        COALESCE(MAX(t.quote_qty) FILTER (WHERE NOT t.is_buyer_maker), 0),
        COALESCE(MAX(t.quote_qty) FILTER (WHERE     t.is_buyer_maker), 0),

        -- Volume-weighted, guarded against a bucket whose quantities sum to zero.
        CASE WHEN SUM(t.quantity) > 0
             THEN SUM(t.price * t.quantity) / SUM(t.quantity)
             ELSE 0 END,

        now()
    FROM trades t
    WHERE t.symbol = p_symbol
      AND t.trade_time >= p_from
      AND t.trade_time <  p_to
    GROUP BY t.symbol, t.exchange,
             to_timestamp(floor(extract(epoch FROM t.trade_time) / 900) * 900)
    ON CONFLICT (symbol, exchange, bucket_start) DO UPDATE SET
        buy_volume_usd  = EXCLUDED.buy_volume_usd,
        sell_volume_usd = EXCLUDED.sell_volume_usd,
        buy_count       = EXCLUDED.buy_count,
        sell_count      = EXCLUDED.sell_count,
        max_buy_usd     = EXCLUDED.max_buy_usd,
        max_sell_usd    = EXCLUDED.max_sell_usd,
        vwap            = EXCLUDED.vwap,
        computed_at     = EXCLUDED.computed_at;

    GET DIAGNOSTICS affected = ROW_COUNT;
    RETURN affected;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION upsert_flow_bars_15m IS
    'Recompute flow_bars_15m for one symbol over [p_from, p_to) from `trades`. '
    'Idempotent: re-running a range replaces those buckets rather than adding to '
    'them, so it doubles as the repair path for late-arriving trades.';


-- ─────────────────────────────────────────────────────────────────────────────
-- Coverage view.
--
-- The first question any backtest has to answer is "how much data is there, and
-- from which venues?" — and it has to answer it before reporting a Sharpe ratio,
-- because a result computed over four days of one exchange is not a result. This
-- makes that question a single select rather than something the backtester
-- reimplements.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE OR REPLACE VIEW v_flow_bar_coverage AS
SELECT
    symbol,
    exchange,
    MIN(bucket_start)                                        AS first_bucket,
    MAX(bucket_start)                                        AS last_bucket,
    COUNT(*)                                                 AS bars,
    -- Buckets that should exist between the first and last, so a gap shows up as
    -- a completeness figure rather than having to be eyeballed.
    (EXTRACT(EPOCH FROM (MAX(bucket_start) - MIN(bucket_start))) / 900 + 1)::BIGINT
                                                             AS bars_expected,
    ROUND(
        COUNT(*)::NUMERIC
        / NULLIF(EXTRACT(EPOCH FROM (MAX(bucket_start) - MIN(bucket_start))) / 900 + 1, 0)
        * 100, 2)                                            AS completeness_pct
FROM flow_bars_15m
GROUP BY symbol, exchange;

COMMENT ON VIEW v_flow_bar_coverage IS
    'Per-venue span and completeness of flow_bars_15m. Read this before trusting '
    'any backtest output: cross-venue consensus is meaningless over a period when '
    'only one venue was ingesting.';
