-- Every signal the strategy produced, what the gate did with it, and what the
-- market did afterwards — including the signals that were never traded.
--
-- Why this table has to exist
-- --------------------------
-- The gate's decisions were auditable only from `docker logs bot`. That log holds
-- one container's lifetime: on this deployment it was 23 hours, and it is destroyed
-- on every push to main, because a push builds new images and `up -d` replaces the
-- container. So the record of what the gate refused — the only evidence that could
-- ever say whether refusing was right — was being deleted several times a week.
--
-- The audit that motivated this table ran on the 23 hours that happened to survive:
-- 15 distinct signals, 3 approved (all three lost), 12 refused (two of which would
-- have won). Under the bot's real constraints — one position at a time, 900s
-- cooldown, 4 entries a day, 12h max hold — the live gate scored -3.00R over that
-- window while approving everything scored +1.00R. Five trades is not evidence of
-- anything. That is exactly the point: after four months of running, five trades is
-- all the evidence that existed, because nothing kept the refusals.
--
-- What a row is
-- -------------
-- One row per (symbol, side, 15-minute decision bucket) in which the strategy
-- produced an ACTIONABLE verdict. Not one per cycle: the strategy re-proposes the
-- same bucket every 30s until the next bar closes — 298 log lines for 15 real
-- signals in that window — and the gate itself is asked once per bucket. The bucket
-- is the decision, so the bucket is the row, and the unique constraint on it is what
-- makes both the writer and the labeler safe to re-run.
--
-- Two writers, deliberately
-- -------------------------
--   • BotService inserts the row when the signal fires, then stamps the gate's
--     verdict onto it. It never reads back and never blocks on this: a failed write
--     costs a row of evidence, and refusing to trade because a research table is
--     unavailable would be the wrong trade-off.
--   • ProcessorService labels the outcome later from `trades`, on its own schedule.
--     Labeling never runs inside the trading loop, so no amount of slow SQL here can
--     delay an entry or an exit.
--
-- The clock this table lives under
-- -------------------------------
-- `trades` is on 7-day retention and the labeling horizon is bot_config.max_hold_
-- minutes (720 = 12h). A signal is therefore labelable for roughly six days after it
-- fires, and after that its ticks are gone. The labeler marks those EXPIRED rather
-- than guessing, because a silently mislabelled outcome would poison every statistic
-- built on this table — and this table exists to be built on.

