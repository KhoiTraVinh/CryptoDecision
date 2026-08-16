"""
XGBoost-based predictor. Same interface as heuristic.score().

Loaded at import time from MODEL_PATH. If the file is absent, corrupt, or the
minimum sample threshold was not met at last training, _model is None and
score() returns None so predictor.py falls back to heuristic.py transparently.
"""
from __future__ import annotations

import logging
import os

log = logging.getLogger(__name__)

MODEL_PATH = os.environ.get("MODEL_PATH", "/app/models/model.pkl")

# Class order must match train.py's LabelEncoder.fit(["DOWN", "NEUTRAL", "UP"])
_CLASSES = ["DOWN", "NEUTRAL", "UP"]
_FEATURES = ["return_24h", "volume_change", "whale_count", "volatility"]

_model = None  # XGBClassifier | None


def _try_load() -> object | None:
    """Load model from disk. Returns None on any failure."""
    if not os.path.exists(MODEL_PATH):
        log.info("Model file not found at %s — using heuristic fallback", MODEL_PATH)
        return None
    try:
        import joblib
        m = joblib.load(MODEL_PATH)
        log.info("XGBoost model loaded from %s", MODEL_PATH)
        return m
    except Exception as exc:
        log.warning("Failed to load model from %s: %s", MODEL_PATH, exc)
        return None


def reload() -> None:
    """Hot-reload model after retrain. Called by train.py after saving."""
    global _model
    _model = _try_load()


def is_loaded() -> bool:
    return _model is not None


def model_version_tag() -> str:
    return "xgboost-v1" if is_loaded() else "heuristic-v1.0"


def score(row: dict) -> tuple[str, float, str, dict] | None:
    """
    Score a feature row using the loaded XGBoost model.

    Parameters
    ----------
    row : dict
        Feature row from daily_feature_table. Required keys:
        return_24h, volume_change, whale_count, volatility.

    Returns
    -------
    (direction, confidence, rationale, signals) — same signature as heuristic.score()
    Returns None if model is not loaded; caller should fall back to heuristic.
    """
    if _model is None:
        return None

    try:
        import numpy as np

        X = [[
            float(row.get("return_24h") or 0.0),
            float(row.get("volume_change") or 0.0),
            float(row.get("whale_count") or 0.0),
            float(row.get("volatility") or 0.0),
        ]]

        proba = _model.predict_proba(X)[0]   # shape (3,) for DOWN/NEUTRAL/UP
        best_idx = int(np.argmax(proba))
        direction = _CLASSES[best_idx]
        confidence = float(np.clip(proba[best_idx], 0.35, 0.90))

        # Build rationale from top-2 feature importances
        importances = _model.feature_importances_
        top2 = sorted(zip(_FEATURES, importances), key=lambda x: x[1], reverse=True)[:2]
        rationale = (
            f"XGBoost prediction: key drivers are {top2[0][0]} "
            f"(importance {top2[0][1]:.2f}) and {top2[1][0]} "
            f"(importance {top2[1][1]:.2f})."
        )

        signals = {
            "return_24h":    round(float(row.get("return_24h") or 0.0), 4),
            "volume_change": round(float(row.get("volume_change") or 0.0), 4),
            "whale_count":   int(row.get("whale_count") or 0),
            "volatility":    round(float(row.get("volatility") or 0.0), 4),
            "proba_down":    round(float(proba[0]), 4),
            "proba_neutral": round(float(proba[1]), 4),
            "proba_up":      round(float(proba[2]), 4),
            "score":         round(confidence, 4),
            "model":         "xgboost",
        }

        return direction, round(confidence, 4), rationale, signals

    except Exception as exc:
        log.warning("XGBoost inference failed: %s — falling back to heuristic", exc)
        return None


# ── Load at import time ────────────────────────────────────────────────────────
# Worker services import this module once at startup; the load is synchronous
# but instantaneous (<1ms when file is absent, ~10ms when file is present).
_model = _try_load()
