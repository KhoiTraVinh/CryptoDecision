-- ============================================================
-- Migration 013: Autonomous AI agent toggle
--
-- When use_ai_agent is true, the entry decision is delegated to the LLM agent
-- (Ollama/Qwen 2.5) driving a bounded tool set, instead of the deterministic
-- MomentumStrategy.
--
-- What the agent does NOT control, by design:
--   * Position sizing   — decided by the risk engine, never a model argument
--   * Stop loss / take profit / trailing / breakeven — evaluated deterministically
--     every cycle in TradingBotService, so an exit never waits on 40s+ inference
--   * Risk limits       — RiskEngine can refuse any order regardless of how
--                         confident the model is
--
-- Defaults to FALSE. Giving a language model discretion over when capital is
-- committed should be an explicit operator decision, not an inherited default.
--
-- Safe to re-run.
-- ============================================================

ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS use_ai_agent BOOLEAN NOT NULL DEFAULT FALSE;

-- Agent-opened positions are tagged AI_AGENT in bot_trades.strategy, so P&L can be
-- attributed to the agent versus the deterministic strategy.
CREATE INDEX IF NOT EXISTS ix_bot_trades_strategy_opened
    ON bot_trades (strategy, opened_at DESC);