CREATE TABLE IF NOT EXISTS signal_outcomes (
    id                   BIGSERIAL PRIMARY KEY,

    -- ── Identity ─────────────────────────────────────────────────────────────
    symbol               VARCHAR(20)  NOT NULL,
    side                 VARCHAR(8)   NOT NULL CHECK (side IN ('LONG', 'SHORT')),
    strategy             VARCHAR(32)  NOT NULL DEFAULT 'XVENUE_FLOW',

    -- The 15-minute bucket whose closed bars produced this verdict. The natural key:
    -- the evidence cannot change inside a bucket, so a second row for the same
    -- bucket would be the same decision counted twice.
    bucket_start         TIMESTAMPTZ  NOT NULL,

    -- When the loop actually proposed it. Later than bucket_start by however long
    -- the aggregation and the cycle took; the gap is itself worth measuring, since
    -- "the entry is late" is the gate's most-used reason for refusing.
    signal_at            TIMESTAMPTZ  NOT NULL,

    -- ── Features, exactly as the scorer computed them ────────────────────────
    -- Stored rather than recomputed. Recomputation would drift the moment a
    -- threshold changes, and the whole purpose here is to compare decisions made
    -- under the rules that were live at the time.
    aggregate_z          NUMERIC(10, 4) NOT NULL,
    aggregate_ofi        NUMERIC(10, 4),
    agreeing_venues      SMALLINT     NOT NULL,
    participating_venues SMALLINT     NOT NULL,
    excluded_venues      SMALLINT     NOT NULL DEFAULT 0,
    dispersion_bps       NUMERIC(10, 2),
    atr_pct              NUMERIC(10, 4),
    signal_price         NUMERIC(20, 8),
    stop_pct             NUMERIC(10, 6) NOT NULL,
    target_pct           NUMERIC(10, 6) NOT NULL,
    reward_risk          NUMERIC(10, 4),
    confidence           NUMERIC(6, 4),

    -- Per-venue votes as the gate saw them. JSONB because the shape is the scorer's
    -- to change and this is evidence, not a schema to query on hot paths.
    venue_votes          JSONB,

    -- ── What the gate did ────────────────────────────────────────────────────
    -- APPROVED | REFUSED | NOT_GATED | UNREACHABLE. Free-text reason kept verbatim:
    -- it is the model's own words and paraphrasing it would destroy the only record
    -- of how it justified itself. Clustering happens at read time, in
    -- gate_reason_cluster(), so a re-clustering never rewrites history.
    gate_decision        VARCHAR(16),
    gate_reason          TEXT,
    gate_model           VARCHAR(48),
    gate_latency_ms      INTEGER,

    -- Set when the entry was actually placed. NULL for every refused signal, which
    -- is most of them, and the reason this table is not just a join on bot_trades.
    bot_trade_id         BIGINT,

    -- ── What the market did next, filled in by the labeler ───────────────────
    entry_price          NUMERIC(20, 8),
    stop_price           NUMERIC(20, 8),
    target_price         NUMERIC(20, 8),
    stop_hit_at          TIMESTAMPTZ,
    target_hit_at        TIMESTAMPTZ,

    -- WIN | LOSS | TIMEOUT | PENDING | EXPIRED | NO_TICKS
    --   TIMEOUT — neither level within the hold limit; a real outcome, worth ~0R
    --   PENDING — the horizon has not elapsed yet; the labeler will return
    --   EXPIRED — the ticks aged out of retention before labeling; unknowable now
    --   NO_TICKS — no OKX print after the signal at all; a data gap, not a result
    outcome              VARCHAR(12),

    -- Result in R (multiples of the risk taken to the stop), the unit the strategy
    -- is judged in. A win is the reward:risk ratio, a loss is exactly -1, a timeout
    -- is what the position was actually worth when the clock ran out.
    outcome_r            NUMERIC(10, 4),
    minutes_to_outcome   INTEGER,
    horizon_minutes      INTEGER,

    -- Which labeling rules produced this row's verdict. Bumping it is how a fixed
    -- labeler re-labels history instead of leaving two incompatible generations of
    -- rows silently mixed in the same statistics.
    label_version        SMALLINT     NOT NULL DEFAULT 1,
    labeled_at           TIMESTAMPTZ,

    created_at           TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT signal_outcomes_bucket_unique UNIQUE (symbol, side, bucket_start, strategy)
);

-- The labeler's work queue: everything not yet resolved, oldest first.
CREATE INDEX IF NOT EXISTS ix_signal_outcomes_unlabeled
    ON signal_outcomes (signal_at)
    WHERE outcome IS NULL OR outcome = 'PENDING';

-- The analysis path: decided outcomes by time.
CREATE INDEX IF NOT EXISTS ix_signal_outcomes_decided
    ON signal_outcomes (symbol, signal_at DESC)
    WHERE outcome IN ('WIN', 'LOSS', 'TIMEOUT');

-- The retrieval path for few-shot examples: nearest neighbours are searched within
-- a symbol and side.
CREATE INDEX IF NOT EXISTS ix_signal_outcomes_neighbours
    ON signal_outcomes (symbol, side, outcome);

COMMENT ON TABLE signal_outcomes IS
    'One row per actionable strategy signal, traded or not, with the gate verdict and '
    'the outcome the market delivered. The refused signals are the point: they are the '
    'only way to measure whether refusing was right.';


