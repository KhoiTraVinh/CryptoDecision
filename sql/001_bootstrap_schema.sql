-- ============================================================
-- Migration 001: Bootstrap CryptoDecision schema
-- Managed at startup by ProcessorService.DatabaseInitializer
-- Run manually for initial DB setup or documentation purposes
-- ============================================================

-- Extensions
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- ── trades (RANGE partitioned by trade_time — daily partitions) ───────────────
CREATE TABLE IF NOT EXISTS trades (
    id              BIGSERIAL,
    symbol          VARCHAR(20)    NOT NULL,
    trade_id        BIGINT         NOT NULL,
    price           NUMERIC(20, 8) NOT NULL,
    quantity        NUMERIC(20, 8) NOT NULL,
    quote_qty       NUMERIC(20, 8) NOT NULL,
    is_buyer_maker  BOOLEAN        NOT NULL,
    is_whale        BOOLEAN        NOT NULL GENERATED ALWAYS AS (quote_qty > 100000) STORED,
    trade_time      TIMESTAMPTZ    NOT NULL,
    ingested_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
    PRIMARY KEY (id, trade_time)   -- partition key included in PK
) PARTITION BY RANGE (trade_time);

CREATE INDEX IF NOT EXISTS ix_trades_symbol    ON trades (symbol, trade_time DESC);
CREATE INDEX IF NOT EXISTS ix_trades_whale     ON trades (is_whale, trade_time DESC) WHERE is_whale = true;

-- Bootstrap 3-day partition window
DO $$
DECLARE
    d     DATE;
    name  TEXT;
    s     TEXT;
    e     TEXT;
BEGIN
    FOR i IN -1..2 LOOP
        d    := CURRENT_DATE + i;
        name := 'trades_' || TO_CHAR(d, 'YYYY_MM_DD');
        s    := TO_CHAR(d,       'YYYY-MM-DD');
        e    := TO_CHAR(d + 1,   'YYYY-MM-DD');
        EXECUTE FORMAT(
            'CREATE TABLE IF NOT EXISTS %I PARTITION OF trades
             FOR VALUES FROM (%L::timestamptz) TO (%L::timestamptz)',
            name, s, e
        );
        EXECUTE FORMAT(
            'CREATE INDEX IF NOT EXISTS ix_%s_symbol_time ON %I (symbol, trade_time DESC)',
            name, name
        );
    END LOOP;
END;
$$;

-- ── klines_1m ─────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS klines_1m (
    id              BIGSERIAL PRIMARY KEY,
    symbol          VARCHAR(20)    NOT NULL,
    open_time       TIMESTAMPTZ    NOT NULL,
    close_time      TIMESTAMPTZ    NOT NULL,
    open_price      NUMERIC(20, 8) NOT NULL,
    high_price      NUMERIC(20, 8) NOT NULL,
    low_price       NUMERIC(20, 8) NOT NULL,
    close_price     NUMERIC(20, 8) NOT NULL,
    volume          NUMERIC(30, 8) NOT NULL,
    quote_volume    NUMERIC(30, 8) NOT NULL,
    num_trades      INT            NOT NULL,
    ingested_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
    UNIQUE (symbol, open_time)
);
CREATE INDEX IF NOT EXISTS ix_klines_symbol_time ON klines_1m (symbol, open_time DESC);

-- ── daily_feature_table ───────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS daily_feature_table (
    id              BIGSERIAL      PRIMARY KEY,
    symbol          VARCHAR(20)    NOT NULL,
    date            DATE           NOT NULL,
    return_24h      NUMERIC(10, 6) NOT NULL,
    volatility      NUMERIC(10, 6) NOT NULL,
    volume_change   NUMERIC(10, 6) NOT NULL,
    whale_count     INT            NOT NULL,
    total_volume    NUMERIC(30, 8) NOT NULL,
    vwap            NUMERIC(20, 8) NOT NULL,
    computed_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
    UNIQUE (symbol, date)
);
CREATE INDEX IF NOT EXISTS ix_daily_feature_symbol_date ON daily_feature_table (symbol, date DESC);

-- ── prediction_table (written by ML service, read by ApiService) ──────────────
CREATE TABLE IF NOT EXISTS prediction_table (
    id              BIGSERIAL      PRIMARY KEY,
    symbol          VARCHAR(20)    NOT NULL,
    date            DATE           NOT NULL,
    direction       VARCHAR(10)    NOT NULL CHECK (direction IN ('UP', 'DOWN', 'NEUTRAL')),
    confidence      NUMERIC(5, 4)  NOT NULL CHECK (confidence BETWEEN 0 AND 1),
    model_version   VARCHAR(50)    NOT NULL,
    created_at      TIMESTAMPTZ    NOT NULL DEFAULT now(),
    UNIQUE (symbol, date, model_version)
);
CREATE INDEX IF NOT EXISTS ix_prediction_symbol_date ON prediction_table (symbol, date DESC);

-- ── Seed sample predictions for testing ──────────────────────────────────────
INSERT INTO prediction_table (symbol, date, direction, confidence, model_version)
VALUES
    ('BTCUSDT', CURRENT_DATE, 'UP',      0.7823, 'v1.0.0'),
    ('ETHUSDT', CURRENT_DATE, 'NEUTRAL', 0.5541, 'v1.0.0')
ON CONFLICT (symbol, date, model_version) DO NOTHING;
