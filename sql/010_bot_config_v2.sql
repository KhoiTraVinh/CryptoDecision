-- Bot config v2: breakeven stop, dynamic TP/SL
ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS use_breakeven_stop     BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS breakeven_trigger_pct  NUMERIC NOT NULL DEFAULT 0.005;
ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS use_dynamic_tp_sl      BOOLEAN NOT NULL DEFAULT FALSE;
