-- ============================================================
-- Migration 005: Trade deduplication unique index
-- Protects against Kafka at-least-once redelivery inserting
-- the same (exchange, trade_id) twice. trade_time is required
-- because the trades table is RANGE-partitioned by trade_time.
-- ============================================================

-- CONCURRENTLY avoids locking the table during index build on existing data.
-- Remove CONCURRENTLY if running on a fresh DB with no data yet.
CREATE UNIQUE INDEX IF NOT EXISTS uq_trades_exchange_trade_id
    ON trades (exchange, trade_id, trade_time);