-- ─── Reason clustering ───────────────────────────────────────────────────────
--
-- The gate's reason is free text from a language model, so it cannot be grouped by
-- equality. It is not open-ended either: the system prompt lists four grounds for
-- skipping and the model reproduces their wording nearly verbatim, so keyword
-- matching over those four is enough and is auditable in a way an embedding is not.
-- If the prompt ever grows a fifth ground, this function needs the matching arm —
-- which is why unmatched text lands in 'other' rather than being forced into a
-- neighbouring bucket.
CREATE OR REPLACE FUNCTION gate_reason_cluster(reason TEXT)
RETURNS TEXT
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN reason IS NULL                         THEN 'none'
        WHEN reason ILIKE '%dispersion%'            THEN 'dispersion_wide'
        WHEN reason ILIKE '%thin data%'
          OR reason ILIKE '%bare minimum%'          THEN 'thin_data_agreement'
        WHEN reason ILIKE '%already open%'
          OR reason ILIKE '%concentrat%'            THEN 'positions_already_open'
        WHEN reason ILIKE '%lost significantly%'
          OR reason ILIKE '%account has already lost%' THEN 'account_loss_today'
        WHEN reason ILIKE '%unreachable%'
          OR reason ILIKE '%not JSON%'
          OR reason ILIKE '%malformed%'
          OR reason ILIKE '%empty answer%'
          OR reason ILIKE '%unrecognised%'          THEN 'gate_failure'
        WHEN reason ILIKE '%coherent%'
          OR reason ILIKE '%proportionate%'         THEN 'approved_coherent'
        ELSE 'other'
    END
$$;

COMMENT ON FUNCTION gate_reason_cluster(TEXT) IS
    'Groups the gate''s free-text reasons into the four grounds its system prompt '
    'names, plus gate_failure for the mechanical refusals (unreachable, unparseable) '
    'that are not judgements at all and must never be counted as if they were.';


-- ─── Premise check ───────────────────────────────────────────────────────────
--
-- Whether the reason the gate gave is contradicted by the brief it was given.
--
-- This is not a style complaint. Two of the four grounds in the prompt are
-- conditional on a number that is in the brief, and in the audited window the model
-- asserted both when the brief said otherwise:
--
--   • "venues were excluded for thin data (the brief says so, count above zero)"
--     on signals where excluded_venues = 0. The prompt warns against exactly this
--     confusion — a venue that participated and disagreed was not excluded — and
--     the model made it anyway, twice, quoting the warning's own parenthetical.
--   • "Several positions are already open in the same direction" on signals where
--     no position was open. It cannot be otherwise: the loop only evaluates entries
--     while open positions are below the per-strategy limit, which is 1, so the
--     gate is only ever asked with zero open.
--
-- Five of twelve refusals in the audited window failed this check. A refusal on a
-- premise the brief contradicts is a defect with a wrong answer attached, and it
-- has to be separable from a judgement the model was entitled to make — otherwise
-- every statistic below averages the two together.
CREATE OR REPLACE FUNCTION gate_premise_contradicted(
    reason TEXT, excluded_venues SMALLINT, bot_trade_id BIGINT)
RETURNS BOOLEAN
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN reason IS NULL THEN FALSE
        WHEN (reason ILIKE '%thin data%' OR reason ILIKE '%excluded%')
             AND COALESCE(excluded_venues, 0) = 0 THEN TRUE
        WHEN reason ILIKE '%already open%' THEN TRUE
        ELSE FALSE
    END
$$;


