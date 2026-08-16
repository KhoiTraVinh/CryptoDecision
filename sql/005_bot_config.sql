-- ──────────────────────────────────────────────────────────────────────────────
-- 005_bot_config.sql — Bot configuration / command table
-- The API writes commands (start/stop/config), the Bot Worker polls and executes.
-- ──────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS bot_config (
    id              SERIAL      PRIMARY KEY,
    enabled         BOOLEAN     NOT NULL DEFAULT FALSE,
    paper_mode      BOOLEAN     NOT NULL DEFAULT TRUE,
    symbol          TEXT        NOT NULL DEFAULT 'BTCUSDT',
    exchange        TEXT        NOT NULL DEFAULT 'BINANCE',
    active_strategies TEXT[]    NOT NULL DEFAULT ARRAY['GRID','MOMENTUM'],
    capital_usd     NUMERIC     NOT NULL DEFAULT 100,
    max_open_trades_per_strategy INT NOT NULL DEFAULT 5,
    position_pct    NUMERIC     NOT NULL DEFAULT 0.10,
    take_profit_pct NUMERIC     NOT NULL DEFAULT 0.003,
    stop_loss_pct   NUMERIC     NOT NULL DEFAULT 0.05,
    cooldown_seconds INT        NOT NULL DEFAULT 120,
    max_hold_minutes INT        NOT NULL DEFAULT 1440,
    daily_loss_limit_pct NUMERIC NOT NULL DEFAULT 0.15,
    eval_interval_seconds INT   NOT NULL DEFAULT 30,
    grid_step_pct   NUMERIC     NOT NULL DEFAULT 0.005,
    min_momentum_buy_ratio NUMERIC NOT NULL DEFAULT 0.65,
    min_buy_ratio_1h NUMERIC   NOT NULL DEFAULT 0.55,
    use_trailing_stop BOOLEAN   NOT NULL DEFAULT TRUE,
    trailing_stop_pct NUMERIC   NOT NULL DEFAULT 0.015,
    -- Worker heartbeat: bot worker updates this every eval cycle
    last_heartbeat  TIMESTAMPTZ,
    last_eval_at    TIMESTAMPTZ,
    open_trade_count INT        NOT NULL DEFAULT 0,
    total_trades    INT         NOT NULL DEFAULT 0,
    total_pnl_usd   NUMERIC    NOT NULL DEFAULT 0,
    win_count       INT         NOT NULL DEFAULT 0,
    loss_count      INT         NOT NULL DEFAULT 0,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed a single default row (singleton config)
INSERT INTO bot_config (id) VALUES (1) ON CONFLICT DO NOTHING;
