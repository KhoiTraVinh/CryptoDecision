-- Consecutive losses on one strategy before the bot disables itself.
--
-- Was a hardcoded 5 in the call to RiskEngine.CheckCircuitBreakers, which stops being
-- right once the exit geometry is a setting: the correct number depends on the win
-- rate that geometry implies. At a 39% win rate five losses in a row occur in 8.6% of
-- any five-trade window -- near certain across forty trades -- so a breaker tuned for a
-- 50% strategy halts a 39% one that is behaving exactly as designed. It already halted
-- this bot for fifteen hours once, on a streak that spanned a strategy rewrite.
--
-- Default stays 5. Raise it only alongside a measured reason: it is the last thing
-- between a signal that has stopped working and an account that finds out slowly.
--
-- Split into its own file rather than appended to 030. 030 had already been applied on
-- one deployment, and editing an applied migration makes this database and the next
-- disagree about what the schema is -- which is exactly what migrate.sh refused to let
-- happen, correctly, and what this file exists to undo.

ALTER TABLE bot_config
    ADD COLUMN IF NOT EXISTS max_consecutive_losses INTEGER NOT NULL DEFAULT 5;

COMMENT ON COLUMN bot_config.max_consecutive_losses IS
    'Losing trades in a row on one strategy before the bot writes enabled = false. '
    'Raise only alongside a measured reason: it is the last thing between a signal '
    'that has stopped working and an account that finds out slowly.';
