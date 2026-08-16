-- 011: Add AI integration columns to bot_config
ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS use_ai_filter      BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS min_ai_confidence   NUMERIC(5,3) NOT NULL DEFAULT 0.500;
ALTER TABLE bot_config ADD COLUMN IF NOT EXISTS use_ai_sizing       BOOLEAN NOT NULL DEFAULT FALSE;
