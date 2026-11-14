-- ─────────────────────────────────────────────────────────────────────────────
-- 055: bot_config.fixed_notional_usd — the app's "amount per trade", wired to sizing
--
-- Fixes a bug of my own making, reported 2026-11-14: the operator raised the amount
-- per trade to $20 in the phone app and orders kept opening at the old size.
--
-- What was wrong
-- --------------
-- 054 and the client API stored the figure in client_link.amount_per_trade_usd, and
-- NOTHING in the order path ever read it. EntrySizing computes
--
--     notional = min( capital_usd * risk_pct_per_trade / stop_pct , MaxOrderNotionalUsd )
--
-- and that column appears nowhere in it. The app wrote to the database, showed the
-- value back, and the bot ignored it — the same "setting that lies" shape as
-- DIP_LONG_ONLY, except this one I built.
--
-- The fix is one source of truth rather than two columns that can drift: the amount
-- lives on bot_config, where BotOptions already reaches both order engines, and
-- client_link.amount_per_trade_usd is dropped so nobody reads the stale copy.
--
-- 0 means "no fixed amount", which restores the risk-based sizing above. Any value
-- is still capped by Okx:MaxOrderNotionalUsd, so this can lower the order size but
-- never raise it past the per-order ceiling.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS fixed_notional_usd NUMERIC(12, 2) NOT NULL DEFAULT 0;

COMMENT ON COLUMN bot_config.fixed_notional_usd IS
    'Operator, 2026-11-14. Position size in USD that the app sets, used directly as the '
    'order notional. 0 = fall back to capital_usd * risk_pct_per_trade / stop_pct. '
    'Always capped by Okx:MaxOrderNotionalUsd.';

-- Carry across whatever the app last stored, so the operator does not have to set it
-- again, and so the number they are looking at on the handset becomes the live one.
UPDATE bot_config b
   SET fixed_notional_usd = c.amount_per_trade_usd
  FROM client_link c
 WHERE b.id = 1 AND c.id = 1
   AND c.amount_per_trade_usd IS NOT NULL
   AND b.fixed_notional_usd = 0;

ALTER TABLE client_link
    DROP COLUMN IF EXISTS amount_per_trade_usd;

DO $$
DECLARE v NUMERIC;
BEGIN
    SELECT fixed_notional_usd INTO v FROM bot_config WHERE id = 1;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'client_link' AND column_name = 'amount_per_trade_usd') THEN
        RAISE EXCEPTION '055: client_link.amount_per_trade_usd survived the drop';
    END IF;

    RAISE NOTICE '055: fixed_notional_usd = % (0 means risk-based sizing).', v;
END $$;
