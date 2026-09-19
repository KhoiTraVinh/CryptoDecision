-- ─────────────────────────────────────────────────────────────────────────────
-- 037: Let signal_outcomes.excluded_venues be NULL
--
-- The defect this repairs
-- -----------------------
-- `e22a31a` deleted the venue-scoring shape the ZScore rule left behind, and corrected
-- the write that had been recording `Votes.Count - ParticipatingVenues` — which with no
-- votes and three venues is 0 - 3 = -3, a negative count of excluded venues, on
-- twenty-five production rows. The corrected writer passes NULL instead, meaning "this
-- rule does not measure venues" rather than "it measured and found none":
--
--     cmd.Parameters.AddWithValue("excluded", DBNull.Value);
--
-- But sql/029 declares the column `SMALLINT NOT NULL DEFAULT 0`, and a DEFAULT only
-- applies when a column is OMITTED from the INSERT. An explicitly-passed NULL is not
-- replaced by it — it is a not-null violation. So every `RecordSignalAsync` call on
-- f2c64db raises 23502 and writes nothing.
--
-- Why this was worth a migration rather than reverting the writer
-- ---------------------------------------------------------------
-- NULL is the honest value and the reverted alternative is the -3. The column stays for
-- the rows written before the deletion, which carry a real count from a rule that really
-- did score venues.
--
-- What it was costing, and why nothing looked wrong
-- -------------------------------------------------
-- `SafeSignalAsync` catches the exception, logs at Warning and returns null, deliberately
-- — a failed research write must never stop an entry. So the trade still opens, still
-- exits, still books its PnL, and the only casualty is the measurement: no
-- `signal_outcomes` row, therefore no gate verdict stamp, no signal-to-trade link, no
-- labeling, and nothing entering the retrieval corpus. H17 (the gate's reachable grounds)
-- and H18 (the reopened ratio path) are both measured off this table, so two open
-- experiments would have recorded nothing while every health check stayed green. That is
-- the failure mode this codebase keeps paying for; see README and HYPOTHESES.md.
--
-- It had not fired yet when this was written: f2c64db deployed at 16:29 UTC on
-- 2026-09-19 and no strategy passed between then and the discovery, because SOL was flat
-- (+0.07% over 4h) and no 15-minute bucket cleared both the $3M floor and the 2.10x
-- ratio. The first signal after the deploy is when it would have bitten.
--
-- Already applied by hand
-- -----------------------
-- Run directly against the production database on 2026-09-19 so the two open hypotheses
-- would stop losing observations before the next signal. `DROP NOT NULL` on an already
-- nullable column is a no-op that does not error, so this file re-running there is
-- harmless — and it has to exist regardless, because sql/029 is what a database built
-- from scratch is made of. Without it a fresh host comes up with the same broken column
-- and the same silent write, which is the shape of defect 023 already carries.
--
-- sql/029 itself is NOT edited: it is recorded in schema_migrations with a checksum, and
-- editing an applied migration is how two machines come to disagree about the schema.
--
-- Downstream
-- ----------
-- `gate_premise_contradicted` already reads the column as `COALESCE(excluded_venues, 0)`,
-- so a NULL there means the same thing it did before: no fabricated exclusion to catch.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE signal_outcomes
    ALTER COLUMN excluded_venues DROP NOT NULL;

COMMENT ON COLUMN signal_outcomes.excluded_venues IS
    'Venues dropped from the aggregate before scoring. NULL means the rule does not '
    'measure venues at all, which is true of every rule in this build -- FlowRatio reads '
    'one aggregate bucket and CandleReversal reads price. A number here belongs to a row '
    'written before the venue-scoring rule was deleted on 2026-09-18.';
