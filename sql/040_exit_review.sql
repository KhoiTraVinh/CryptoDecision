-- ─────────────────────────────────────────────────────────────────────────────
-- 040: Record when a position was last put to the LLM for an exit decision
--
-- What changes above this column
-- ------------------------------
-- The early exit stops being a hard rule. Until now a position was closed when the
-- aggregate imbalance over the last 15 closed buckets turned against it after having
-- favoured it — `OFI_REVERSAL`, evaluated every 30 seconds. From here the model is asked
-- instead: **is there still force in this direction, or is the trend turning?** If the
-- answer is no, the position is cut.
--
-- `last_exit_review_at` is what paces that. Without it the question would be asked every
-- cycle, and that is not affordable here: one call costs a measured 42-43s on a 2-vCPU
-- host whose Ollama container already sits at 3.406 of its 3.418 GiB limit with
-- NUM_PARALLEL=1, so calls serialise. The evaluation cycle has a 120s budget and must also
-- manage every other open position and, on the entry side, possibly ask the gate. Two
-- positions reviewed synchronously every cycle would blow that budget and the loop would
-- log "Open positions were not evaluated this cycle".
--
-- So: a position is left alone for its first two hours, then reviewed at most once every
-- two hours. A four-hour hold — the measured average is 4.04h — costs about two calls,
-- which is the same order as the entry gate's current 2-10 calls a day rather than the
-- 16-per-position that a per-bucket cadence would have cost.
--
-- Why not a column on a config table
-- ----------------------------------
-- The pacing is per POSITION, not global: two positions opened an hour apart should be
-- reviewed an hour apart. Holding it on the row also means a restart cannot lose it and
-- cannot double-review — the bot recovers open positions from this table on startup, so
-- the clock survives with the position it belongs to.
--
-- NULL means never reviewed, which is the correct state for every row that exists now and
-- for every new position until it is two hours old.
--
-- The deterministic rule is NOT deleted
-- -------------------------------------
-- `UseFlowOfiExit` and `FlowOfiBars` stay, and the 15-bucket sum still runs — but only as
-- the FALLBACK, evaluated at the moment a review was due and the model could not answer
-- (Ollama down, timed out, unparseable). It deliberately no longer runs on its own every
-- cycle: if it did, it would close positions before the two-hour mark ever arrived and the
-- model would almost never be asked. That is the whole reason this is a replacement rather
-- than an addition.
--
-- Keeping it as the fallback is a deliberate choice about which way to fail. The OFI exit
-- is the only exit that has produced positive R on this account — 30 exits, +4.647R,
-- against the stop's -6.615R over 6 and a take-profit that has never fired — so an
-- Ollama outage falling back to it preserves the mechanism that earns, rather than leaving
-- positions with nothing but a stop that is where all the damage is.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS last_exit_review_at TIMESTAMPTZ;

COMMENT ON COLUMN bot_trades.last_exit_review_at IS
    'When the LLM was last asked whether to cut this position. NULL = never asked. Paces '
    'the review to once every ExitReviewIntervalHours, per position rather than globally, '
    'and survives a restart because open positions are recovered from this table.';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'bot_trades' AND column_name = 'last_exit_review_at')
    THEN
        RAISE EXCEPTION 'sql/040 did not add bot_trades.last_exit_review_at';
    END IF;

    RAISE NOTICE 'sql/040: last_exit_review_at present on bot_trades';
END $$;
