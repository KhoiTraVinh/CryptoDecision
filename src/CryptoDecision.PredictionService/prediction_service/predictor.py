"""
Prediction cycle orchestration.

run_prediction_cycle():
  For each configured symbol:
    1. Build a MarketContext from daily features, order flow, history and quotes
    2. Ask the ensemble for a verdict (LLM + XGBoost + heuristic)
    3. Upsert prediction_table
    4. Publish to Kafka topic
    5. Record Prometheus metrics

There is no model branching here any more. Which models exist, what they weigh and
what happens when one is unavailable are all decisions owned by registry.py and
ensemble.py; this module only sequences the steps.
"""
from __future__ import annotations

import datetime
import time

import structlog

from .config import settings
from .context import MarketContext
from .database import (
    get_connection,
    get_exchange_quotes,
    get_feature_history,
    get_latest_features,
    get_timeframe_flows,
    upsert_prediction,
)
from .ensemble import EnsembleResult
from .kafka_producer import publish
from .metrics import (
    ensemble_agreement_total,
    ensemble_score,
    model_confidence,
    model_participation_total,
    prediction_cycle_errors,
    prediction_cycle_seconds,
    predictions_total,
)
from .registry import get_ensemble

log = structlog.get_logger(__name__)


def run_prediction_cycle() -> None:
    started = time.perf_counter()
    conn = get_connection()
    try:
        for symbol in settings.symbols:
            try:
                _predict_symbol(conn, symbol)
            except Exception:
                prediction_cycle_errors.inc()
                log.exception("predict_symbol_failed", symbol=symbol)
                # One symbol's failure must not poison the shared connection for
                # the next symbol — psycopg2 refuses further work after an error.
                try:
                    conn.rollback()
                except Exception:  # noqa: BLE001
                    pass
    finally:
        conn.close()
        prediction_cycle_seconds.observe(time.perf_counter() - started)


def _build_context(conn: object, symbol: str) -> MarketContext | None:
    """Gather every input the models need. Returns None when features are absent."""
    feature_row = get_latest_features(conn, symbol)  # type: ignore[arg-type]
    if not feature_row:
        log.warning("no_features_available", symbol=symbol)
        return None

    # Enrichment is optional: if the trades table is unavailable or empty the
    # models still get the daily row, which is what the old pipeline ran on.
    def _safe(fn, label: str, default):
        try:
            return fn()
        except Exception as exc:  # noqa: BLE001
            log.warning("context_enrichment_failed", part=label, symbol=symbol, error=str(exc))
            try:
                conn.rollback()  # type: ignore[attr-defined]
            except Exception:  # noqa: BLE001
                pass
            return default

    flows = _safe(lambda: get_timeframe_flows(conn, symbol), "flows", [])  # type: ignore[arg-type]
    history = _safe(
        lambda: get_feature_history(conn, symbol, settings.context_history_days),  # type: ignore[arg-type]
        "history",
        [],
    )
    quotes = _safe(lambda: get_exchange_quotes(conn, symbol), "quotes", [])  # type: ignore[arg-type]

    return MarketContext.from_rows(
        symbol=symbol,
        as_of=datetime.datetime.now(datetime.timezone.utc),
        feature_row=feature_row,
        flow_rows=flows,
        history_rows=history,
        quote_rows=quotes,
    )


def _predict_symbol(conn: object, symbol: str) -> None:
    ctx = _build_context(conn, symbol)
    if ctx is None:
        return

    verdict = get_ensemble().predict(ctx)
    if verdict is None:
        prediction_cycle_errors.inc()
        log.error("ensemble_returned_no_verdict", symbol=symbol)
        return

    upsert_prediction(
        conn,  # type: ignore[arg-type]
        symbol=symbol,
        date=ctx.date,
        direction=verdict.direction,
        confidence=verdict.confidence,
        model_version=verdict.model_version,
        rationale=verdict.rationale,
        signals=verdict.signals,
    )

    _record_metrics(symbol, verdict)
    _publish(ctx, verdict)

    log.info(
        "prediction_published",
        symbol=symbol,
        direction=verdict.direction,
        confidence=verdict.confidence,
        models=list(verdict.used_models),
        score=verdict.signals.get("ensemble_score"),
        agreement=verdict.signals.get("agreement"),
        date=str(ctx.date),
    )


def _record_metrics(symbol: str, verdict: EnsembleResult) -> None:
    predictions_total.labels(
        symbol=symbol.upper(),
        direction=verdict.direction,
        model_type=verdict.model_version,
    ).inc()
    model_confidence.labels(symbol=symbol.upper()).set(verdict.confidence)
    ensemble_score.labels(symbol=symbol.upper()).set(
        float(verdict.signals.get("ensemble_score") or 0.0)
    )
    ensemble_agreement_total.labels(
        agreement=str(verdict.signals.get("agreement") or "unknown")
    ).inc()
    for name in verdict.used_models:
        model_participation_total.labels(model=name).inc()


def _publish(ctx: MarketContext, verdict: EnsembleResult) -> None:
    payload = {
        "symbol":        ctx.symbol,
        "date":          str(ctx.date),
        "direction":     verdict.direction,
        "confidence":    verdict.confidence,
        "model_version": verdict.model_version,
        "rationale":     verdict.rationale,
        "signals":       verdict.signals,
        "predicted_at":  datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        ),
    }
    publish(
        topic=settings.kafka_topic(ctx.symbol),
        key=ctx.symbol.lower(),
        payload=payload,
    )
