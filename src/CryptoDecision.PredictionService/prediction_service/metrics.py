"""
Prometheus metrics for PredictionService.

Exposes /metrics on METRICS_PORT (default: 9091) via prometheus_client's
built-in HTTP server in a daemon thread.

Design: pull-based (no background timer), zero CPU overhead when not being scraped.
"""
from __future__ import annotations

import os
import threading

from prometheus_client import Counter, Gauge, Histogram, start_http_server

METRICS_PORT = int(os.environ.get("METRICS_PORT", "9091"))

# ── Prediction metrics ────────────────────────────────────────────────────────

predictions_total = Counter(
    "predictions_generated_total",
    "Total number of predictions generated",
    ["symbol", "direction", "model_type"],   # model_type: ensemble composition tag
)

model_confidence = Gauge(
    "model_confidence",
    "Most recent prediction confidence score (0.0–1.0)",
    ["symbol"],
)

prediction_cycle_errors = Counter(
    "prediction_cycle_errors_total",
    "Number of prediction cycles that raised an exception",
)

prediction_cycle_seconds = Histogram(
    "prediction_cycle_seconds",
    "Wall-clock duration of a full prediction cycle across all symbols",
    buckets=(0.5, 1, 2.5, 5, 10, 30, 60, 120, 300),
)

# ── LLM metrics ───────────────────────────────────────────────────────────────

llm_requests_total = Counter(
    "llm_requests_total",
    "Ollama chat requests by outcome",
    ["outcome"],   # success | failure_request | failure_parse
)

llm_latency_seconds = Histogram(
    "llm_latency_seconds",
    "End-to-end latency of an Ollama chat request",
    # A 7B on CPU commonly lands in the 10-60s range; buckets span that and beyond.
    buckets=(0.5, 1, 2.5, 5, 10, 20, 30, 45, 60, 90, 120),
)

llm_tokens_total = Counter(
    "llm_tokens_total",
    "Tokens processed by the LLM",
    ["kind"],   # prompt | completion
)

llm_available = Gauge(
    "llm_available",
    "1 when Ollama is reachable and holds the configured model, else 0",
)

# ── Ensemble metrics ──────────────────────────────────────────────────────────

ensemble_score = Gauge(
    "ensemble_score",
    "Latest ensemble signed score in [-1, +1]; positive is bullish",
    ["symbol"],
)

ensemble_agreement_total = Counter(
    "ensemble_agreement_total",
    "How often the contributing models agreed",
    ["agreement"],   # unanimous | conflict | insufficient | unanimous_but_neutral
)

model_participation_total = Counter(
    "model_participation_total",
    "Times each model contributed a result to the ensemble",
    ["model"],
)

# ── Server lifecycle ───────────────────────────────────────────────────────────

_started = False
_lock    = threading.Lock()


def start_metrics_server() -> None:
    """Start Prometheus HTTP server in a daemon thread (idempotent)."""
    global _started
    with _lock:
        if _started:
            return
        start_http_server(METRICS_PORT)
        _started = True
