-- ─────────────────────────────────────────────────────────────────────────────
-- 016: Leverage and margin mode on bot_trades
--
-- The bot now trades USDT-margined perpetual swaps rather than spot. Two trades
-- with identical entry, exit and size are no longer comparable: at 3x one risked
-- a third of the margin the other did, and only one of them was anywhere near a
-- liquidation price. Without these columns that difference is unrecoverable from
-- the row, which makes any post-hoc read of the P&L series misleading.
--
-- Nullable, with no default: a NULL here means "not a leveraged trade" — every
-- pre-existing row, and every paper row.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS leverage    NUMERIC(6, 2),
    ADD COLUMN IF NOT EXISTS margin_mode TEXT;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'bot_trades_margin_mode_check'
    ) THEN
        ALTER TABLE bot_trades
            ADD CONSTRAINT bot_trades_margin_mode_check
            CHECK (margin_mode IS NULL OR margin_mode IN ('isolated', 'cross'));
    END IF;
END $$;

COMMENT ON COLUMN bot_trades.leverage IS
    'Leverage in force when the position was opened. NULL for spot and paper trades.';
COMMENT ON COLUMN bot_trades.margin_mode IS
    'isolated = loss capped at this position''s margin. cross = the whole account '
    'balance backs the position. NULL for spot and paper trades.';
