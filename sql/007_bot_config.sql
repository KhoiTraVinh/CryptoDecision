-- Bot configuration table (single row, upsert pattern).
-- ApiService writes is_running + options here.
-- BotService polls every 10s and starts/stops accordingly.

CREATE TABLE IF NOT EXISTS bot_config (
    id                            INT           PRIMARY KEY DEFAULT 1,
    is_running                    BOOLEAN       NOT NULL DEFAULT false,
    paper_mode                    BOOLEAN       NOT NULL DEFAULT true,
    symbol                        TEXT          NOT NULL DEFAULT 'BTCUSDT',
    strategy_list                 TEXT          NOT NULL DEFAULT 'GRID,MOMENTUM',
    capital_usd                   NUMERIC(12,2) NOT NULL DEFAULT 1000,
    position_pct                  NUMERIC(6,4)  NOT NULL DEFAULT 0.01,
    take_profit_pct               NUMERIC(6,4)  NOT NULL DEFAULT 0.003,
    stop_loss_pct                 NUMERIC(6,4)  NOT NULL DEFAULT 0.05,
    max_open_trades_per_strategy  INT           NOT NULL DEFAULT 5,
    cooldown_seconds              INT           NOT NULL DEFAULT 120,
    grid_step_pct                 NUMERIC(6,4)  NOT NULL DEFAULT 0.005,
    use_trailing_stop             BOOLEAN       NOT NULL DEFAULT true,
    trailing_stop_pct             NUMERIC(6,4)  NOT NULL DEFAULT 0.015,
    updated_at                    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- Ensure default row exists (BotService reads row id=1 on startup)
INSERT INTO bot_config (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
