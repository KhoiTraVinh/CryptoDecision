-- ─────────────────────────────────────────────────────────────────────────────
-- 014: Live trading provenance on bot_trades
--
-- Until now every row in bot_trades was a simulation, so nothing recorded where
-- a trade happened or whether real money moved. Once OkxOrderEngine can place
-- real orders those two facts stop being implicit and become the most important
-- columns in the table: a paper row and a live row are not comparable, and a
-- live row without its exchange order id cannot be reconciled against the
-- exchange's own record.
--
-- mode is NOT NULL DEFAULT 'PAPER' deliberately. Backfilling existing rows to
-- 'PAPER' is correct — they were all simulations — and a row that somehow skips
-- the column lands on the safe side of the distinction rather than being
-- counted as real.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS mode           TEXT NOT NULL DEFAULT 'PAPER',
    ADD COLUMN IF NOT EXISTS exchange       TEXT NOT NULL DEFAULT 'BINANCE',
    ADD COLUMN IF NOT EXISTS entry_order_id TEXT,
    ADD COLUMN IF NOT EXISTS exit_order_id  TEXT,
    ADD COLUMN IF NOT EXISTS fee_usd        NUMERIC(18, 8);

-- mode is a two-valued flag, not free text. A typo here would silently reclassify
-- real trades as simulations in every P&L query that filters on it.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'bot_trades_mode_check'
    ) THEN
        ALTER TABLE bot_trades
            ADD CONSTRAINT bot_trades_mode_check CHECK (mode IN ('PAPER', 'LIVE'));
    END IF;
END $$;

-- Reconciliation lookup: "which trade is exchange order 21234567890?"
-- Partial index because only live rows carry an order id.
CREATE INDEX IF NOT EXISTS idx_bot_trades_entry_order_id
    ON bot_trades (entry_order_id) WHERE entry_order_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_bot_trades_mode ON bot_trades (mode);

COMMENT ON COLUMN bot_trades.mode IS
    'PAPER = simulated fill, no order left the process. LIVE = a real order was '
    'placed on the exchange and real funds moved.';
COMMENT ON COLUMN bot_trades.exchange IS
    'Venue the order was placed on (OKX), or the price source for a paper fill.';
COMMENT ON COLUMN bot_trades.entry_order_id IS
    'Exchange order id (OKX ordId) of the entry order. NULL for paper trades.';
COMMENT ON COLUMN bot_trades.exit_order_id IS
    'Exchange order id of the exit order. NULL for paper trades and open trades.';
COMMENT ON COLUMN bot_trades.fee_usd IS
    'Total fees actually charged by the exchange for this trade, in USD. Entry '
    'fee at open, entry + exit fee once closed. NULL for paper trades, whose fee '
    'is modelled rather than charged.';
