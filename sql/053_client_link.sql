-- ─────────────────────────────────────────────────────────────────────────────
-- 053: client_link — the customer's own OKX API key, for the mobile app
--
-- The customer creates the key themselves on OKX with read + trade and NO
-- withdraw, whitelists the server's egress IP, and sends it once through
-- POST /api/link. This table is where it lands.
--
-- One row, id = 1, same shape as bot_config: the service is single-tenant today
-- and a second row would silently never be read. See mobile/README.md.
--
-- What is stored and what is not
-- ------------------------------
-- secret_enc and passphrase_enc are AES-GCM ciphertext, keyed by
-- ClientApi__EncryptionKey which lives in the environment and never in this
-- database. A dump of this table alone does not yield a usable key.
--
-- api_key is stored in clear on purpose: it is not a secret by itself, it is
-- needed in the OK-ACCESS-KEY header on every request, and having it readable
-- is what lets an operator match a row against the key list on the OKX account
-- page when a customer asks which key is linked.
--
-- token_hash is SHA-256 of the bearer token handed to the handset. The token
-- itself is never stored, so this table cannot be used to impersonate the app.
--
-- Revocation
-- ----------
-- DELETE FROM client_link is the server-side kill switch, but it is not the
-- real one: the customer revoking the key on okx.com stops trading whatever
-- this server believes, and the app tells them so.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS client_link (
    id               INT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    api_key          TEXT        NOT NULL,
    secret_enc       TEXT        NOT NULL,
    passphrase_enc   TEXT        NOT NULL,
    permissions      TEXT        NOT NULL,
    okx_uid          TEXT,
    token_hash       TEXT        NOT NULL,
    amount_per_trade_usd NUMERIC(12, 2),
    linked_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at     TIMESTAMPTZ
);

COMMENT ON TABLE client_link IS
    'The customer''s own OKX API key for the mobile app. One row (id = 1): the '
    'service is single-tenant. Secrets are AES-GCM ciphertext under '
    'ClientApi__EncryptionKey, which is NOT in this database.';

COMMENT ON COLUMN client_link.token_hash IS
    'SHA-256 of the bearer token given to the handset. The token itself is never '
    'stored, so a dump of this table cannot impersonate the app.';

COMMENT ON COLUMN client_link.amount_per_trade_usd IS
    'Position size the customer chose in the app. NULL until they set one, in '
    'which case the server falls back to its own configured sizing.';

DO $$
BEGIN
    RAISE NOTICE '053: client_link ready (% row(s)).', (SELECT count(*) FROM client_link);
END $$;
