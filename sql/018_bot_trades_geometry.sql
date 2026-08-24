-- ─────────────────────────────────────────────────────────────────────────────
-- 018: Per-trade stop and target prices
--
-- Why the row has to carry them
-- -----------------------------
-- Exits used to be derived from configuration at evaluation time: take profit 2.0%,
-- stop loss 1.5%, trailing 1.2%, read fresh from bot_config on every cycle. That
-- works only while the numbers are constants. Once the stop is scaled to measured
-- volatility, the distance is a property of the moment the position was opened, and
-- recomputing it later asks a different question — the ATR has moved, so the stop
-- would drift under the position it is supposed to be protecting.
--
-- It also made a live config edit retroactive. Widening stop_loss_pct while a
-- position was open silently moved that position's stop, which is the one thing a
-- stop must never do.
--
-- Nullable, no default: NULL means "this row predates volatility-scaled exits, use
-- the configured percentages", which is what every existing trade needs.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS stop_price    NUMERIC(20, 8),
    ADD COLUMN IF NOT EXISTS target_price  NUMERIC(20, 8),
    -- The ATR reading the geometry was derived from, in percent of price. Recorded
    -- because without it a post-hoc review cannot tell a stop that was too tight
    -- from a market that moved further than usual — and that distinction is the
    -- whole reason the stop stopped being a constant.
    ADD COLUMN IF NOT EXISTS atr_pct_at_entry NUMERIC(10, 6);

COMMENT ON COLUMN bot_trades.stop_price IS
    'Absolute price at which this position closes for a loss, fixed at entry from '
    'measured volatility. NULL for rows opened before volatility-scaled exits.';
COMMENT ON COLUMN bot_trades.target_price IS
    'Absolute price at which this position closes for a profit, fixed at entry.';
COMMENT ON COLUMN bot_trades.atr_pct_at_entry IS
    'ATR as a percent of price when the geometry was set, so a losing trade can be '
    'attributed to a stop that was too tight versus a market that moved unusually.';


-- ─────────────────────────────────────────────────────────────────────────────
-- The entry gate's verdict, and who gave it.
--
-- The deterministic signal proposes; a gate approves or refuses. Both halves have
-- to be recoverable from the row afterwards, because "why did we take this trade"
-- and "why did we skip that one" are the two questions a losing run has to answer,
-- and the previous incarnation could answer neither: the score lived in a container
-- log that did not survive the container.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS gate_verdict TEXT,
    ADD COLUMN IF NOT EXISTS gate_reason  TEXT;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'bot_trades_gate_verdict_check'
    ) THEN
        ALTER TABLE bot_trades
            ADD CONSTRAINT bot_trades_gate_verdict_check
            CHECK (gate_verdict IS NULL
                   OR gate_verdict IN ('APPROVED', 'APPROVED_DEGRADED', 'NOT_GATED'));
    END IF;
END $$;

COMMENT ON COLUMN bot_trades.gate_verdict IS
    'APPROVED = the AI gate approved this entry. APPROVED_DEGRADED = approved by the '
    'deterministic fallback because the gate was unreachable and the operator has '
    'allowed that. NOT_GATED = gating was switched off. A refused entry never '
    'produces a row here — see bot_config.last_refusal_reason and the refusal trail.';


-- ─────────────────────────────────────────────────────────────────────────────
-- Gate configuration.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config
    -- Require the AI gate to approve before any entry is placed.
    --
    -- Default TRUE, unlike every other AI switch in this table. The others add the
    -- model's opinion to a decision that happens anyway, so defaulting them off is
    -- the conservative choice. This one can only ever *prevent* an entry, so
    -- defaulting it on is the conservative choice — and it is the switch that makes
    -- the discipline real: no position is opened that the gate did not approve.
    ADD COLUMN IF NOT EXISTS require_ai_gate BOOLEAN NOT NULL DEFAULT TRUE,

    -- What to do when the gate cannot be reached.
    --
    -- FALSE (the default) means no entry: an unavailable gate stops trading rather
    -- than silently reverting to ungated entries. That is the failure mode worth
    -- having, because the alternative is a deployment where the gate has been dead
    -- for a week and nothing looks different.
    ADD COLUMN IF NOT EXISTS allow_entry_without_gate BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN bot_config.require_ai_gate IS
    'When true, no entry is placed unless the AI gate approves it. The gate can only '
    'refuse a candidate the deterministic signal already produced; it cannot propose '
    'a trade, choose a direction, or alter size or stops.';
COMMENT ON COLUMN bot_config.allow_entry_without_gate IS
    'When true, an unreachable gate falls back to the deterministic signal alone and '
    'the trade is recorded as APPROVED_DEGRADED. When false (default) an unreachable '
    'gate means no entry.';
