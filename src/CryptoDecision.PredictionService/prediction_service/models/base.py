"""
Model provider contract.

Every prediction model (heuristic, XGBoost, LLM) implements PredictionModel and
returns a ModelResult. predictor.py never branches on model type — it asks the
ensemble for a verdict and the ensemble asks each registered model in turn.

Adding a model = one new class + one registration. No existing code changes (OCP).
"""
from __future__ import annotations

import abc
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

if TYPE_CHECKING:  # pragma: no cover - typing only, avoids a runtime import cycle
    from ..context import MarketContext

# Canonical direction vocabulary. prediction_table has a CHECK constraint on these.
UP = "UP"
DOWN = "DOWN"
NEUTRAL = "NEUTRAL"
DIRECTIONS = (UP, DOWN, NEUTRAL)

# Confidence is clamped to this band everywhere. A model claiming 0.99 certainty
# about a 24h crypto move is lying; a model claiming 0.0 is useless downstream.
CONFIDENCE_MIN = 0.35
CONFIDENCE_MAX = 0.90


@dataclass(frozen=True)
class ModelResult:
    """One model's verdict on one symbol."""

    direction: str
    confidence: float
    rationale: str
    signals: dict = field(default_factory=dict)
    model_name: str = "unknown"
    model_version: str = "v0"
    latency_ms: float = 0.0
    #: True when this result came from a fallback path rather than the intended
    #: model (e.g. Ollama unreachable). The ensemble down-weights degraded results.
    degraded: bool = False

    @property
    def signed_score(self) -> float:
        """
        Direction + confidence collapsed into a single signed score in [-1, +1].

        UP      → +confidence
        DOWN    → -confidence
        NEUTRAL →  0.0

        This is the only representation the ensemble arithmetic uses, which keeps
        weighted averaging meaningful across models that disagree.
        """
        if self.direction == UP:
            return self.confidence
        if self.direction == DOWN:
            return -self.confidence
        return 0.0


def clamp_confidence(value: float) -> float:
    """Clamp to [CONFIDENCE_MIN, CONFIDENCE_MAX], coercing NaN to the midpoint."""
    try:
        v = float(value)
    except (TypeError, ValueError):
        return 0.5
    if v != v:  # NaN — the only value that is not equal to itself
        return 0.5
    return max(CONFIDENCE_MIN, min(CONFIDENCE_MAX, v))


def normalize_direction(value: object) -> str:
    """Coerce arbitrary model output to a valid direction, defaulting to NEUTRAL."""
    text = str(value or "").strip().upper()
    if text in DIRECTIONS:
        return text
    # Tolerate common LLM phrasings rather than discarding an otherwise good answer.
    if text in ("BULLISH", "LONG", "BUY", "POSITIVE", "RISE"):
        return UP
    if text in ("BEARISH", "SHORT", "SELL", "NEGATIVE", "FALL"):
        return DOWN
    return NEUTRAL


def score_to_direction(signed: float, dead_zone: float) -> tuple[str, float]:
    """
    Inverse of ModelResult.signed_score.

    Returns (direction, confidence). Scores inside +/- dead_zone are NEUTRAL —
    without a dead zone an ensemble that nets to +0.01 would emit a confident UP.
    """
    magnitude = abs(signed)
    if magnitude < dead_zone:
        # Confidence in a NEUTRAL call rises as the score approaches zero.
        return NEUTRAL, clamp_confidence(0.5 - magnitude * 0.5)
    return (UP if signed > 0 else DOWN), clamp_confidence(magnitude)


class PredictionModel(abc.ABC):
    """Base class for all prediction models."""

    #: Stable identifier used in metrics labels and ensemble weight lookup.
    name: str = "base"

    @property
    def version(self) -> str:
        """Version tag written to prediction_table.model_version."""
        return "v0"

    def is_available(self) -> bool:
        """
        Whether this model can serve a prediction right now.

        Checked before every call so a model whose backing resource disappeared
        (missing model.pkl, Ollama container down) is skipped rather than raising.
        """
        return True

    @abc.abstractmethod
    def predict(self, ctx: "MarketContext") -> ModelResult | None:
        """
        Score the given market context.

        Returns None to abstain — the ensemble then proceeds without this model
        instead of failing the cycle. Implementations must not raise; catch
        internally and return None.
        """
        raise NotImplementedError
