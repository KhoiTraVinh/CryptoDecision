-- ============================================================================
-- Price Alerts — user-configurable price threshold notifications
-- ============================================================================

CREATE TABLE IF NOT EXISTS price_alerts (
    id              BIGSERIAL       PRIMARY KEY,
    user_id         TEXT,                                   -- mobile app user (nullable for anonymous)
    symbol          TEXT            NOT NULL,               -- e.g. BTCUSDT
    condition       TEXT            NOT NULL,               -- ABOVE, BELOW
    target_price    NUMERIC(20,8)   NOT NULL,
    is_active       BOOLEAN         NOT NULL DEFAULT TRUE,
    is_triggered    BOOLEAN         NOT NULL DEFAULT FALSE,
    triggered_at    TIMESTAMPTZ,
    triggered_price NUMERIC(20,8),
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    note            TEXT                                    -- user note
);

CREATE INDEX IF NOT EXISTS idx_price_alerts_active
    ON price_alerts (symbol, is_active) WHERE is_active = TRUE;

CREATE TABLE IF NOT EXISTS alert_notifications (
    id              BIGSERIAL       PRIMARY KEY,
    alert_id        BIGINT          NOT NULL REFERENCES price_alerts(id),
    symbol          TEXT            NOT NULL,
    condition       TEXT            NOT NULL,
    target_price    NUMERIC(20,8)   NOT NULL,
    actual_price    NUMERIC(20,8)   NOT NULL,
    triggered_at    TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_alert_notifications_symbol
    ON alert_notifications (symbol, triggered_at DESC);