-- ─── The report view ─────────────────────────────────────────────────────────
--
-- Everything Task 2 asks for, as a view rather than a script, so the numbers on the
-- dashboard, in health.sh and in a weekly report cannot disagree about what a win
-- rate is.
CREATE OR REPLACE VIEW signal_gate_report AS
SELECT
    s.symbol,
    s.gate_decision,
    gate_reason_cluster(s.gate_reason)                                   AS reason_cluster,
    gate_premise_contradicted(s.gate_reason, s.excluded_venues, s.bot_trade_id)
                                                                         AS premise_contradicted,
    COUNT(*)                                                             AS signals,
    COUNT(*) FILTER (WHERE s.outcome = 'WIN')                            AS wins,
    COUNT(*) FILTER (WHERE s.outcome = 'LOSS')                           AS losses,
    COUNT(*) FILTER (WHERE s.outcome = 'TIMEOUT')                        AS timeouts,
    COUNT(*) FILTER (WHERE s.outcome IN ('PENDING', 'EXPIRED', 'NO_TICKS')) AS unlabelled,
    -- Win rate over decided outcomes only. Timeouts are excluded from the rate and
    -- reported separately: they are neither a win nor a loss, and folding them into
    -- either makes a slow market look like a skilled or unskilled one.
    ROUND(100.0 * COUNT(*) FILTER (WHERE s.outcome = 'WIN')
          / NULLIF(COUNT(*) FILTER (WHERE s.outcome IN ('WIN', 'LOSS')), 0), 1) AS win_rate_pct,
    ROUND(SUM(s.outcome_r) FILTER (WHERE s.outcome IN ('WIN', 'LOSS', 'TIMEOUT')), 2) AS total_r,
    ROUND(AVG(s.outcome_r) FILTER (WHERE s.outcome IN ('WIN', 'LOSS', 'TIMEOUT')), 3) AS avg_r,
    MIN(s.signal_at)                                                     AS first_signal,
    MAX(s.signal_at)                                                     AS last_signal
FROM signal_outcomes s
GROUP BY 1, 2, 3, 4;

COMMENT ON VIEW signal_gate_report IS
    'Win rate and R by gate decision and reason cluster. Read it with the sample size '
    'in view: below ~200 decided signals or 7 days these numbers describe one market '
    'regime, not the gate.';


-- ─── Threshold change ledger ─────────────────────────────────────────────────
--
-- Task 4's requirement, as a table rather than a convention: any threshold derived
-- from this data is written here with the evidence and the person who approved it
-- BEFORE it reaches production config.
--
-- Nothing in this repository writes to this table automatically, and nothing reads
-- it to change behaviour. That is deliberate. An auto-tuner that could deploy its
-- own thresholds would be a system optimising a 5-trade sample into production risk
-- — the exact failure the 250:1 hypothesis-to-observation ratio in HYPOTHESES.md
-- already warns about.
CREATE TABLE IF NOT EXISTS gate_tuning_log (
    id             BIGSERIAL PRIMARY KEY,
    changed_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    parameter      VARCHAR(64) NOT NULL,
    old_value      TEXT        NOT NULL,
    new_value      TEXT        NOT NULL,
    -- The evidence, in the shape a reviewer needs to disagree with it.
    sample_size    INTEGER     NOT NULL,
    window_start   TIMESTAMPTZ NOT NULL,
    window_end     TIMESTAMPTZ NOT NULL,
    in_sample_r    NUMERIC(10, 4),
    out_sample_r   NUMERIC(10, 4),
    rationale      TEXT        NOT NULL,
    approved_by    VARCHAR(64) NOT NULL,
    applied_at     TIMESTAMPTZ,

    -- A change proposed on a sample this small is not a change, it is a coin flip
    -- with paperwork. The floor is enforced here so that skipping it takes a
    -- deliberate migration rather than a moment of optimism.
    CONSTRAINT gate_tuning_min_sample CHECK (sample_size >= 200)
);

COMMENT ON TABLE gate_tuning_log IS
    'Every gate threshold or prompt change, with the evidence and the human who '
    'approved it. Never written automatically; the tuner proposes, a person applies.';


-- ─── Verify ──────────────────────────────────────────────────────────────────
DO $$
DECLARE missing TEXT;
BEGIN
    SELECT string_agg(c, ', ') INTO missing
    FROM (VALUES ('signal_outcomes'), ('gate_tuning_log')) v(c)
    WHERE to_regclass('public.' || c) IS NULL;

    IF missing IS NOT NULL THEN
        RAISE EXCEPTION 'migration 029 did not create: %', missing;
    END IF;

    IF to_regclass('public.signal_gate_report') IS NULL THEN
        RAISE EXCEPTION 'migration 029 did not create the signal_gate_report view';
    END IF;

    PERFORM gate_reason_cluster('dispersion is wide');
    RAISE NOTICE 'signal_outcomes, gate_tuning_log, signal_gate_report present';
END $$;
