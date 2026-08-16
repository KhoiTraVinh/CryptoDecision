-- Migration 004: Add exchange column to trades table
-- PG 11+: ADD NOT NULL DEFAULT does NOT rewrite the table (O(1) even with billions of rows).
-- Existing rows get exchange = 'BINANCE' retroactively via the DEFAULT.

ALTER TABLE trades
    ADD COLUMN IF NOT EXISTS exchange VARCHAR(16) NOT NULL DEFAULT 'BINANCE';

-- Index for per-exchange queries and exchange-aware aggregations
CREATE INDEX IF NOT EXISTS ix_trades_exchange
    ON trades (exchange, symbol, trade_time DESC);
