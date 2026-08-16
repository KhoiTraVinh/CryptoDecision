-- ── App Users — track who is using the mobile app ──────────────────────────
CREATE TABLE IF NOT EXISTS app_users (
    id            SERIAL PRIMARY KEY,
    name          VARCHAR(100)  NOT NULL,
    device_id     VARCHAR(200)  NOT NULL UNIQUE,
    registered_at TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    last_seen     TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_app_users_registered ON app_users (registered_at DESC);
CREATE INDEX IF NOT EXISTS idx_app_users_last_seen  ON app_users (last_seen DESC);
