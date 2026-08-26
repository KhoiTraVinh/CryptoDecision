-- Move the retired strategy's live trades out of the active series.
--
-- MOMENTUM traded real funds on 2026-08-22 and was replaced by XVENUE_FLOW on
-- 2026-08-24. Its ten trades stayed in bot_trades, and two things still read the
-- series without filtering by strategy: SeedStats at bot startup, and
-- MaxDrawdownPct inside the circuit breakers. Both blend a retired signal's
-- record into judgements about the current one.
--
-- The consecutive-loss breaker was a third, and it was the one that bit: it
-- walked XVENUE_FLOW's first losing trade straight into four MOMENTUM losses
-- from three days earlier, called it five in a row, and disabled the bot for
-- fifteen hours. That is fixed in code (RiskEngine scopes the streak by
-- strategy), so this migration is not what makes the breaker correct -- it stops
-- the remaining aggregates from mixing two strategies' records.
--
-- ARCHIVED, NOT DELETED. These are real orders that really filled on OKX for
-- real money. The ten of them netted +0.1769 USD, and they are the only record
-- that the account traded live that day. Aggregate statistics wanting a clean
-- slate is not a reason to destroy a financial record; it is a reason to move it
-- somewhere the aggregates do not look.
--
-- To read the full history again, union the two tables.

CREATE TABLE IF NOT EXISTS bot_trades_archive (LIKE bot_trades INCLUDING ALL);

COMMENT ON TABLE bot_trades_archive IS
    'Trades from strategies no longer in service. Same shape as bot_trades. '
    'Nothing in the bot reads this table: it exists so the active series can be '
    'about the running strategy without a real financial record being deleted.';

ALTER TABLE bot_trades_archive
    ADD COLUMN IF NOT EXISTS archived_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ADD COLUMN IF NOT EXISTS archived_reason TEXT;

-- Guarded so a re-run cannot duplicate rows, even though migrate.sh applies each
-- file once by checksum.
INSERT INTO bot_trades_archive
SELECT t.*, NOW(),
       'MOMENTUM retired 2026-08-24, replaced by XVENUE_FLOW'
FROM bot_trades t
WHERE t.strategy = 'MOMENTUM'
  AND NOT EXISTS (SELECT 1 FROM bot_trades_archive a WHERE a.id = t.id);

DELETE FROM bot_trades WHERE strategy = 'MOMENTUM';

DO $$
DECLARE
    moved   INT;
    active  INT;
    net     NUMERIC;
BEGIN
    SELECT COUNT(*), COALESCE(SUM(pnl_usd), 0) INTO moved, net FROM bot_trades_archive;
    SELECT COUNT(*) INTO active FROM bot_trades;
    RAISE NOTICE 'archived % trade(s) netting % USD; % row(s) remain active', moved, net, active;
END $$;
