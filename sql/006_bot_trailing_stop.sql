-- ============================================================
-- Migration 006: Add trailing stop support to bot_trades table
-- peak_price tracks the highest price since trade open (LONG)
-- Used by TradingBotService to evaluate trailing stop exits.
-- ============================================================

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS peak_price NUMERIC(20, 8);
