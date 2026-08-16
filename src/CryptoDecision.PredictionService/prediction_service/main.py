"""
Entry point: python -m prediction_service.main

1. Configure structured logging
2. Start Prometheus metrics server on port 9091 (daemon thread)
3. Start health HTTP server in daemon thread
4. Warm up the Ollama model so the first prediction is not the cold one
5. Run one prediction cycle immediately
6. Schedule prediction cycle every N seconds (default 300 / 5 min)
7. Schedule XGBoost retrain every Sunday at 02:00 UTC
8. Block in run_pending loop
"""
from __future__ import annotations

import time

import schedule
import structlog
import structlog.dev

from .config import settings
from .health import start_health_server
from .metrics import start_metrics_server
from .predictor import run_prediction_cycle
from .registry import warmup_llm


def _configure_logging() -> None:
    structlog.configure(
        processors=[
            structlog.stdlib.add_log_level,
            structlog.processors.TimeStamper(fmt="iso"),
            structlog.processors.StackInfoRenderer(),
            structlog.processors.format_exc_info,
            structlog.processors.JSONRenderer(),
        ],
        wrapper_class=structlog.make_filtering_bound_logger(20),  # INFO
        context_class=dict,
        logger_factory=structlog.PrintLoggerFactory(),
    )


def main() -> None:
    _configure_logging()
    log = structlog.get_logger(__name__)

    log.info(
        "prediction_service_starting",
        symbols=settings.symbols,
        interval_seconds=settings.prediction_interval_seconds,
        llm_enabled=settings.ollama_enabled,
        llm_model=settings.ollama_model if settings.ollama_enabled else None,
        ensemble_weights=settings.ensemble_weights(),
    )

    # Prometheus metrics on port 9091 — pull-based, daemon thread
    start_metrics_server()
    log.info("metrics_server_started", port=9091)

    start_health_server()
    log.info("health_server_started", port=settings.health_port)

    # Pull the 7B into Ollama's memory before the first real prediction, so the
    # cold-start load cost is paid here rather than inside a scheduled cycle.
    # Non-fatal: a failure here only means the first prediction is slower.
    if settings.ollama_enabled and settings.ollama_warmup:
        try:
            warmup_llm()
        except Exception:
            log.exception("llm_warmup_failed")

    # Run once at startup so predictions are available immediately
    try:
        run_prediction_cycle()
    except Exception:
        log.exception("startup_cycle_failed")

    # Prediction every N seconds
    schedule.every(settings.prediction_interval_seconds).seconds.do(
        _safe_run, log=log
    )

    # Weekly retrain every Sunday at 02:00 UTC
    # Runs synchronously in the main thread — training takes ~1-2s for 180 rows,
    # so there is no need for a separate thread and zero RAM overhead at idle.
    schedule.every().sunday.at("02:00").do(_safe_retrain, log=log)

    log.info(
        "scheduler_started",
        interval_seconds=settings.prediction_interval_seconds,
        retrain_schedule="every Sunday 02:00 UTC",
    )

    while True:
        schedule.run_pending()
        time.sleep(10)


def _safe_run(log: object) -> None:
    try:
        run_prediction_cycle()
    except Exception:
        structlog.get_logger(__name__).exception("prediction_cycle_failed")


def _safe_retrain(log: object) -> None:
    try:
        structlog.get_logger(__name__).info("weekly_retrain_starting")
        from .train import train
        train()
        structlog.get_logger(__name__).info("weekly_retrain_complete")
    except Exception:
        structlog.get_logger(__name__).exception("weekly_retrain_failed")


if __name__ == "__main__":
    main()
