"""
Adapter exposing the XGBoost scorer through the PredictionModel contract.

The loading, inference and hot-reload logic stays in prediction_service/ml_model.py
so train.py's `ml_model.reload()` call after a retrain keeps working untouched.
"""
from __future__ import annotations

import time

import structlog

from .. import ml_model
from ..context import MarketContext
from .base import PredictionModel, ModelResult, clamp_confidence, normalize_direction

log = structlog.get_logger(__name__)


class XgboostModel(PredictionModel):
    name = "xgboost"

    @property
    def version(self) -> str:
        return ml_model.model_version_tag()

    def is_available(self) -> bool:
        """False until a model.pkl has been trained and loaded."""
        return ml_model.is_loaded()

    def predict(self, ctx: MarketContext) -> ModelResult | None:
        if not self.is_available():
            return None

        started = time.perf_counter()
        # ml_model.score() already returns None on any inference failure.
        result = ml_model.score(ctx.feature_row())
        if result is None:
            return None

        direction, confidence, rationale, signals = result
        latency_ms = (time.perf_counter() - started) * 1000.0

        return ModelResult(
            direction=normalize_direction(direction),
            confidence=clamp_confidence(confidence),
            rationale=rationale,
            signals=signals,
            model_name=self.name,
            model_version=ml_model.model_version_tag(),
            latency_ms=latency_ms,
        )
