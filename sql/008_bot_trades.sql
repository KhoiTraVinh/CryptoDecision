-- ── Trading Bot — Database Migration ──────────────────────────────────────────
-- Run once on the CryptoDecision PostgreSQL database.
-- Safe to re-run: uses IF NOT EXISTS.

CREATE TABLE IF NOT EXISTS bot_trades (
    id           BIGSERIAL        PRIMARY KEY,
    symbol       TEXT             NOT NULL,
    side         TEXT             NOT NULL DEFAULT 'BUY',
    entry_price  NUMERIC(18, 8)   NOT NULL,
    exit_price   NUMERIC(18, 8),
    quantity     NUMERIC(18, 8)   NOT NULL,
    notional_usd NUMERIC(10, 4)   NOT NULL,
    pnl_usd      NUMERIC(10, 4),
    pnl_pct      NUMERIC(8,  6),
    status       TEXT             NOT NULL DEFAULT 'OPEN',
    opened_at    TIMESTAMPTZ      NOT NULL DEFAULT NOW(),
    closed_at    TIMESTAMPTZ,
    close_reason TEXT,
    strategy     TEXT             NOT NULL DEFAULT 'UNKNOWN',
    peak_price   NUMERIC(20, 8)
);

-- Indexes for common queries
CREATE INDEX IF NOT EXISTS idx_bot_trades_symbol    ON bot_trades(symbol);
CREATE INDEX IF NOT EXISTS idx_bot_trades_status    ON bot_trades(status);
CREATE INDEX IF NOT EXISTS idx_bot_trades_opened_at ON bot_trades(opened_at DESC);

-- Comment
COMMENT ON TABLE bot_trades IS
    'Paper and real trading bot trade history. side=BUY only (spot scalping). '
    'status: OPEN | CLOSED | STOPPED. close_reason: TP | SL | TIMEOUT | MANUAL.';
