-- ─────────────────────────────────────────────────────────────────────────────
-- 057: one trading account per linked key, instead of exactly one account ever
--
-- Until now client_link carried `CHECK (id = 1)`: the database itself refused to
-- hold a second account. That was correct while the only key was the operator's,
-- and it became the wrong constraint the moment a customer was given the app --
-- their link would have silently OVERWRITTEN the operator's key, moved the bot
-- onto their account, and logged nothing that looked like a problem.
--
-- The model after this migration
-- ------------------------------
--   bot_config.id   IS the account id. A row existing is what PROVISIONS an
--                   account; there is no separate accounts table, because every
--                   per-account setting already lives here.
--   client_link.id  is that same account id. A row is the KEY attached to the
--                   account. An account can exist with no key linked yet.
--   bot_trades.account_id  says whose order it was.
--
-- Execution stays one process per account: each bot container is told its own
-- Bot__AccountId and behaves exactly as the single-account bot always has. This
-- migration is what lets two of them share a database without colliding.
--
-- Provisioning a second account is deliberate and NOT done here -- a migration
-- runs on every deploy, and quietly creating accounts is not something a deploy
-- should do. To add one, clone the operator's settings (39 columns, so clone
-- rather than retype):
--
--     CREATE TEMP TABLE a AS SELECT * FROM bot_config WHERE id = 1;
--     UPDATE a SET id = 2, enabled = false;
--     INSERT INTO bot_config SELECT * FROM a;
--
-- enabled = false on purpose: the account should be switched on by a person
-- after its container is running and its key is linked, not before.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── client_link: many rows ───────────────────────────────────────────────────
ALTER TABLE client_link DROP CONSTRAINT IF EXISTS client_link_id_check;

-- The default existed only to make the single row easy to upsert. With real ids
-- being assigned, an INSERT that forgets one should fail rather than land on
-- account 1 and take over the operator's slot.
ALTER TABLE client_link ALTER COLUMN id DROP DEFAULT;

-- Lookup is now by token ALONE (there is no longer a known id to pair it with),
-- so a duplicate token_hash would make authentication ambiguous. Unique makes
-- that unrepresentable rather than a thing to remember.
CREATE UNIQUE INDEX IF NOT EXISTS client_link_token_hash_key
    ON client_link (token_hash);

-- One OKX key belongs to one account. Without this, the same key linked twice
-- would have two accounts trading the same funds against each other, each
-- unaware of the other's positions.
CREATE UNIQUE INDEX IF NOT EXISTS client_link_api_key_key
    ON client_link (api_key);

COMMENT ON TABLE client_link IS
    'One row per trading account: the OKX key linked through the mobile app. '
    'id matches bot_config.id. Secrets are AES-GCM ciphertext under '
    'ClientApi__EncryptionKey, which is NOT in this database.';

-- ── bot_trades: whose order was it ───────────────────────────────────────────
-- DEFAULT 1 backfills every existing row to the operator, which is historically
-- true: account 1 is the only one that has ever traded.
ALTER TABLE bot_trades ADD COLUMN IF NOT EXISTS account_id INT NOT NULL DEFAULT 1;

-- Matches how it is read: the circuit breakers and the daily entry cap ask for
-- one account's recent trades, newest first.
CREATE INDEX IF NOT EXISTS bot_trades_account_opened_idx
    ON bot_trades (account_id, opened_at DESC);

COMMENT ON COLUMN bot_trades.account_id IS
    'bot_config.id of the account this order was placed for. Circuit breakers, '
    'the daily entry cap and open-position recovery are all scoped by it, so a '
    'customer''s losing streak cannot halt the operator''s bot or vice versa.';

-- ── bot_config: the row IS the account ───────────────────────────────────────
COMMENT ON COLUMN bot_config.id IS
    'The account id. Row 1 is the operator. A row existing provisions an '
    'account; the bot container that trades it is started with a matching '
    'Bot__AccountId.';

DO $$
DECLARE
    accounts INT;
    links    INT;
    untagged INT;
BEGIN
    SELECT count(*) INTO accounts FROM bot_config;
    SELECT count(*) INTO links    FROM client_link;
    SELECT count(*) INTO untagged FROM bot_trades WHERE account_id IS NULL;

    IF untagged > 0 THEN
        RAISE EXCEPTION '057: % bot_trades row(s) have no account_id', untagged;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_constraint
               WHERE conrelid = 'client_link'::regclass
                 AND conname  = 'client_link_id_check') THEN
        RAISE EXCEPTION '057: client_link still refuses a second account';
    END IF;

    RAISE NOTICE '057: multi-account ready. % account(s) provisioned, % key(s) linked.',
        accounts, links;
END $$;
