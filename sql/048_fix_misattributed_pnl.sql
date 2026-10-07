-- ─────────────────────────────────────────────────────────────────────────────
-- 048: correct trades 170 and 175, which were paid another position's P&L
--
-- Both rows took their realised figure from an OKX positions-history entry that
-- belonged to the PREVIOUS position. The guard meant to prevent that used openAvgPx
-- alone, on the stated assumption that "a different position almost never opened at
-- the same price". Trade 174 opened at 116.37 and trade 175 at 116.36 — 0.0086%
-- apart against a 0.1% tolerance — so 175 was credited 174's +0.09457775.
--
-- The code defect is fixed separately (902b663): settlement time and a hold-scaled
-- magnitude bound are now required alongside the price match. This migration repairs
-- the two rows that were already written.
--
--     id   recorded    corrected    moved by
--     170  +0.2614     +0.1191      -0.1423
--     175  +0.0946     -0.1503      -0.2449     (flips from a win to a loss)
--
-- The corrected figures are DERIVED: (exit - entry) * quantity - fee, which is what
-- the engine computes from this trade's own fills before reconciliation. They EXCLUDE
-- funding, exactly as the fallback path says when attribution fails. For a 1.0h and a
-- 1.1h hold that is roughly 5-7 bps unaccounted, against the 84 bps and 48 bps of
-- someone else's P&L being removed. Reaching for the true funded figure would mean
-- replaying positions-history against the exchange, which is not something a
-- migration should do.
--
-- WHY THIS IS NOT COSMETIC
-- ------------------------
-- SUM(pnl_usd) over LIVE closed trades is what feeds todayPnlUsd into
-- RiskEngine.CheckCircuitBreakers. The daily loss limit has been evaluated against a
-- ledger reading -0.2397 when the account stood at -0.6269 — 2.6x better than
-- reality, in the direction that delays a breaker rather than trips one early.
--
-- It also corrupts the open H34 window: capture ratio on winners read 78.6%, above
-- the 70% KEEP bar, because 175 was counted as a +0.0946 winner. It is a loss.
--
-- SAFETY
-- ------
-- Each UPDATE is guarded on the exact wrong value, so re-running this file changes
-- nothing. bot_config counters are RECOMPUTED from bot_trades rather than adjusted by
-- a delta, so they end up correct even if they had drifted for some other reason.
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE bot_trades
   SET pnl_usd = 0.1191,
       pnl_pct = 0.003992
 WHERE id = 170
   AND pnl_usd = 0.2614;

UPDATE bot_trades
   SET pnl_usd = -0.1503,
       pnl_pct = -0.005167
 WHERE id = 175
   AND pnl_usd = 0.0946;

-- Recomputed, not adjusted. These counters are maintained incrementally by the bot,
-- so deriving them from the rows is the only version that is self-correcting.
UPDATE bot_config c
   SET total_trades  = s.n,
       total_pnl_usd = s.total,
       win_count     = s.wins,
       loss_count    = s.losses,
       updated_at    = now()
  FROM (
        SELECT count(*)                           AS n,
               coalesce(sum(pnl_usd), 0)          AS total,
               count(*) FILTER (WHERE pnl_usd > 0)  AS wins,
               count(*) FILTER (WHERE pnl_usd <= 0) AS losses
          FROM bot_trades
         WHERE mode = 'LIVE' AND status = 'CLOSED'
       ) s
 WHERE c.id = 1;

DO $$
DECLARE
    p170 NUMERIC; p175 NUMERIC; tot NUMERIC; w INT; l INT;
BEGIN
    SELECT pnl_usd INTO p170 FROM bot_trades WHERE id = 170;
    SELECT pnl_usd INTO p175 FROM bot_trades WHERE id = 175;
    SELECT total_pnl_usd, win_count, loss_count INTO tot, w, l FROM bot_config WHERE id = 1;

    IF p170 <> 0.1191 THEN
        RAISE EXCEPTION '048: trade 170 is %, expected 0.1191', p170;
    END IF;

    IF p175 <> -0.1503 THEN
        RAISE EXCEPTION '048: trade 175 is %, expected -0.1503', p175;
    END IF;

    IF p175 > 0 THEN
        RAISE EXCEPTION '048: trade 175 is still recorded as a win';
    END IF;

    RAISE NOTICE '048: 170 -> %, 175 -> %. Ledger now % over % wins / % losses. '
                 'Both figures exclude funding, as the attribution-failure path does.',
                 p170, p175, tot, w, l;
END $$;
