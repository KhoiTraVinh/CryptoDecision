-- Which entry rule opened this position.
--
-- FlowRatio now has two ways in, and they are not variations of one rule -- they are
-- different trades with different measured behaviour:
--
--   RATIO        the last closed bucket leaned >= RatioMinimum on >= RatioMinVolumeUsd.
--   HIGH_VOLUME  the bucket traded >= RatioHighVolumeUsd, so the ratio test was waived
--                and the entry took whichever side was heavier, however narrow the lead.
--
-- Why a column rather than reading it back out of entry_rationale
-- ---------------------------------------------------------------
-- Because this repository has already paid for that. `allow_entry_without_gate` was
-- applied by matching the first two words of the gate's reason string, which silently
-- missed four of the six failure paths -- including the empty-answer case that actually
-- occurred in production on 2026-09-06, where an entry was blocked by a rule the
-- operator had explicitly switched off. The fix there was to make the distinction a
-- real field (GateDecision.Unavailable) instead of recovering it from prose. This is
-- the same distinction and it gets the same treatment: a fact the code branches on
-- lives in a column, not in a sentence written for a human.
--
-- It is also what makes the two rules separable afterwards. H9 judges the ratio rule and
-- H11 judges the high-volume one; they share a strategy name, a position book and a
-- trade stream, so without this column the 20-trade bar in H11's decision rule cannot be
-- counted and H9's 30-trade bar would be reached by a mixed population.
--
-- NULL means "not recorded": every row written before this column existed, and any
-- future strategy that does not set it. Readers must treat NULL as unknown rather than
-- as any particular path -- in particular the concurrency limit below counts only rows
-- that positively say HIGH_VOLUME, so an unmarked row can never silently consume the
-- single slot.

ALTER TABLE bot_trades
    ADD COLUMN IF NOT EXISTS entry_path TEXT;

COMMENT ON COLUMN bot_trades.entry_path IS
    'Which entry rule opened this position: RATIO (the imbalance threshold) or '
    'HIGH_VOLUME (the news-print waiver, which skips the ratio test). NULL for rows '
    'written before 2026-09-11 and for strategies that do not set it. Recorded as a '
    'column rather than parsed from entry_rationale because the code branches on it.';

-- Partial index: the only query that reads this is "how many HIGH_VOLUME positions are
-- open right now", evaluated on every entry decision. Open positions are a handful of
-- rows against a growing table, and the predicate matches the check exactly.
CREATE INDEX IF NOT EXISTS idx_bot_trades_open_entry_path
    ON bot_trades (entry_path)
    WHERE status = 'OPEN';

-- How many positions opened through the high-volume waiver may be held at once.
--
-- 1, and the reason is concentration rather than tidiness. The waiver fires on the
-- largest buckets in the sample -- 42 of 1,770 above $20M -- and those cluster: of the
-- nine buckets above $40M, two pairs were fifteen minutes apart and five of the nine
-- fell on three days. So a rule with no per-rule cap can put its whole day's risk into
-- one macro event, which is the single thing a "news print" entry is most likely to do.
-- max_open_trades_per_strategy and max_open_per_side do not prevent it: both sides of
-- one event qualify, so 2 total with 1 per side still admits a LONG and a SHORT from
-- the same fifteen minutes.
--
-- 0 disables the cap and restores the previous behaviour.

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS max_open_high_volume INTEGER NOT NULL DEFAULT 1;

COMMENT ON COLUMN bot_config.max_open_high_volume IS
    'Concurrent positions opened through the FlowRatio high-volume waiver '
    '(bot_trades.entry_path = HIGH_VOLUME). 0 disables the cap. Separate from '
    'max_open_trades_per_strategy because the waiver fires on clustered events and '
    'both sides of one event can qualify.';
