-- ─────────────────────────────────────────────────────────────────────────────
-- 041: signal_outcomes.gate_decision becomes TEXT, and the rows it silently
--      dropped are recovered from bot_trades
--
-- The defect
-- ----------
-- sql/029 declares the column `VARCHAR(16)`. `AiEntryGate` writes four values:
--
--     APPROVED            8 chars    fits
--     REFUSED             7 chars    fits
--     NOT_GATED           9 chars    fits
--     APPROVED_DEGRADED  17 chars    DOES NOT FIT
--
-- So every degraded approval — the gate answering SKIP on one ground, or on a premise
-- the brief contradicts, with `allow_entry_without_gate` letting the entry through —
-- raised 22001 inside `SignalOutcomeRepository.StampGateAsync` and wrote nothing:
--
--     22001: value too long for type character varying(16)
--
-- `SafeRecordAsync` catches it, logs at Warning and continues, deliberately: a failed
-- research write must never stop a position being managed. The trade still opened, still
-- carried its verdict on `bot_trades.gate_verdict` (TEXT, which never had this problem),
-- and the only casualty was the research table.
--
-- What it cost
-- ------------
-- Nine signal rows lost all four gate columns — decision, reason, model and latency. The
-- shape of the loss is what makes it bad: `REFUSED` fits, so **every refusal was recorded
-- and every degraded approval was not**. Anything grouping `signal_outcomes` by
-- `gate_decision` — `signal_gate_report` above all — saw a gate that only ever refuses.
-- Five of the nine are the trades of observation window W1.
--
-- This is the same failure mode as 037, three weeks later and one column across: a write
-- that cannot succeed, an exception that is caught by design, and every health check green.
--
-- Why TEXT rather than a bigger VARCHAR
-- -------------------------------------
-- Because the defect IS the width. `VARCHAR(32)` would be another guess about strings
-- nobody has written yet, and the sibling column `bot_trades.gate_verdict` has been TEXT
-- the whole time and has never once truncated or thrown. Postgres stores them identically;
-- varchar(n) -> text is binary-coercible, so this does not rewrite the table.
--
-- The view has to be dropped and recreated
-- ----------------------------------------
-- `signal_gate_report` selects the column, and Postgres refuses ALTER COLUMN TYPE on a
-- column a view depends on — widening included. The body below is `pg_get_viewdef` taken
-- from production, so it is recreated as it actually is rather than as sql/029 remembers
-- it. `gate_reason_cluster` and `gate_premise_contradicted` are called from inside it;
-- sql/038 kept both for exactly this reason and grep over the C# cannot see either.
--
-- migrate.sh applies each file with psql --single-transaction and puts the ledger insert
-- into that same transaction, so the view is never observably missing. This file must NOT
-- open a transaction of its own: an explicit COMMIT here would end that one early and
-- leave the schema_migrations row outside it, which is the atomicity migrate.sh exists to
-- provide. No other migration in this directory opens one either.
--
-- The backfill
-- ------------
-- `bot_trades` holds the verdict and the reason for all nine, linked by `bot_trade_id`.
-- Model and latency are not on that table and are gone for good — left NULL, which reads
-- as "not recorded" rather than inventing a value. Restricted to rows that are NULL and
-- do have a trade, so it is idempotent and cannot touch a row the gate legitimately never
-- reached: signal 1554 has no trade because the per-side limit stopped it before the gate
-- ran, and it must stay NULL.
--
-- sql/029 is NOT edited. It is recorded in schema_migrations with a checksum, and editing
-- an applied migration is how two machines come to disagree about the schema.
-- ─────────────────────────────────────────────────────────────────────────────

DROP VIEW IF EXISTS signal_gate_report;

ALTER TABLE signal_outcomes
    ALTER COLUMN gate_decision TYPE TEXT;

