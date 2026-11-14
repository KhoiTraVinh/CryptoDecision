-- ─────────────────────────────────────────────────────────────────────────────
-- 054: bot_config.sl_side_block_minutes — do not re-enter a side it was STOPPED on
--
-- Operator rule, 2026-11-14, after three stop-outs in a row on the long side cost
-- $1.82 while SOL fell 9.2% in 15 hours and the operator shut the server off by hand
-- to stop a fourth.
--
-- Not the same rule as SameSideLossBlockMinutes
-- --------------------------------------------
-- That one blocks a side after ANY loss and is held at 60 minutes, because 2h, 4h and
-- 8h all looked better on the full 134-trade sample and all collapsed in its second
-- half. This one blocks only after an EXCHANGE_SL, which is a different and much
-- narrower signal: a stop-out means price travelled the full 2% against a
-- mean-reversion entry, so a trend is in progress. A timeout that closed a few cents
-- down says nothing of the kind.
--
-- The difference is the whole argument. Measured on the 28 closed CANDLE_REVERSAL
-- trades, blocking the same side for 6 hours:
--
--     after an EXCHANGE_SL   3 trades blocked, -$1.135   <- almost pure loss
--     after ANY loss         7 trades blocked, -$0.050   <- blocks winners too
--
-- The any-loss version blocks more than twice as many trades for no net benefit,
-- which is exactly the indiscriminate behaviour that failed out-of-sample before.
--
-- Registered as thin
-- ------------------
-- 3 blocked trades, and most of the gain is ONE cascade (31 -> 33 -> 34 on 11-13/14).
-- Excluding that episode the rule is worth about +$0.09. What makes it worth keeping
-- anyway is the asymmetry: at 6 hours it costs exactly one winning trade (+$0.08) and
-- avoids two stop-outs (-$1.21). 4 hours was tested and is too short — it misses the
-- second trade of the cascade, which entered 5h53m after the first stop.
--
-- 0 disables it, which leaves SameSideLossBlockMinutes as the only side gate.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS sl_side_block_minutes INT NOT NULL DEFAULT 360;

COMMENT ON COLUMN bot_config.sl_side_block_minutes IS
    'Operator, 2026-11-14. Minutes a side is blocked after a trade on it closed '
    'EXCHANGE_SL. 360. Narrower than same_side_loss_block_minutes, which blocks after '
    'any loss and stays at 60. 0 disables.';

DO $$
DECLARE v INT; s INT;
BEGIN
    SELECT sl_side_block_minutes INTO v FROM bot_config WHERE id = 1;

    IF v IS NULL THEN
        RAISE EXCEPTION '054: sl_side_block_minutes is NULL on bot_config row 1';
    END IF;

    RAISE NOTICE '054: a side is blocked for % min after a stop-out on it.', v;
END $$;
