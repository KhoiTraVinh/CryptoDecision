-- The strategy's current verdict, queryable.
--
-- Written because answering "how is the bot doing right now" took four queries
-- and a hand-rolled z calculation. SOL fell 2.7% over three hours and the most
-- recent line in the container log was 33 minutes old and said z=+0.50, because
-- CrossVenueFlowStrategy throttles its abstention log to once per code change
-- and then once per 120 cycles. That throttle is right for the log — a stable
-- refusal should not fill it — but it means the operator is blind for up to an
-- hour precisely when the market is moving and they most want to look.
--
-- Deliberately NOT reusing last_refusal_reason / refusal_count. Those count
-- refusals that mattered: the daily entry cap, and the gate declining a finished
-- proposal. An abstention happens on almost every cycle, so feeding it into
-- refusal_count would push that counter to ~2,880 a day and destroy the meaning
-- of a column already in use.
--
-- Deliberately NOT a time-series table either. flow_bars_15m already holds
-- everything the z is derived from, so the whole history of z is recomputable at
-- any time from data we keep — scripts/verdict-now.sql does exactly that. A
-- second copy of derivable data is a second thing to keep consistent.

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS last_verdict_code    VARCHAR(48),
    ADD COLUMN IF NOT EXISTS last_verdict_detail  TEXT,
    ADD COLUMN IF NOT EXISTS last_verdict_z       NUMERIC(10, 4),
    ADD COLUMN IF NOT EXISTS last_verdict_agree   SMALLINT,
    ADD COLUMN IF NOT EXISTS last_verdict_venues  SMALLINT,
    ADD COLUMN IF NOT EXISTS last_verdict_at      TIMESTAMPTZ;

COMMENT ON COLUMN bot_config.last_verdict_code IS
    'Abstain code from the last evaluation, or ACTIONABLE when the strategy proposed '
    'an entry. Updated every cycle, unlike the throttled log line.';

COMMENT ON COLUMN bot_config.last_verdict_z IS
    'Aggregate cross-venue flow z at the last evaluation. The number that decides '
    'entry, so "how close was it" is a query rather than a log search.';

COMMENT ON COLUMN bot_config.last_verdict_at IS
    'When the last verdict was formed. Distinct from last_eval_at: the loop can tick '
    'without producing a verdict, for example when the price fetch fails.';
