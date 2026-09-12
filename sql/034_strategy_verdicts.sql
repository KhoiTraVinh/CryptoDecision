-- One current verdict per strategy, instead of one for the whole bot.
--
-- bot_config.last_verdict_* is a single set of columns on a single row, written every
-- cycle from inside the loop over active_strategies. That was exactly right while one
-- strategy ran. With two, each overwrites the other every cycle and the order of
-- active_strategies decides who survives -- so with '{XVENUE_FLOW,CANDLE_REVERSAL}' the
-- dip rule would win the write forever and FlowRatio's verdict would never be visible at
-- all. Both scripts/flow.sh and scripts/health.sh label that field "THE BOT'S VERDICT
-- (authoritative)", so the one surface an operator trusts would have been showing one
-- rule and silently hiding the other.
--
-- This is the same failure shape the verdict columns were added to fix. They exist
-- because the strategy's own abstention log is throttled, and during a 2.7% move the
-- freshest line was 33 minutes stale -- so the state had to be rebuilt by hand from
-- flow_bars_15m. Replacing one stale surface with one that shows the wrong strategy is
-- no better.
--
-- bot_config.last_verdict_* is NOT dropped. It stops being written, and the columns are
-- left in place: a DROP in a boot path is not a thing to run against a live account, and
-- an unused column costs nothing. They are marked superseded below so the next reader
-- does not trust a value frozen at whatever was written before this migration.

CREATE TABLE IF NOT EXISTS strategy_verdicts (
    strategy     TEXT         PRIMARY KEY,
    symbol       TEXT         NOT NULL,
    code         VARCHAR(48)  NOT NULL,
    detail       TEXT,
    aggregate_z  NUMERIC(10, 4),
    agree        SMALLINT,
    venues       SMALLINT,
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT now()
);

COMMENT ON TABLE strategy_verdicts IS
    'The most recent scorer verdict per strategy, upserted every evaluation cycle. One '
    'row per strategy name, so two strategies running in parallel cannot overwrite each '
    'other. Supersedes bot_config.last_verdict_*, which held one verdict for the whole '
    'bot and was written from inside the per-strategy loop.';

COMMENT ON COLUMN strategy_verdicts.aggregate_z IS
    'The ZScore statistic, or 0 when the rule does not compute one. FlowRatio and '
    'CandleReversal both record 0 here meaning NOT MEASURED, not "measured as nothing" '
    '-- read the detail string, which states what actually decided the verdict.';

COMMENT ON COLUMN bot_config.last_verdict_code IS
    'SUPERSEDED 2026-09-12 by the strategy_verdicts table and no longer written. A value '
    'here is frozen at whatever the last single-strategy build wrote. Left in place '
    'rather than dropped; do not read it.';
