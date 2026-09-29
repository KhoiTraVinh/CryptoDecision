-- ─────────────────────────────────────────────────────────────────────────────
-- 044: drop the entry gate's two config switches
--
-- H30 switched the gate off on 2026-09-29; this deletes it. `AiEntryGate` and its
-- 856 lines are gone, so `require_ai_gate` and `allow_entry_without_gate` no longer
-- control anything and a column that controls nothing is a trap for the next reader.
--
-- Why the gate went
-- -----------------
-- Measured against the 12-hour label over every gated signal on record:
--
--     APPROVED            57 signals    mean labelled R   -0.047
--     APPROVED_DEGRADED   35 signals    mean labelled R   +0.030
--     REFUSED             47 signals    mean labelled R   +0.392
--
-- It refused the better signals and approved the worse ones, on 139 observations and
-- across two separate measurements. The bound on that number is in HYPOTHESES.md: the
-- labels are 12-hour fixed barriers rather than the deployed exit, and 30 of the 47
-- refusals ended TIMEOUT, so +0.392R is a direction and not a magnitude.
--
-- WHAT IS DELIBERATELY KEPT, and this is the important half
-- ---------------------------------------------------------
-- Every gate column on `signal_outcomes` — gate_decision, gate_reason, gate_model,
-- gate_latency_ms — and `bot_trades.gate_verdict` / `gate_reason`, with all their rows.
-- Nothing writes them any more; they are the measurement history.
--
-- H30's own decision rule says to restore the gate if the window comes out below -0.10R.
-- That judgement is made FROM these columns. Dropping them to tidy up would delete the
-- evidence for the decision the entry commits to making, which is the shape of mistake
-- this repository has paid for more than once.
--
-- `signal_gate_report` and its two functions (gate_reason_cluster,
-- gate_premise_contradicted) are kept for the same reason: they are how that history is
-- read, and sql/038 already had to be told not to drop them.
--
-- New rows will carry NULL in all of these. NULL now means "no gate existed", where
-- before it meant "the gate never ran on this signal". The column comment on
-- gate_decision, set in sql/041, says the older thing; it is left alone rather than
-- rewritten, because both readings are true of the rows they apply to and the date in
-- this file is what separates them.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE bot_config DROP COLUMN IF EXISTS require_ai_gate;
ALTER TABLE bot_config DROP COLUMN IF EXISTS allow_entry_without_gate;

DO $$
DECLARE
    gone   INT;
    kept   INT;
    stamps INT;
BEGIN
    SELECT count(*) INTO gone FROM information_schema.columns
    WHERE table_name='bot_config' AND column_name IN ('require_ai_gate','allow_entry_without_gate');

    IF gone > 0 THEN
        RAISE EXCEPTION '044: % gate switch(es) still on bot_config', gone;
    END IF;

    SELECT count(*) INTO kept FROM information_schema.columns
    WHERE table_name='signal_outcomes'
      AND column_name IN ('gate_decision','gate_reason','gate_model','gate_latency_ms');

    IF kept <> 4 THEN
        RAISE EXCEPTION
            '044: expected all 4 signal_outcomes gate columns to survive, found % — the '
            'history H30 is judged from must not be dropped', kept;
    END IF;

    SELECT count(*) INTO stamps FROM signal_outcomes WHERE gate_decision IS NOT NULL;

    RAISE NOTICE '044: gate switches dropped; % stamped signal row(s) preserved for H30.', stamps;
END $$;
