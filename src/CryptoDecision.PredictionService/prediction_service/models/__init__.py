"""Prediction models. Each implements PredictionModel and returns a ModelResult."""
from .base import (
    CONFIDENCE_MAX,
    CONFIDENCE_MIN,
    DIRECTIONS,
    DOWN,
    NEUTRAL,
    UP,
    ModelResult,
    PredictionModel,
    clamp_confidence,
    normalize_direction,
    score_to_direction,
)
from .heuristic_model import HeuristicModel
from .llm_model import LlmModel
from .xgboost_model import XgboostModel

__all__ = [
    "CONFIDENCE_MAX",
    "CONFIDENCE_MIN",
    "DIRECTIONS",
    "DOWN",
    "NEUTRAL",
    "UP",
    "ModelResult",
    "PredictionModel",
    "clamp_confidence",
    "normalize_direction",
    "score_to_direction",
    "HeuristicModel",
    "LlmModel",
    "XgboostModel",
]
