"""
Adapter exposing the rule-based scorer through the PredictionModel contract.

The scoring logic itself stays in prediction_service/heuristic.py — it is well
tested by use, has no dependencies, and train.py has no reason to care that a
model wrapper now exists. This file only translates its tuple return into a
ModelResult.

This model is also the designated last-resort fallback: it needs nothing beyond
the daily feature row, so it is always available. If Ollama is down and no
model.pkl has been trained, the pipeline still produces a prediction.
"""
from __future__ import annotations

import time

import structlog

from ..context import MarketContext
from ..heuristic import score as heuristic_score
from .base import PredictionModel, ModelResult, clamp_confidence, normalize_direction

log = structlog.get_logger(__name__)


class HeuristicModel(PredictionModel):
    name = "heuristic"

    def __init__(self, version: str = "heuristic-v2.0") -> None:
        self._version = version

    @property
    def version(self) -> str:
        return self._version

    def is_available(self) -> bool:
        # Pure arithmetic over the feature row — nothing to be unavailable.
        return True

    def predict(self, ctx: MarketContext) -> ModelResult | None:
        started = time.perf_counter()
        try:
            direction, confidence, rationale, signals = heuristic_score(ctx.feature_row())
        except Exception as exc:  # noqa: BLE001 - models must not raise
            log.warning("heuristic_scoring_failed", symbol=ctx.symbol, error=str(exc))
            return None

        latency_ms = (time.perf_counter() - started) * 1000.0

        return ModelResult(
            direction=normalize_direction(direction),
            confidence=clamp_confidence(confidence),
            rationale=rationale,
            signals={**signals, "model": "heuristic"},
            model_name=self.name,
            model_version=self._version,
            latency_ms=latency_ms,
        )
