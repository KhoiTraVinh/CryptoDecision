-- ============================================================
-- Migration 012: Ensemble prediction support
--
-- The AI module now runs an ensemble (Ollama/Qwen2.5 + XGBoost + heuristic)
-- rather than a single model. Two schema changes follow from that:
--
--   signals JSONB  — per-model breakdown for one prediction: what each model
--                    said, its weight, its latency, plus the market snapshot the
--                    verdict was made on. This is what makes a bad signal
--                    diagnosable after the fact instead of just wrong.
--
--   model_version  — now carries an ensemble composition tag such as
--                    'ensemble-heuristic+llm+xgboost'. Because the table is
--                    UNIQUE (symbol, date, model_version), a degraded run made
--                    while Ollama was down lands in its own row instead of
--                    silently overwriting the full-ensemble prediction.
--
-- Safe to re-run. Applied automatically by ProcessorService.DatabaseInitializer;
-- run manually when preserving an existing postgres_data volume.
-- ============================================================

ALTER TABLE prediction_table ADD COLUMN IF NOT EXISTS signals JSONB;

-- Direction/confidence over time per model composition — the query the dashboard
-- and any accuracy backfill both want.
CREATE INDEX IF NOT EXISTS ix_prediction_model_version
    ON prediction_table (symbol, model_version, date DESC);

-- Containment queries over the per-model breakdown, e.g. finding every prediction
-- where the LLM and XGBoost disagreed.
CREATE INDEX IF NOT EXISTS ix_prediction_signals
    ON prediction_table USING GIN (signals);