CREATE VIEW signal_gate_report AS
 SELECT symbol,
    gate_decision,
    gate_reason_cluster(gate_reason) AS reason_cluster,
    gate_premise_contradicted(gate_reason, excluded_venues, bot_trade_id) AS premise_contradicted,
    count(*) AS signals,
    count(*) FILTER (WHERE outcome::text = 'WIN'::text) AS wins,
    count(*) FILTER (WHERE outcome::text = 'LOSS'::text) AS losses,
    count(*) FILTER (WHERE outcome::text = 'TIMEOUT'::text) AS timeouts,
    count(*) FILTER (WHERE outcome::text = ANY (ARRAY['PENDING'::character varying, 'EXPIRED'::character varying, 'NO_TICKS'::character varying]::text[])) AS unlabelled,
    round(100.0 * count(*) FILTER (WHERE outcome::text = 'WIN'::text)::numeric / NULLIF(count(*) FILTER (WHERE outcome::text = ANY (ARRAY['WIN'::character varying, 'LOSS'::character varying]::text[])), 0)::numeric, 1) AS win_rate_pct,
    round(sum(outcome_r) FILTER (WHERE outcome::text = ANY (ARRAY['WIN'::character varying, 'LOSS'::character varying, 'TIMEOUT'::character varying]::text[])), 2) AS total_r,
    round(avg(outcome_r) FILTER (WHERE outcome::text = ANY (ARRAY['WIN'::character varying, 'LOSS'::character varying, 'TIMEOUT'::character varying]::text[])), 3) AS avg_r,
    min(signal_at) AS first_signal,
    max(signal_at) AS last_signal
   FROM signal_outcomes s
  GROUP BY symbol, gate_decision, (gate_reason_cluster(gate_reason)), (gate_premise_contradicted(gate_reason, excluded_venues, bot_trade_id));

COMMENT ON COLUMN signal_outcomes.gate_decision IS
    'APPROVED, APPROVED_DEGRADED, NOT_GATED or REFUSED, as AiEntryGate produced it. TEXT '
    'and not VARCHAR(n): this column was VARCHAR(16) until 2026-09-23, which silently '
    'rejected every 17-character APPROVED_DEGRADED for four days while recording every '
    'REFUSED, so the table showed a gate that only ever refused. NULL means the gate never '
    'ran on this signal -- a cap or cooldown stopped the entry first -- not that it '
    'answered and the answer was lost.';

-- Recover the nine rows the width rejected. gate_model and gate_latency_ms are not
-- carried on bot_trades and stay NULL.
UPDATE signal_outcomes s
SET gate_decision = t.gate_verdict,
    gate_reason   = COALESCE(s.gate_reason, t.gate_reason)
FROM bot_trades t
WHERE t.id = s.bot_trade_id
  AND s.gate_decision IS NULL
  AND t.gate_verdict IS NOT NULL;

DO $$
DECLARE
    remaining INT;
    recovered INT;
BEGIN
    -- The post-condition of the UPDATE above and nothing wider. Deliberately NOT
    -- "every signal with a trade has a verdict": that is a statement about data this
    -- migration does not control, and a single future row failing it would fail the
    -- migration, which is never recorded, which fails the next deploy too — a deadlock
    -- no one could clear without editing an applied file. This version is true by
    -- construction the moment the UPDATE succeeds, on a fresh empty database included.
    SELECT count(*) INTO remaining
    FROM signal_outcomes s
    JOIN bot_trades t ON t.id = s.bot_trade_id
    WHERE s.gate_decision IS NULL AND t.gate_verdict IS NOT NULL;

    SELECT count(*) INTO recovered
    FROM signal_outcomes WHERE gate_decision = 'APPROVED_DEGRADED';

    IF remaining > 0 THEN
        RAISE EXCEPTION
            '041: % signal row(s) still have a trade whose verdict was not copied back; '
            'the backfill did not do what it claims', remaining;
    END IF;

    RAISE NOTICE '041: gate_decision is TEXT; % APPROVED_DEGRADED row(s) now readable, '
                 '0 signal rows left with a trade and no verdict.', recovered;
END $$;
